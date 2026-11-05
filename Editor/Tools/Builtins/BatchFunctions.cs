// Copyright (C) KitWright. Licensed under MIT.
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
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
    [ToolProvider("MCP")]
    internal static class BatchFunctions
    {
        [Description("Run multiple MCP tool calls sequentially in a single request, on the main thread, saving round-trips. " +
                     "Pass a JSON array of {\"name\": \"<tool_name>\", \"params\": {..}} objects. Each result is returned in order. " +
                     "By default a failing call stops the batch; set stop_on_error=false to continue past failures. " +
                     "A capture in the batch comes back as an image block of the response, with {\"image_index\": N} " +
                     "standing in for it in results (N counts the response's images from 0). " +
                     "The scene changes the whole batch makes collapse into a single Undo step, so the user can revert " +
                     "the batch with one Ctrl+Z instead of one per command. File writes, asset imports and play-mode " +
                     "changes are outside Unity's undo system and are not reverted by it. " +
                     "rollback_on_error=true also reverts that step when the batch stops on a failure or its time budget. " +
                     "resolve_references=true lets a command use an earlier command's result; dry_run validates without running.")]
        public static async Task<object> BatchExecute(
            [ToolParam("JSON array of commands, e.g. [{\"name\":\"create_game_object\",\"params\":{\"name\":\"Box\",\"primitive\":\"Cube\"}},{\"name\":\"get_hierarchy\",\"params\":{}}]. " +
                       "With resolve_references each command may carry an id matching ^[A-Za-z_][A-Za-z0-9_-]*$, unique in the batch.")] string commands,
            [ToolParam("Stop the batch when a call fails (default true). If false, remaining calls still run.", Required = false)] bool stop_on_error = true,
            [ToolParam("Name shown in Unity's Edit > Undo menu for the collapsed step.", Required = false)] string undo_label = null,
            [ToolParam("Revert the batch's Undo-tracked scene changes when it stops on a failure or its time budget (default false). " +
                       "File, asset, settings and Play Mode changes are not reverted. With stop_on_error=false only the time budget " +
                       "stops the batch, so only that rolls it back. Edits made in the Editor while the batch runs are reverted with it. " +
                       "undo/redo commands are refused; execute_menu_item 'Edit/Undo' or Undo.PerformUndo in execute_code are not " +
                       "caught and can pop an Undo step from before the batch.", Required = false)] bool rollback_on_error = false,
            [ToolParam("Replace a string param whose whole value is \"$<id or index>.<path>\" with that earlier command's result, read " +
                       "under its data first (\"$0.instanceId\"), then the whole result (\"$0.data.instanceId\"). The path starts with a " +
                       "letter or '_', so \"$0.99\" stays text, and \"$$<id>.<path>\" passes \"$<id>.<path>\" through literally. A reference " +
                       "to a later, failed or unknown command, or a path that is not there, fails that command with UNRESOLVED_REFERENCE. " +
                       "Default false: every string arrives as written.", Required = false)] bool resolve_references = false,
            [ToolParam("Validate the command names (and the ids, with resolve_references) without running anything.", Required = false)] bool dry_run = false)
        {
            JArray parsed;
            try
            {
                parsed = JsonParse.Array(commands);
            }
            catch (System.Exception ex)
            {
                return Response.Error("INVALID_COMMANDS", new { message = ex.Message, expected = "a JSON array of {name, params} objects" });
            }

            return await Run(parsed, stop_on_error, undo_label, rollback_on_error, resolve_references, dry_run);
        }

        // References are opt-in because batch_execute shipped without them: a "$5.99" label a client already
        // sends must still arrive as text.
        internal static async Task<object> Run(
            JArray parsed, bool stop_on_error, string undo_label, bool rollback_on_error, bool resolveReferences, bool dryRun = false)
        {
            if (parsed.Count == 0)
                return Response.Error("EMPTY_BATCH", new { message = "commands array is empty" });
            if (parsed.Count > 100)
                return Response.Error("BATCH_TOO_LARGE", new { count = parsed.Count, max = 100 });

            var names = parsed.Select(command => (command as JObject)?["name"]?.ToString()).ToList();
            var ids = resolveReferences ? parsed.Select(command => ReadId(command as JObject)).ToArray() : null;
            if (ids != null && !TryValidateIds(ids, out var idError))
                return idError;

            if (rollback_on_error)
            {
                // An undo while the batch's group is still empty pops the user's group from before the batch, and
                // RevertAllDownToGroup cannot bring it back.
                for (int i = 0; i < names.Count; i++)
                {
                    if (string.Equals(names[i], nameof(UndoFunctions.Undo), System.StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(names[i], nameof(UndoFunctions.Redo), System.StringComparison.OrdinalIgnoreCase))
                        return Response.Error("BATCH_UNDO_IN_ROLLBACK", new { index = i, name = names[i] },
                            "undo and redo cannot run in a batch that rolls back on error. Drop them, or set rollback_on_error=false.");
                }
            }

            if (dryRun)
            {
                var unknown = names.Select((n, at) => new { index = at, name = n })
                    .Where(c => string.IsNullOrEmpty(c.name) || (ToolRegistry.GetMethod(c.name) == null && !ToolRegistry.IsManualTool(c.name)))
                    .ToArray();
                return unknown.Length > 0
                    ? Response.Error("UNKNOWN_TOOL", new { commands = unknown }, "Every command needs a name naming a registered tool.")
                    : Response.Success($"{parsed.Count} command(s) are valid; nothing ran.", new { count = parsed.Count, dry_run = true });
            }

            var invoker = new FunctionInvoker();
            var results = new List<object>();
            var resultTokens = new JToken[parsed.Count];
            var succeeded = new bool[parsed.Count];
            bool aborted = false;
            bool timedOut = false;
            var wasPlaying = IsPlaying();

            // The request is cut off at the ceiling, after which the client has its timeout and
            // every command still to come would run unseen. Stopping short of it leaves the batch to
            // answer for itself. A command already running cannot be stopped, hence the margin.
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
                    // TOOL_NOT_EXPOSED. Unknown names answer UNKNOWN_TOOL first, as a direct call does.
                    // The budget, the clock and the undo group are this batch's: an inner batch would
                    // hide its commands from the budget and start a clock of its own.
                    string refusal = null;
                    if (string.Equals(name, ToolRegistry.BatchToolName, System.StringComparison.OrdinalIgnoreCase))
                        refusal = ToolResultFormatter.Error("BATCH_NESTED", new { tool = name },
                            "batch_execute cannot run inside batch_execute. Put the inner commands in this batch's own commands array (up to 100).");
                    else if (ToolRegistry.GetMethod(name) == null && !ToolRegistry.IsManualTool(name))
                        refusal = ToolResultFormatter.Error("UNKNOWN_TOOL", new { tool = name });
                    else if (!MCPToolExportPolicy.IsToolAllowed(name, Settings(), out var profileKey))
                        refusal = MCPExecutionBridge.WithheldToolError(
                            name, profileKey, ToolPackageGate.MissingPackageFor(name), MCPExecutionBridge.NotExposedHint(Settings()));

                    if (refusal != null)
                    {
                        results.Add(new { index = i, name, result = JToken.Parse(refusal) });
                        if (stop_on_error) { aborted = true; break; }
                        continue;
                    }

                    var paramsToken = parsed[i]["params"];
                    if (ids != null)
                    {
                        paramsToken = ResolveReferences(paramsToken, ids, i, resultTokens, succeeded, out var unresolved);
                        if (unresolved != null)
                        {
                            results.Add(new { index = i, name, result = JToken.Parse(ToolResultFormatter.Error("UNRESOLVED_REFERENCE", unresolved,
                                "A $ref.path value must name an earlier operation that succeeded (by id or index) and a path its result has.")) });
                            if (stop_on_error) { aborted = true; break; }
                            continue;
                        }
                    }

                    var fc = new FunctionCall
                    {
                        FunctionName = name,
                        Parameters = ExtractParams(paramsToken)
                    };

                    // Await instead of blocking: async tools pump their state
                    // machine on EditorApplication.update — a sync .GetAwaiter().GetResult() here
                    // deadlocks the editor main thread against that update loop.
                    var raw = await invoker.InvokeAsync(fc);
                    // The bridge adds the Play Mode note to a direct call; this loop never goes through it.
                    raw = MCPExecutionBridge.AppendPlayModeNote(raw, name, IsPlaying());
                    var resultToken = TryParse(raw);
                    bool ok = IsSuccess(raw, resultToken);

                    results.Add(new { index = i, name, result = resultToken });
                    resultTokens[i] = resultToken as JToken;
                    succeeded[i] = ok;

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

            bool rolledBack = false;
            string rollbackSkipped = null;
            if (aborted && rollback_on_error)
            {
                // Entering or leaving Play Mode resets the undo stack around the transition, so the batch's group
                // may no longer be the one this id names.
                if (IsPlaying() != wasPlaying)
                {
                    rollbackSkipped = "play_mode_changed";
                }
                else
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    rolledBack = true;
                }
            }

            object payload = rollback_on_error
                ? (object)new { count = results.Count, total = parsed.Count, aborted, rolled_back = rolledBack, rollback_skipped = rollbackSkipped, results }
                : new { count = results.Count, total = parsed.Count, aborted, results };
            var reverted = rolledBack
                ? "The whole batch was reverted (its Undo-tracked scene changes; file, asset, settings and Play Mode changes were not). "
                : rollbackSkipped != null ? "Not rolled back: Play Mode changed during the batch. " : string.Empty;

            if (timedOut)
                return Response.Error("BATCH_TIMED_OUT", payload,
                    $"Batch stopped after {results.Count} of {parsed.Count} command(s): it ran " +
                    $"{clock.Elapsed.TotalSeconds:0}s of its {budgetSeconds}s budget, and the rest did not run. " + reverted +
                    (rolledBack ? "Resend the whole batch, or split it into smaller ones." : "Send the remaining commands as another batch."));

            // An aborted batch has to read as a failure: MCPRequestHandler derives isError from this
            // envelope's success field, so reporting true hands the caller - a parent batch, or the
            // client itself - a green light for steps whose setup never ran.
            return aborted
                ? Response.Error("BATCH_ABORTED", payload,
                    $"Batch stopped after {results.Count} of {parsed.Count} command(s) due to an error. " + reverted +
                    (rolledBack ? "Fix the failing step and resend the whole batch." : "Inspect results for the failing step; pass stop_on_error=false to run past failures."))
                : Response.Success($"Batch executed {results.Count} command(s).", payload);
        }

        // \z, not $: $ also matches before a trailing newline, which would let "a\n" through as an id.
        private static readonly Regex IdPattern = new Regex(@"^[A-Za-z_][A-Za-z0-9_-]*\z");

        // The path has to start with an identifier, so a price such as "$0.99" stays text.
        private static readonly Regex ReferencePattern = new Regex(@"^\$(?<ref>[A-Za-z_][A-Za-z0-9_-]*|\d+)\.(?<path>[A-Za-z_]\S*)\z");

        private static string ReadId(JObject command)
        {
            var token = command?["id"];
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static bool TryValidateIds(IReadOnlyList<string> ids, out object error)
        {
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] == null)
                    continue;

                if (!IdPattern.IsMatch(ids[i]))
                {
                    error = Response.Error("INVALID_ID", new { index = i, id = ids[i], expected = IdPattern.ToString() });
                    return false;
                }

                if (!seen.Add(ids[i]))
                {
                    error = Response.Error("DUPLICATE_ID", new { index = i, id = ids[i] });
                    return false;
                }
            }

            error = null;
            return true;
        }

        internal static JToken ResolveReferences(
            JToken token, IReadOnlyList<string> ids, int current, IReadOnlyList<JToken> results, IReadOnlyList<bool> succeeded,
            out object unresolved)
        {
            unresolved = null;
            switch (token)
            {
                case JObject obj:
                {
                    var copy = new JObject();
                    foreach (var property in obj.Properties())
                    {
                        var value = ResolveReferences(property.Value, ids, current, results, succeeded, out unresolved);
                        if (unresolved != null)
                            return null;
                        copy[property.Name] = value;
                    }
                    return copy;
                }
                case JArray array:
                {
                    var copy = new JArray();
                    foreach (var item in array)
                    {
                        var value = ResolveReferences(item, ids, current, results, succeeded, out unresolved);
                        if (unresolved != null)
                            return null;
                        copy.Add(value);
                    }
                    return copy;
                }
                case JValue value when value.Type == JTokenType.String:
                {
                    var text = (string)value;
                    if (text.StartsWith("$$", System.StringComparison.Ordinal) && ReferencePattern.IsMatch(text.Substring(1)))
                        return new JValue(text.Substring(1));

                    var match = ReferencePattern.Match(text);
                    if (!match.Success)
                        return value;

                    var reference = match.Groups["ref"].Value;
                    var path = match.Groups["path"].Value;
                    var found = Lookup(reference, path, ids, current, results, succeeded);
                    if (found == null)
                        unresolved = new { reference, path };
                    return found;
                }
                default:
                    return token;
            }
        }

        private static JToken Lookup(
            string reference, string path, IReadOnlyList<string> ids, int current, IReadOnlyList<JToken> results, IReadOnlyList<bool> succeeded)
        {
            var index = char.IsDigit(reference[0])
                ? (int.TryParse(reference, out var n) ? n : -1)
                : IndexOf(ids, reference);
            if (index < 0 || index >= current || !succeeded[index] || !(results[index] is JObject result))
                return null;

            try
            {
                // Results are {success, message, data}: "$0.instanceId" reads data, "$0.data.instanceId" the envelope.
                var found = (result["data"] as JContainer)?.SelectToken(path) ?? result.SelectToken(path);
                // A JSON null would be dropped as an absent param, and the callee's "missing" error hides the cause.
                return found == null || found.Type == JTokenType.Null ? null : found.DeepClone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static int IndexOf(IReadOnlyList<string> ids, string id)
        {
            for (int i = 0; i < ids.Count; i++)
                if (string.Equals(ids[i], id, System.StringComparison.Ordinal))
                    return i;
            return -1;
        }

        // Settable so a test can reach the deadline without running for minutes.
        internal static int DeadlineMarginSeconds = 15;

        // Settable so a test can see the Play Mode note without entering Play Mode.
        internal static System.Func<bool> IsPlaying = () => EditorApplication.isPlaying;

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

        internal static Dictionary<string, string> ExtractParams(JToken paramsToken)
        {
            var dict = new Dictionary<string, string>();
            if (!(paramsToken is JObject obj)) return dict;

            foreach (var prop in obj.Properties())
            {
                // Same as a direct call: a JSON null is an argument left out, not the text "null".
                if (prop.Value.Type == JTokenType.Null)
                    continue;

                dict[prop.Name] = prop.Value.Type == JTokenType.String
                    ? prop.Value.ToString()
                    : prop.Value.ToString(Formatting.None);
            }
            return dict;
        }

        private static object TryParse(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            try { return JsonParse.Token(raw); }
            catch { return raw; }
        }
    }
}
