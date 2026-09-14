// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KitWright.Editor.Settings;
using KitWright.Editor.State;
using KitWright.Editor.Threading;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Helpers;
using UnityEngine;

namespace KitWright.Editor.MCP.Server
{
    /// <summary>
    /// Bridges MCP tool calls to KitWright's FunctionInvoker.
    /// Handles thread marshalling and approval workflow.
    /// </summary>
    internal class MCPExecutionBridge
    {
        private readonly EditorThreadHelper _threadHelper;
        private readonly SettingsController _settings;
        private readonly StateController _stateController;
        private readonly FunctionInvoker _invoker;
        private readonly MCPInteractionLog _interactionLog;

        public MCPExecutionBridge(
            EditorThreadHelper threadHelper,
            SettingsController settings,
            StateController stateController,
            FunctionInvoker invoker,
            MCPInteractionLog interactionLog)
        {
            _threadHelper = threadHelper ?? throw new ArgumentNullException(nameof(threadHelper));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _stateController = stateController ?? throw new ArgumentNullException(nameof(stateController));
            _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
            _interactionLog = interactionLog;
        }

        public async Task<string> ExecuteToolAsync(
            string toolName,
            Dictionary<string, object> arguments,
            CancellationToken ct)
        {
            // Read before the branch: the off-editor-thread path skips the body below, and a tool the
            // profile withholds must not be reachable just because it answers from another thread.
            // Every call here is a locked in-memory read or a cached lookup, so it is safe off-thread.
            var profile = MCPToolExportPolicy.Parse(_settings.MCPToolExportProfile);
            var profileKey = MCPToolExportPolicy.ToSettingValue(profile);
            var isAllowed = MCPToolExportPolicy.IsToolAllowed(
                toolName,
                profile,
                _settings.IsProfileConfigured(profileKey),
                _settings.GetProfileTools(profileKey));

            if (ToolRegistry.RunsOffEditorThread(toolName))
            {
                // No interaction log: it writes SessionState, which is main-thread only.
                return isAllowed
                    ? await InvokeOffEditorThreadAsync(toolName, arguments)
                    : ToolResultFormatter.Error("TOOL_NOT_EXPOSED", new { tool = toolName, profile = profileKey });
            }

            return await _threadHelper.ExecuteAsyncOnEditorThreadAsync(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var functionCall = new FunctionCall
                    {
                        FunctionName = toolName
                    };

                    foreach (var kvp in arguments)
                        functionCall.Parameters[kvp.Key] = ConvertArgumentToString(kvp.Value);

                    ToolRegistry.ManualTools.TryGetValue(toolName, out var manualTool);
                    var method = ToolRegistry.GetMethod(toolName);
                    if (method == null && manualTool == null)
                    {
                        var error = ToolResultFormatter.Error("UNKNOWN_TOOL", new { tool = toolName });
                        _interactionLog?.Add(toolName, MCPToolCallStatus.Error, error);
                        return error;
                    }

                    if (!isAllowed)
                    {
                        var error = ToolResultFormatter.Error("TOOL_NOT_EXPOSED", new
                        {
                            tool = toolName,
                            profile = profileKey
                        });
                        _interactionLog?.Add(toolName, MCPToolCallStatus.Error, error);
                        return error;
                    }

                    DomainReloadHandler.ResetResumeCounter();
                    _stateController.SetState(KitWrightState.ExecutingFunction);
                    DomainReloadHandler.SavePendingFunction(functionCall);

                    PluginDebugLogger.Log($"[KitWright MCP Server] Executing tool: {toolName}");
                    var result = await _invoker.InvokeAsync(functionCall);
                    DomainReloadHandler.CompletePendingFunction(_stateController);

                    var resultText = result ?? "Completed successfully";
                    _interactionLog?.Add(toolName,
                        ToolResultFormatter.IsError(resultText) ? MCPToolCallStatus.Error : MCPToolCallStatus.Success,
                        resultText);
                    return resultText;
                }
                catch (Exception ex)
                {
                    DomainReloadHandler.ClearPendingFunction();
                    _stateController.ClearState();
                    var exError = ToolResultFormatter.Error("TOOL_EXCEPTION",
                        new { tool = toolName, message = ex.Message });
                    Debug.LogError($"[KitWright MCP Server] Error executing tool '{toolName}': {ex.Message}\n{ex.StackTrace}");
                    _interactionLog?.Add(toolName, MCPToolCallStatus.Error, exError);
                    return exError;
                }
            }, ct);
        }

        // No state bookkeeping, no domain-reload record, no interaction log: all of that writes
        // SessionState, which is main-thread only, and this path exists precisely for when the
        // main thread is stuck inside a modal.
        private async Task<string> InvokeOffEditorThreadAsync(string toolName, Dictionary<string, object> arguments)
        {
            var functionCall = new FunctionCall
            {
                FunctionName = toolName,
                Parameters = System.Linq.Enumerable.ToDictionary(arguments, kvp => kvp.Key, kvp => ConvertArgumentToString(kvp.Value))
            };

            try
            {
                return await _invoker.InvokeAsync(functionCall) ?? "Completed successfully";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("TOOL_EXCEPTION", new { tool = toolName, message = ex.Message });
            }
        }

        internal static string ConvertArgumentToString(object value)
        {
            if (value == null) return string.Empty;
            if (value is string strValue) return strValue;
            if (value is bool boolValue) return boolValue ? "true" : "false";
            // FunctionInvoker parses back with InvariantCulture, where a comma-decimal locale's "1,5"
            // reads as the group-separated 15 instead of failing.
            if (value is int || value is long || value is float || value is double)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (value is Dictionary<string, object> dict) return JsonCodec.Serialize(dict);
            if (value is System.Collections.IList list)
            {
                var items = new List<object>();
                foreach (var item in list) items.Add(item);
                return JsonCodec.Serialize(items);
            }
            return value.ToString();
        }
    }
}
