// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KitWright.Editor.Settings;
using UnityEngine;

namespace KitWright.Editor.MCP.Server
{
    /// <summary>
    /// Handles MCP protocol requests (initialize, tools/list, tools/call, etc.)
    /// </summary>
    internal class MCPRequestHandler
    {
        // structuredContent only exists from this revision on.
        internal const string ProtocolVersion = "2025-06-18";

        private static readonly string[] SupportedProtocolVersions = { "2024-11-05", "2025-03-26", ProtocolVersion };

        // A client that gets back a version it does not speak is expected to drop the connection,
        // so echo what it asked for whenever we can serve it.
        internal static string NegotiateProtocolVersion(string requested) =>
            Array.IndexOf(SupportedProtocolVersions, requested) >= 0 ? requested : ProtocolVersion;

        private readonly MCPToolExporter _toolExporter;
        private readonly MCPExecutionBridge _executionBridge;
        private readonly MCPResourceProvider _resourceProvider;
        private readonly MCPPromptProvider _promptProvider;
        private readonly string _serverName;
        private readonly string _serverVersion;
        private readonly string _projectIdentity;

        // structuredContent only exists from 2025-06-18 on, so a client that negotiated an older
        // revision must not receive it. Kept per session: one handler serves every client, and a
        // single old client must not silently strip the field from everyone else's results. Requests
        // with no session id (plain HTTP POST) share the sessionless slot.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _negotiatedBySession
            = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        private static string SessionKey(MCPRequest request) =>
            string.IsNullOrEmpty(request?.SessionId) ? string.Empty : request.SessionId;

        private string NegotiatedFor(MCPRequest request) =>
            _negotiatedBySession.TryGetValue(SessionKey(request), out var version) ? version : ProtocolVersion;

        public MCPRequestHandler(
            MCPToolExporter toolExporter,
            MCPExecutionBridge executionBridge,
            MCPResourceProvider resourceProvider,
            MCPPromptProvider promptProvider,
            string serverName,
            string serverVersion,
            string projectIdentity)
        {
            _toolExporter = toolExporter ?? throw new ArgumentNullException(nameof(toolExporter));
            _executionBridge = executionBridge ?? throw new ArgumentNullException(nameof(executionBridge));
            _resourceProvider = resourceProvider ?? throw new ArgumentNullException(nameof(resourceProvider));
            _promptProvider = promptProvider ?? throw new ArgumentNullException(nameof(promptProvider));
            _serverName = string.IsNullOrWhiteSpace(serverName) ? "KitWright MCP Server" : serverName;
            _serverVersion = string.IsNullOrWhiteSpace(serverVersion) ? "0.0.0" : serverVersion;
            _projectIdentity = projectIdentity ?? string.Empty;
        }

        public async Task<MCPResponse> HandleRequestAsync(MCPRequest request, CancellationToken ct)
        {
            try
            {
                if (request == null)
                    return CreateErrorResponse(null, -32600, "Invalid Request");

                if (request.JsonRpc != "2.0")
                    return CreateErrorResponse(request.Id, -32600, "Invalid Request: jsonrpc must be '2.0'");

                if (ShouldLogRequest(request.Method))
                    PluginDebugLogger.Log($"[KitWright MCP Server] Handling request: {request.Method}");

                return request.Method switch
                {
                    "initialize" => HandleInitialize(request),
                    // Spec MUST: a ping is answered with an empty result, never -32601.
                    "ping" => new MCPResponse { Id = request.Id, Result = new Dictionary<string, object>() },
                    "notifications/initialized" => null,
                    "notifications/cancelled" => null,
                    "logging/setLevel" => HandleLoggingSetLevel(request),
                    "tools/list" => HandleToolsList(request),
                    "tools/call" => await HandleToolsCallAsync(request, ct),
                    "prompts/list" => HandlePromptsList(request),
                    "prompts/get" => HandlePromptsGet(request),
                    "resources/list" => HandleResourcesList(request),
                    "resources/read" => HandleResourcesRead(request),
                    "resources/templates/list" => HandleResourceTemplatesList(request),
                    _ when request.Method != null && request.Method.StartsWith("notifications/") => null,
                    _ => CreateErrorResponse(request.Id, -32601, $"Method not found: {request.Method}")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KitWright MCP Server] Error handling request: {ex.Message}\n{ex.StackTrace}");
                return CreateErrorResponse(request?.Id, -32603, $"Internal error: {ex.Message}");
            }
        }

        private MCPResponse HandleLoggingSetLevel(MCPRequest request)
        {
            if (request.Params != null && request.Params.TryGetValue("level", out var levelObj) && levelObj is string levelName)
            {
                SSE.SSESessionManager.Instance.SetLoggingLevel(request.SessionId, levelName);
                return new MCPResponse { Id = request.Id, Result = new Dictionary<string, object>() };
            }

            return CreateErrorResponse(request.Id, -32602, "Invalid params: 'level' is required");
        }

        private MCPResponse HandleInitialize(MCPRequest request)
        {
            var requested = request.Params != null && request.Params.TryGetValue("protocolVersion", out var versionObj)
                ? versionObj as string
                : null;

            var negotiated = NegotiateProtocolVersion(requested);
            _negotiatedBySession[SessionKey(request)] = negotiated;

            var result = new Dictionary<string, object>
            {
                ["protocolVersion"] = negotiated,
                ["serverInfo"] = new Dictionary<string, object>
                {
                    ["name"] = _serverName,
                    ["version"] = _serverVersion
                },
                ["kitwright"] = new Dictionary<string, object>
                {
                    ["projectIdentity"] = _projectIdentity,
                    ["projectIdentityVersion"] = ProjectIdentity.IdentityVersion
                },
                ["capabilities"] = new Dictionary<string, object>
                {
                    // listChanged: the server piggybacks notifications/tools/list_changed
                    // onto the next POST response (SSE) after the exposed tool set changes.
                    ["tools"] = new Dictionary<string, object> { ["listChanged"] = true },
                    ["resources"] = new Dictionary<string, object>(),
                    ["prompts"] = new Dictionary<string, object>(),
                    ["logging"] = new Dictionary<string, object>()
                }
            };

            PluginDebugLogger.Log("[KitWright MCP Server] Initialized successfully");
            return new MCPResponse { Id = request.Id, Result = result };
        }

        private MCPResponse HandleToolsList(MCPRequest request)
        {
            var tools = _toolExporter.ExportTools();
            PluginDebugLogger.Log($"[KitWright MCP Server] Returning {tools.Count} tools");

            return new MCPResponse
            {
                Id = request.Id,
                Result = new Dictionary<string, object> { ["tools"] = tools }
            };
        }

        private async Task<MCPResponse> HandleToolsCallAsync(MCPRequest request, CancellationToken ct)
        {
            try
            {
                if (!request.Params.TryGetValue("name", out var nameObj) || !(nameObj is string toolName))
                    return CreateErrorResponse(request.Id, -32602, "Invalid params: 'name' is required");

                var arguments = request.Params.ContainsKey("arguments") && request.Params["arguments"] is Dictionary<string, object> args
                    ? args
                    : new Dictionary<string, object>();

                PluginDebugLogger.Log($"[KitWright MCP Server] Calling tool: {toolName}");
                var result = await _executionBridge.ExecuteToolAsync(toolName, arguments, ct);

                var callResult = new Dictionary<string, object>
                {
                    ["content"] = BuildContentFromResult(result)
                };
                if (TryParseEnvelope(result, out var envelope, out var isError))
                {
                    // Version strings are ISO dates, so ordinal compare is a revision compare.
                    if (string.CompareOrdinal(NegotiatedFor(request), ProtocolVersion) >= 0)
                        callResult["structuredContent"] = envelope;
                    if (isError)
                        callResult["isError"] = true;
                }

                return new MCPResponse
                {
                    Id = request.Id,
                    Result = callResult
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KitWright MCP Server] Error executing tool: {ex.Message}");
                return CreateErrorResponse(request.Id, -32603, $"Tool execution failed: {ex.Message}");
            }
        }

        private MCPResponse HandlePromptsList(MCPRequest request)
        {
            return new MCPResponse
            {
                Id = request.Id,
                Result = new Dictionary<string, object>
                {
                    ["prompts"] = _promptProvider.ListPrompts()
                }
            };
        }

        private MCPResponse HandlePromptsGet(MCPRequest request)
        {
            if (request.Params == null ||
                !request.Params.TryGetValue("name", out var nameObj) ||
                !(nameObj is string promptName) ||
                string.IsNullOrWhiteSpace(promptName))
            {
                return CreateErrorResponse(request.Id, -32602, "Invalid params: 'name' is required");
            }

            var arguments = request.Params.ContainsKey("arguments") && request.Params["arguments"] is Dictionary<string, object> args
                ? args
                : new Dictionary<string, object>();

            return new MCPResponse
            {
                Id = request.Id,
                Result = _promptProvider.GetPrompt(promptName, arguments)
            };
        }

        private MCPResponse HandleResourcesList(MCPRequest request)
        {
            return new MCPResponse
            {
                Id = request.Id,
                Result = new Dictionary<string, object>
                {
                    ["resources"] = _resourceProvider.ListResources()
                }
            };
        }

        private MCPResponse HandleResourcesRead(MCPRequest request)
        {
            if (request.Params == null ||
                !request.Params.TryGetValue("uri", out var uriObj) ||
                !(uriObj is string uri) ||
                string.IsNullOrWhiteSpace(uri))
            {
                return CreateErrorResponse(request.Id, -32602, "Invalid params: 'uri' is required");
            }

            return new MCPResponse
            {
                Id = request.Id,
                Result = _resourceProvider.ReadResource(uri)
            };
        }

        private MCPResponse HandleResourceTemplatesList(MCPRequest request)
        {
            return new MCPResponse
            {
                Id = request.Id,
                Result = new Dictionary<string, object>
                {
                    ["resourceTemplates"] = _resourceProvider.ListResourceTemplates()
                }
            };
        }

        private const string ImageDataUriPrefix = "data:image/png;base64,";

        private List<Dictionary<string, object>> BuildContentFromResult(string result)
        {
            var content = new List<Dictionary<string, object>>();

            if (result != null && result.StartsWith(ImageDataUriPrefix))
            {
                var base64Data = result.Substring(ImageDataUriPrefix.Length);
                content.Add(new Dictionary<string, object>
                {
                    ["type"] = "image", ["data"] = base64Data, ["mimeType"] = "image/png"
                });
                content.Add(new Dictionary<string, object>
                {
                    ["type"] = "text", ["text"] = "Screenshot captured successfully."
                });
            }
            else
            {
                content.Add(new Dictionary<string, object>
                {
                    ["type"] = "text", ["text"] = result
                });
            }

            return content;
        }

        // Only the {success, ...} envelope is promoted to structuredContent, so free-form JSON
        // (or JSON-looking text) from a tool never lands there unvalidated.
        internal static bool TryParseEnvelope(string result, out object envelope, out bool isError)
        {
            envelope = null;
            isError = false;

            if (string.IsNullOrEmpty(result) || result[0] != '{')
                return false;

            try
            {
                var parsed = Newtonsoft.Json.Linq.JObject.Parse(result);
                if (parsed["success"]?.Type != Newtonsoft.Json.Linq.JTokenType.Boolean)
                    return false;

                // JsonCodec only understands plain dictionaries/lists, not JTokens.
                envelope = ToPlainObject(parsed);
                isError = !parsed.Value<bool>("success");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object ToPlainObject(Newtonsoft.Json.Linq.JToken token)
        {
            switch (token.Type)
            {
                case Newtonsoft.Json.Linq.JTokenType.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var property in ((Newtonsoft.Json.Linq.JObject)token).Properties())
                        dict[property.Name] = ToPlainObject(property.Value);
                    return dict;
                case Newtonsoft.Json.Linq.JTokenType.Array:
                    var list = new List<object>();
                    foreach (var item in (Newtonsoft.Json.Linq.JArray)token)
                        list.Add(ToPlainObject(item));
                    return list;
                case Newtonsoft.Json.Linq.JTokenType.Integer:
                    return token.ToObject<long>();
                case Newtonsoft.Json.Linq.JTokenType.Float:
                    return token.ToObject<double>();
                case Newtonsoft.Json.Linq.JTokenType.Boolean:
                    return token.ToObject<bool>();
                case Newtonsoft.Json.Linq.JTokenType.Null:
                    return null;
                default:
                    return token.ToString();
            }
        }

        private MCPResponse CreateErrorResponse(object requestId, int code, string message)
        {
            return new MCPResponse
            {
                Id = requestId,
                Error = new MCPError { Code = code, Message = message }
            };
        }

        private static bool ShouldLogRequest(string method)
        {
            switch (method)
            {
                case null:
                case "initialize":
                case "ping":
                case "notifications/initialized":
                case "notifications/cancelled":
                case "resources/list":
                case "resources/read":
                case "resources/templates/list":
                case "tools/list":
                case "prompts/list":
                    return false;
                default:
                    return !method.StartsWith("notifications/", StringComparison.Ordinal);
            }
        }
    }
}
