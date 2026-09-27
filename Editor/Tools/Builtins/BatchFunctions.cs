// Copyright (C) KitWright. Licensed under MIT.
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.DI;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Settings;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Batch")]
    internal static class BatchFunctions
    {
        [Description("Run multiple MCP tool calls sequentially in a single request, on the main thread, saving round-trips. " +
                     "Pass a JSON array of {\"name\": \"<tool_name>\", \"params\": {..}} objects. Each result is returned in order. " +
                     "By default a failing call stops the batch; set stop_on_error=false to continue past failures. " +
                     "A capture in the batch comes back as an image block of the response, with {\"image_index\": N} " +
                     "standing in for it in results (N counts the response's images from 0). " +
                     "The scene changes the whole batch makes collapse into a single Undo step, so the user can revert " +
                     "the batch with one Ctrl+Z instead of one per command. File writes, asset imports and play-mode " +
                     "changes are outside Unity's undo system and are not reverted by it.")]
        public static async Task<object> BatchExecute(
            [ToolParam("JSON array of commands, e.g. [{\"name\":\"create_primitive\",\"params\":{\"primitive_type\":\"Cube\"}},{\"name\":\"get_hierarchy\",\"params\":{}}]")] string commands,
            [ToolParam("Stop the batch when a call fails (default true). If false, remaining calls still run.", Required = false)] bool stop_on_error = true,
            [ToolParam("Name shown in Unity's Edit > Undo menu for the collapsed step.", Required = false)] string undo_label = null)
        {
            JArray parsed;
            try
            {
                parsed = JArray.Parse(commands);
            }
            catch (System.Exception ex)
            {
                return Response.Error("INVALID_COMMANDS", new { message = ex.Message, expected = "a JSON array of {name, params} objects" });
            }

            if (parsed.Count == 0)
                return Response.Error("EMPTY_BATCH", new { message = "commands array is empty" });
            if (parsed.Count > 100)
                return Response.Error("BATCH_TOO_LARGE", new { count = parsed.Count, max = 100 });

            var invoker = new FunctionInvoker();
            var results = new List<object>();
            bool aborted = false;
            bool timedOut = false;

            // The request is cut off at the ceiling, after which the client has its timeout and
            // every command still to come would run unseen. Stopping short of it leaves the batch to
            // answer for itself. A command already running cannot be stopped, hence the margin.
            var names = parsed.Select(command => (command as JObject)?["name"]?.ToString()).ToList();
            var budgetSeconds = ToolRegistry.BatchBudgetSeconds(names, MCPServerService.ToolCallTimeoutMs / 1000);
            var clock = Stopwatch.StartNew();

            // A fresh group, so collapsing takes in only what the batch registered: collapsing onto
            // the current one also swallowed whatever the user had just done in the same group.
            // Commands await across editor frames, and Unity opens a new group each frame, so
            // without the collapse a 12-command batch costs the user 12 presses of Ctrl+Z.
            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            var undoName = string.IsNullOrWhiteSpace(undo_label)
                ? $"MCP batch ({parsed.Count} command(s))"
                : undo_label;
            Undo.SetCurrentGroupName(undoName);

            try
            {
                for (int i = 0; i < parsed.Count; i++)
                {
                    if (clock.Elapsed.TotalSeconds > budgetSeconds - DeadlineMarginSeconds)
                    {
                        timedOut = true;
                        aborted = true;
                        break;
                    }

                    var name = names[i];
                    if (string.IsNullOrEmpty(name))
                    {
                        results.Add(new { index = i, success = false, error = "MISSING_NAME" });
                        if (stop_on_error) { aborted = true; break; }
                        continue;
                    }

                    // This runs its own invoker, so MCPExecutionBridge never sees the sub-commands:
                    // without this check a batch reaches tools that a direct call answers with
                    // TOOL_NOT_EXPOSED.
                    if (!MCPToolExportPolicy.IsToolAllowed(name, Settings(), out var profileKey))
                    {
                        results.Add(new { index = i, name, result = JToken.Parse(
                            ToolResultFormatter.Error("TOOL_NOT_EXPOSED", new { tool = name, profile = profileKey })) });
                        if (stop_on_error) { aborted = true; break; }
                        continue;
                    }

                    var fc = new FunctionCall
                    {
                        FunctionName = name,
                        Parameters = ExtractParams(parsed[i]["params"])
                    };

                    // Await instead of blocking: async tools pump their state
                    // machine on EditorApplication.update — a sync .GetAwaiter().GetResult() here
                    // deadlocks the editor main thread against that update loop.
                    var raw = await invoker.InvokeAsync(fc);
                    var resultToken = TryParse(raw);
                    bool ok = IsSuccess(raw, resultToken);

                    results.Add(new { index = i, name, result = resultToken });

                    if (!ok && stop_on_error) { aborted = true; break; }
                }
            }
            finally
            {
                // RecordObject entries are finalized at end of frame, so without the flush the last
                // command's component changes land in a group opened after the collapse.
                Undo.FlushUndoRecordObjects();
                Undo.SetCurrentGroupName(undoName);
                Undo.CollapseUndoOperations(undoGroup);
            }

            var payload = new { count = results.Count, total = parsed.Count, aborted, results };

            if (timedOut)
                return Response.Error("BATCH_TIMED_OUT", payload,
                    $"Batch stopped after {results.Count} of {parsed.Count} command(s): it ran " +
                    $"{clock.Elapsed.TotalSeconds:0}s of its {budgetSeconds}s budget, and the rest did not run. " +
                    "Send the remaining commands as another batch.");

            // An aborted batch has to read as a failure: MCPRequestHandler derives isError from this
            // envelope's success field, so reporting true hands the caller - a parent batch, or the
            // client itself - a green light for steps whose setup never ran.
            return aborted
                ? Response.Error("BATCH_ABORTED", payload,
                    $"Batch stopped after {results.Count} of {parsed.Count} command(s) due to an error. " +
                    "Inspect results for the failing step; pass stop_on_error=false to run past failures.")
                : Response.Success($"Batch executed {results.Count} command(s).", payload);
        }

        // Settable so a test can reach the deadline without running for minutes.
        internal static int DeadlineMarginSeconds = 15;

        private static SettingsController Settings() =>
            RootScopeServices.Services?.GetService(typeof(SettingsController)) as SettingsController;

        // Image tools return a bare "data:image/...;base64,..." string that FunctionInvoker passes
        // through unwrapped, so there is no success field to read: a screenshot is not a failure.
        internal static bool IsSuccess(string raw, object resultToken)
        {
            if (raw != null && raw.StartsWith("data:", System.StringComparison.Ordinal))
                return true;

            return resultToken is JObject obj
                   && obj["success"]?.Type == JTokenType.Boolean
                   && obj["success"].Value<bool>();
        }

        private static Dictionary<string, string> ExtractParams(JToken paramsToken)
        {
            var dict = new Dictionary<string, string>();
            if (!(paramsToken is JObject obj)) return dict;

            foreach (var prop in obj.Properties())
            {
                dict[prop.Name] = prop.Value.Type == JTokenType.String
                    ? prop.Value.ToString()
                    : prop.Value.ToString(Formatting.None);
            }
            return dict;
        }

        private static object TryParse(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            try { return JToken.Parse(raw); }
            catch { return raw; }
        }
    }
}
