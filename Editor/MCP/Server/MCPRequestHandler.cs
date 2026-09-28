// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KitWright.Editor.Services;
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
        private readonly string _spillDirectory;

        // Claude Code refuses a tool result over MAX_MCP_OUTPUT_TOKENS (25K tokens by default,
        // ~100 KB), and a refused result is a failed call. 64K chars is ~16K tokens, which leaves
        // the rest of the response room under the default cap.
        internal const int MaxInlineTextChars = 64 * 1024;
        internal const int SpillPreviewChars = 4 * 1024;
        internal const int SpilledOutputsKept = 20;
        private const string SpillDirRelative = "Library/KitWrightMcp/Outputs";

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
            // Resolved here, on the editor thread: a spill can run on a request thread, and Unity's
            // API is only guaranteed on the editor thread.
            _spillDirectory = Path.Combine(ApplicationPaths.ProjectRoot, SpillDirRelative);
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
            MCPToolListChangeNotifier.Observe(request.SessionId);

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

                var content = BuildContentFromResult(result);
                var spilled = SpillOversizedText(content, toolName, _spillDirectory);
                var callResult = new Dictionary<string, object>
                {
                    ["content"] = content
                };
                if (TryParseEnvelope(result, out var envelope, out var isError))
                {
                    // Version strings are ISO dates, so ordinal compare is a revision compare.
                    // structuredContent is the same payload again, so a spilled result drops it.
                    if (!spilled && string.CompareOrdinal(NegotiatedFor(request), ProtocolVersion) >= 0)
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

        // Only text spills: an image block is sized by the capture tools and is not read as tokens.
        internal static bool SpillOversizedText(List<Dictionary<string, object>> content, string toolName, string directory)
        {
            var spilled = false;
            foreach (var block in content)
            {
                if (!block.TryGetValue("type", out var type) || !"text".Equals(type) ||
                    !block.TryGetValue("text", out var value) || !(value is string text) ||
                    text.Length <= MaxInlineTextChars)
                    continue;

                string path;
                try
                {
                    path = WriteSpillFile(text, toolName, directory);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Inline is what the call returned before spilling existed: a client may still
                    // refuse it, but that beats losing the result to a disk error.
                    Debug.LogWarning($"[KitWright MCP Server] Could not spill the {text.Length}-char result of {toolName}: {ex.Message}");
                    continue;
                }

                var preview = char.IsHighSurrogate(text[SpillPreviewChars - 1]) ? SpillPreviewChars - 1 : SpillPreviewChars;
                block["text"] =
                    $"This result is {text.Length} characters, over the {MaxInlineTextChars}-character inline limit, so all of it was written to {path}. " +
                    "Read that file with your own file tools (read_file returns only the start of a long file), " +
                    "or repeat the call with the tool's paging or max_* parameters for a smaller answer. " +
                    $"The first {preview} characters follow.\n\n" + text.Substring(0, preview);
                spilled = true;
            }

            return spilled;
        }

        private static string WriteSpillFile(string text, string toolName, string directory)
        {
            Directory.CreateDirectory(directory);
            var safeName = Regex.Replace(toolName ?? string.Empty, "[^A-Za-z0-9_-]", "_");
            var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{safeName}.txt");
            File.WriteAllText(path, text);

            // The timestamp prefix makes name order creation order.
            foreach (var stale in Directory.GetFiles(directory, "*.txt")
                         .OrderByDescending(file => file, StringComparer.Ordinal)
                         .Skip(SpilledOutputsKept))
            {
                try
                {
                    File.Delete(stale);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // A reader still holds it open; the next spill deletes it.
                }
            }

            return path;
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

        private const string ImageDataUriPrefix = "data:image/";
        private const string Base64Marker = ";base64,";

        // A capture run inside batch_execute is not a bare data URI but a string value in its JSON,
        // which the client would otherwise receive as hundreds of KB of base64 text. The lookbehind
        // leaves a URI quoted inside another string alone. Base64 carries no quote or backslash, so
        // the closing quote is the end of the value.
        private static readonly System.Text.RegularExpressions.Regex EmbeddedImageDataUri =
            new System.Text.RegularExpressions.Regex(
                "(?<!\\\\)\"data:(image/[A-Za-z0-9.+-]+);base64,([A-Za-z0-9+/=]*)\"",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        internal static List<Dictionary<string, object>> BuildContentFromResult(string result)
        {
            var content = new List<Dictionary<string, object>>();

            // Any base64 image, not only PNG: a screenshot that crossed a wire arrives JPEG-encoded
            // because a downscaled PNG is still an order of magnitude larger, and pinning the prefix to
            // one format silently turned those into half a megabyte of base64 text in the transcript.
            var marker = result != null && result.StartsWith(ImageDataUriPrefix, StringComparison.Ordinal)
                ? result.IndexOf(Base64Marker, StringComparison.Ordinal)
                : -1;

            if (marker > 0)
            {
                content.Add(ImageBlock(
                    result.Substring("data:".Length, marker - "data:".Length),
                    result.Substring(marker + Base64Marker.Length)));
                content.Add(new Dictionary<string, object>
                {
                    ["type"] = "text", ["text"] = "Screenshot captured successfully."
                });
            }
            else
            {
                var images = new List<Dictionary<string, object>>();
                content.Add(new Dictionary<string, object>
                {
                    ["type"] = "text", ["text"] = LiftEmbeddedImages(result, images)
                });
                content.AddRange(images);
            }

            return content;
        }

        // Each embedded image becomes {"image_index": N}, N counting this response's image blocks from 0.
        // structuredContent goes through the same pass, so the two agree on N.
        internal static string LiftEmbeddedImages(string result, List<Dictionary<string, object>> images)
        {
            if (result == null || result.IndexOf("\"" + ImageDataUriPrefix, StringComparison.Ordinal) < 0)
                return result;

            var index = 0;
            return EmbeddedImageDataUri.Replace(result, match =>
            {
                images?.Add(ImageBlock(match.Groups[1].Value, match.Groups[2].Value));
                return "{\"image_index\":" + index++ + "}";
            });
        }

        private static Dictionary<string, object> ImageBlock(string mimeType, string base64Data) =>
            new Dictionary<string, object>
            {
                ["type"] = "image",
                ["data"] = base64Data,
                ["mimeType"] = mimeType
            };

        // Only the {success, ...} envelope is promoted to structuredContent, so free-form JSON
        // (or JSON-looking text) from a tool never lands there unvalidated.
        internal static bool TryParseEnvelope(string result, out object envelope, out bool isError)
        {
            envelope = null;
            isError = false;
            result = LiftEmbeddedImages(result, null);

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
