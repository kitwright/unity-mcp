// Copyright (C) KitWright. Licensed under MIT.
using System.Collections.Generic;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
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

            // Snapshot the group before the first command so everything the batch registers can be
            // collapsed into it. Commands await across editor frames, and Unity opens a new group
            // each frame, so without this a 12-command batch costs the user 12 presses of Ctrl+Z.
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(string.IsNullOrWhiteSpace(undo_label)
                ? $"MCP batch ({parsed.Count} command(s))"
                : undo_label);

            try
            {
                for (int i = 0; i < parsed.Count; i++)
                {
                    var name = (parsed[i] as JObject)?["name"]?.ToString();
                    if (string.IsNullOrEmpty(name))
                    {
                        results.Add(new { index = i, success = false, error = "MISSING_NAME" });
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
                Undo.CollapseUndoOperations(undoGroup);
            }

            return Response.Success(
                aborted ? $"Batch stopped after {results.Count} of {parsed.Count} command(s) due to an error." : $"Batch executed {results.Count} command(s).",
                new { count = results.Count, total = parsed.Count, aborted, results });
        }

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
