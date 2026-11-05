// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using KitWright.Editor.DI;
using KitWright.Editor.Services;
using KitWright.Editor.Settings;
using KitWright.Editor.Tools.Helpers;
using KitWright.Editor.Tools.Scripting;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("ExecuteCode")]
    internal static class ScriptExecutionFunctions
    {
        private const string HistorySessionKey = "KitWright.MCP.ExecuteCode.History";
        private const int HistoryMaxEntries = 50;
        private const int HistoryMaxCodeChars = 32 * 1024;
        private const string KitWrightScriptingNamespace = "KitWright.Editor.Tools.Scripting";
        private const string RefreshDidNotStartCompilationCode = "REFRESH_DID_NOT_START_COMPILATION";

        [Description("Primary high-flexibility execution tool. Compiles a C# snippet with Unity's Roslyn csc first " +
                     "while preserving the in-memory compilation/execution flow, then runs the compiled assembly on the editor thread. " +
                     "Three templates are supported:\n" +
                     "  1) Shortest: a bare method body, no class — the common usings (UnityEngine, UnityEditor, System.Linq, " +
                     "SceneManagement, ...) are added for you, and `return <anything>` becomes the response message.\n" +
                     "  2) Recommended for edits: implement IKitWrightCommand on a class — receives an ExecutionContext (ctx) " +
                     "with RegisterObjectCreation/RegisterObjectModification/DestroyObject (auto-Undo + tracked) and " +
                     "Log/LogWarning/LogError (returned in the response). When a snippet mutates a scene object or asset, " +
                     "call ctx.RegisterObjectModification(obj) BEFORE changing it — a bare EditorUtility.SetDirty marks the " +
                     "change for saving but leaves the user with no Ctrl+Z.\n" +
                     "  3) Legacy: any class with `public static` Run() — return value becomes the response message.\n" +
                     "Before compiling, the editor's AssetDatabase is refreshed and pending compilation is awaited, " +
                     "so external file edits are picked up automatically without a separate request_recompile " +
                     "(pass skip_refresh=true to bypass this for read-only snippets). " +
                     "The refresh is skipped while Play Mode runs, so a snippet then runs against whatever is already compiled. " +
                     "When a full-class snippet implements IKitWrightCommand, the required KitWright.Editor.Tools.Scripting using is added automatically if omitted. " +
                     "safety_checks blocks a small set of obviously dangerous patterns " +
                     "(File.Delete, Process.Start, while(true), Environment.Exit, AssetDatabase.DeleteAsset, etc) " +
                     "plus broad System.IO writes and obvious absolute/system/traversal paths. " +
                     "This catches accidents, not intent: safety_checks is an argument you control, so it is neither a sandbox nor a security boundary. " +
                     "If omitted, the MCP Settings window's default safety-check setting is used (enabled by default); " +
                     "explicitly passing true or false overrides that default. Project namespaces are not auto-injected " +
                     "by default; add `using` directives in the snippet, or enable the ScriptAssemblies-based convenience toggle in the MCP Settings window. " +
                     "Every invocation is appended to a session-scoped history (see get_execute_code_history / replay_execute_code). " +
                     "A call is cut off after about 170 s while the work already running in the editor goes on; " +
                     "pass timeout_seconds for work that legitimately takes longer.")]
        public static async Task<object> ExecuteCode(
            [ToolParam("C# code to execute: a bare method body, or a full class (IKitWrightCommand or static Run()).")] string code,
            [ToolParam("If true, reject the call before compile when the code contains obviously dangerous patterns. Guards against accidents only — you can pass false yourself, so it never holds against a caller that wants through. If omitted, uses the MCP Settings window default.", Required = false)] bool? safety_checks = null,
            [ToolParam("If true, skip the pre-compile AssetDatabase.Refresh + wait-for-ready. Use only when the editor is already up to date -- e.g. a read-only inspection snippet. The default refresh can trigger an import/domain reload (from your own OR another actor's pending changes in a shared editor), which is why it is skipped automatically while Play Mode runs. When skipped, external file edits made since the last compile are NOT picked up.", Required = false)] bool skip_refresh = false,
            [ToolParam("Seconds the snippet may run, for work that legitimately outlasts the ~170 s call ceiling (up to 1680). Raises the call ceiling to this plus 120 s and the budget for the snippet's loops from 20 s to this, so a runaway loop is stopped only after this long. 0 (default) keeps both ordinary limits; a value never narrows them.", Required = false)] int timeout_seconds = 0)
        {
            var effectiveSafetyChecks = safety_checks ?? ResolveSafetyChecksDefault();
            if (effectiveSafetyChecks)
            {
                if (ExecuteCodeSafetyPolicy.TryFindViolation(code, strictFilesystemChecks: true, out var pattern, out var reason, out var alternative))
                {
                    var blocked = Response.Error("SAFETY_CHECK_BLOCKED",
                        new
                        {
                            pattern,
                            reason,
                            alternative,
                            hint = "Rewrite the snippet to avoid the pattern. If the alternative names a tool that answers TOOL_NOT_EXPOSED, set_tool_profile 'full' exposes it. safety_checks=false lifts this, but that is the user's call to make — do not retry with it on your own."
                        });
                    AppendHistory(code, false, $"Blocked: {reason}");
                    return blocked;
                }
            }

            // The refresh can trigger an import and a domain reload, which tears down Play Mode
            // and never returns to the caller. The parameter doc has always promised this is
            // skipped while playing; only the check was missing.
            if (!skip_refresh && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                try
                {
                    await EditorRefreshPipeline.RefreshAndWaitForReadyAsync(TimeSpan.FromSeconds(120));
                }
                catch (TimeoutException)
                {
                    AppendHistory(code, false, "EDITOR_BUSY");
                    return Response.Error("EDITOR_BUSY",
                        new { hint = "Unity is still compiling/importing. Retry in a moment, or pass skip_refresh=true if you know the editor is up to date." });
                }
                catch (EditorRefreshDidNotStartCompilationException ex)
                {
                    AppendHistory(code, false, RefreshDidNotStartCompilationCode);
                    return RefreshDidNotStartCompilationError(ex);
                }
            }

            var className = "TempScript_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var fullCode = BuildCodeForCompilation(
                code,
                className,
                ResolveProjectNamespaceInjection(),
                out var actualClassName);

            try
            {
                var result = RunInSingleUndoGroup(UndoGroupName(code),
                    () => CompileAndExecute(fullCode, actualClassName, effectiveSafetyChecks, SnippetPlacement.Of(code, fullCode),
                        LoopBudgetFor(timeout_seconds)));
                AppendHistory(code, IsSuccess(result), SummarizeResult(result));
                return result;
            }
            catch (Exception ex)
            {
                var root = UnwrapTargetInvocationException(ex);
                Debug.LogError($"[KitWright] ExecuteCode failed: {root.GetType().FullName}: {root.Message}\n{root.StackTrace}");
                AppendHistory(code, false, $"{root.GetType().Name}: {root.Message}");
                return Response.Error("EXECUTE_CODE_FAILED", new
                {
                    message = root.Message,
                    exception_type = root.GetType().FullName,
                    stack = root.StackTrace,
                    outer_exception_type = ReferenceEquals(root, ex) ? null : ex.GetType().FullName,
                    outer_message = ReferenceEquals(root, ex) ? null : ex.Message
                });
            }
        }

        internal static Exception UnwrapTargetInvocationException(Exception exception)
        {
            while (exception is TargetInvocationException invocationException &&
                   invocationException.InnerException != null)
            {
                exception = invocationException.InnerException;
            }

            return exception;
        }

        // The code request_recompile and wait_for_compilation already answer for the same condition.
        internal static object RefreshDidNotStartCompilationError(EditorRefreshDidNotStartCompilationException exception)
        {
            return Response.Error(RefreshDidNotStartCompilationCode, new
            {
                refresh = exception.RefreshResult?.ToResponseData(),
                hint = "Unity did not start compiling script files that are newer than the compiled assemblies, so the snippet was not run. " +
                       "Pass skip_refresh=true to run it against what is already compiled, or get the edit compiled first " +
                       "(a hot-reload or auto-refresh interception plugin may be swallowing the compile request)."
            });
        }

        [Description("Return the most recent execute_code invocations (success or failure) from the current Editor session. " +
                     "Each entry has an id that never changes and is never reused, not even after the Editor restarts; pass it to " +
                     "replay_execute_code to re-run that entry. History survives domain reloads but is cleared " +
                     "when the Editor closes (uses SessionState). Cap is 50 entries; older entries are dropped.")]
        [ReadOnlyTool]
        public static object GetExecuteCodeHistory(
            [ToolParam("Number of most-recent entries to return (1-50). Default 10.", Required = false)] int limit = 10)
        {
            var entries = LoadHistory().entries;
            limit = Mathf.Clamp(limit, 1, HistoryMaxEntries);
            var slice = entries.Skip(Math.Max(0, entries.Count - limit)).ToList();
            var view = new List<object>(slice.Count);
            for (int i = slice.Count - 1; i >= 0; i--)
            {
                var entry = slice[i];
                view.Add(new
                {
                    id = entry.id,
                    timestamp = entry.timestamp,
                    success = entry.success,
                    summary = entry.summary,
                    code_preview = Preview(entry.code, 240),
                    code_length = entry.code?.Length ?? 0
                });
            }
            return Response.Success($"Returned {view.Count} of {entries.Count} history entries.",
                new { total = entries.Count, returned = view.Count, entries = view });
        }

        [Description("Re-run a past execute_code invocation by its history id (use get_execute_code_history to discover ids). " +
                     "The original code is re-compiled and executed; this also appends a new history entry. " +
                     "An id that has left the history (it keeps the latest 50) is refused rather than matched to another entry. " +
                     "Pass safety_checks to override the MCP Settings window default. " +
                     "Like execute_code, the AssetDatabase is refreshed before compiling unless skip_refresh=true (use it to replay a read-only snippet " +
                     "without risking an import or domain reload); timeout_seconds raises the budget for the snippet's loops.")]
        public static async Task<object> ReplayExecuteCode(
            [ToolParam("History entry id to replay: the `id` field returned by get_execute_code_history. It is an id, not a list position.")] int index,
            [ToolParam("If true, re-evaluate the safety blocklist before re-running. Guards against accidents only — you can pass false yourself, so it never holds against a caller that wants through. If omitted, uses the MCP Settings window default.", Required = false)] bool? safety_checks = null,
            [ToolParam("If true, skip the pre-compile AssetDatabase.Refresh + wait-for-ready, as for execute_code. Use only when the editor is already up to date, e.g. replaying a read-only inspection snippet: the default refresh can trigger an import/domain reload on a shared editor.", Required = false)] bool skip_refresh = false,
            [ToolParam("Seconds the snippet may run, as for execute_code (up to 1680): raises the budget for the snippet's loops from 20 s to this. 0 (default) keeps the ordinary limit; a value never narrows it.", Required = false)] int timeout_seconds = 0)
        {
            var refusal = ResolveReplayEntry(LoadHistory(), index, out var entry);
            if (refusal != null)
                return refusal;

            return await ExecuteCode(entry.code, safety_checks, skip_refresh, timeout_seconds);
        }

        internal static object ResolveReplayEntry(HistoryBox box, int id, out HistoryEntry entry)
        {
            entry = box.entries.FirstOrDefault(candidate => candidate.id == id);
            if (entry == null)
                return Response.Error("HISTORY_ENTRY_NOT_FOUND",
                    new
                    {
                        provided = id,
                        oldest_id = box.entries.Count > 0 ? (int?)box.entries[0].id : null,
                        newest_id = box.entries.Count > 0 ? (int?)box.entries[box.entries.Count - 1].id : null
                    },
                    "No entry has that id: it was dropped from the history (the latest 50 are kept), cleared, or never existed. " +
                    "Ids are not reused, so nothing else answers to it. get_execute_code_history lists the current ids.");

            if (string.IsNullOrEmpty(entry.code))
                return Response.Error("HISTORY_ENTRY_EMPTY", new { id = entry.id });

            return null;
        }

        [Description("Erase the entire execute_code history for the current Editor session. " +
                     "Useful when you want a clean slate before a fresh experiment or before sharing a session recording.")]
        public static object ClearExecuteCodeHistory()
        {
            var box = LoadHistory();
            var before = box.entries.Count;
            // lastId stays, so an id handed out before the clear is never given to a new entry.
            box.entries.Clear();
            SessionState.SetString(HistorySessionKey, JsonConvert.SerializeObject(box));
            return Response.Success($"Cleared {before} history entr{(before == 1 ? "y" : "ies")}.",
                new { cleared = before });
        }

        // ---- History helpers ----------------------------------------------------

        private static bool ResolveSafetyChecksDefault()
        {
            var settings = RootScopeServices.Services?.GetService(typeof(SettingsController)) as SettingsController;
            return settings?.ExecuteCodeSafetyChecksEnabled ?? true;
        }

        private static bool ResolveProjectNamespaceInjection()
        {
            var settings = RootScopeServices.Services?.GetService(typeof(SettingsController)) as SettingsController;
            return settings?.ExecuteCodeProjectNamespaceInjectionEnabled ?? false;
        }

        [Serializable]
        internal class HistoryEntry
        {
            public int id;
            public string timestamp;
            public string code;
            public bool success;
            public string summary;
        }

        [Serializable]
        internal class HistoryBox
        {
            public List<HistoryEntry> entries = new List<HistoryEntry>();
            public int lastId;
        }

        // 2026-01-01 UTC. A new history starts numbering from the clock, so an id a client kept from
        // before an Editor restart (or from a history that could not be read back) matches nothing.
        private const long HistoryIdEpochSeconds = 1767225600L;

        private static HistoryBox NewHistoryBox() =>
            new HistoryBox { lastId = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - HistoryIdEpochSeconds) };

        internal static HistoryBox LoadHistory()
        {
            var raw = SessionState.GetString(HistorySessionKey, null);
            if (string.IsNullOrEmpty(raw))
                return NewHistoryBox();
            try
            {
                var box = JsonConvert.DeserializeObject<HistoryBox>(raw) ?? NewHistoryBox();
                // Entries written before ids existed all read as 0, which would make them share one id.
                foreach (var entry in box.entries)
                    if (entry.id <= 0)
                        entry.id = ++box.lastId;
                return box;
            }
            catch
            {
                return NewHistoryBox();
            }
        }

        internal static HistoryEntry AddHistoryEntry(HistoryBox box, string code, bool success, string summary)
        {
            var entry = new HistoryEntry
            {
                id = ++box.lastId,
                timestamp = DateTime.UtcNow.ToString("o"),
                // SessionState holds the whole history as one string and rewrites it on every call,
                // so a generated 200 KB snippet costs every later call. Too large to keep means too
                // large to replay, which the replay tool already answers for an empty entry.
                code = code != null && code.Length <= HistoryMaxCodeChars ? code : string.Empty,
                success = success,
                summary = Preview(summary, 200)
            };
            box.entries.Add(entry);
            if (box.entries.Count > HistoryMaxEntries)
                box.entries.RemoveRange(0, box.entries.Count - HistoryMaxEntries);
            return entry;
        }

        private static void AppendHistory(string code, bool success, string summary)
        {
            try
            {
                var box = LoadHistory();
                AddHistoryEntry(box, code, success, summary);
                SessionState.SetString(HistorySessionKey, JsonConvert.SerializeObject(box));
            }
            catch (Exception ex)
            {
                // Swallow — history is best-effort, must never break real execution.
                Debug.LogWarning($"[KitWright] Failed to append execute_code history: {ex.Message}");
            }
        }

        private static bool IsSuccess(object result)
        {
            if (result == null) return false;
            var prop = result.GetType().GetProperty("success");
            return prop?.GetValue(result) is bool b && b;
        }

        private static string SummarizeResult(object result)
        {
            if (result == null) return "null";
            var t = result.GetType();
            var success = t.GetProperty("success")?.GetValue(result) as bool? ?? false;
            if (success)
                return (t.GetProperty("message")?.GetValue(result) as string) ?? "OK";
            var code = t.GetProperty("code")?.GetValue(result) as string;
            var error = t.GetProperty("error")?.GetValue(result) as string;
            return code ?? error ?? "ERROR";
        }

        private static string Preview(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? string.Empty;
            return s.Substring(0, max) + "…";
        }

        // One snippet has to be one Ctrl+Z: a snippet that touches ten objects otherwise registers ten
        // undo entries, so the first undo leaves the project reverted by a tenth. Incrementing first keeps
        // unrelated earlier edits out of the group, and no undo record means no entry, so a read-only
        // snippet still leaves nothing behind.
        internal static object RunInSingleUndoGroup(string groupName, Func<object> execute)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            try
            {
                return execute();
            }
            finally
            {
                // RecordObject entries are finalized at end of frame, so without the flush every
                // component mutation the snippet made lands in a group created after the collapse.
                Undo.FlushUndoRecordObjects();
                Undo.SetCurrentGroupName(groupName);
                Undo.CollapseUndoOperations(group);
            }
        }

        internal static string UndoGroupName(string code)
        {
            var match = Regex.Match(code ?? string.Empty, @"class\s+(\w+)");
            if (match.Success)
                return "execute_code: " + match.Groups[1].Value;

            var firstLine = (code ?? string.Empty).Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0);
            return "execute_code: " + Preview(firstLine, 60);
        }

        internal static TimeSpan LoopBudgetFor(int timeoutSeconds)
        {
            var requested = TimeSpan.FromSeconds(ToolRegistry.ClampExecuteCodeTimeout(timeoutSeconds));
            return requested > LoopBudgetGuard.DefaultBudget ? requested : LoopBudgetGuard.DefaultBudget;
        }

        private static object CompileAndExecute(string code, string className, bool safetyChecks,
            SnippetPlacement placement = default, TimeSpan? loopBudget = null)
        {
            var compilation = ScriptCompilerPipeline.Compile(LoopGuardInjector.Inject(code));
            if (compilation.Status == ScriptCompilationStatus.CompilationFailed)
            {
                var errors = compilation.Errors.Select(placement.ToSnippet).ToList();
                var hint = HintForCompileErrors(errors);
                // The attempts only say something when one compiler failed over to another; a lone
                // Roslyn attempt repeated what "compiler" already says, on every compile error.
                return compilation.Attempts.Count > 1
                    ? Response.Error("COMPILATION_FAILED", new
                    {
                        compiler = compilation.CompilerName,
                        errors,
                        compiler_attempts = compilation.Attempts
                    }, hint)
                    : Response.Error("COMPILATION_FAILED", new { compiler = compilation.CompilerName, errors }, hint);
            }

            if (compilation.Status != ScriptCompilationStatus.Success || compilation.Assembly == null)
            {
                return Response.Error("COMPILATION_BACKEND_UNAVAILABLE", new
                {
                    compiler = compilation.CompilerName,
                    message = compilation.Message,
                    compiler_attempts = compilation.Attempts
                });
            }

            if (safetyChecks &&
                CompiledCodeGuard.TryFindViolation(compilation.Assembly, strict: true, out var reference, out var guardReason))
            {
                // A modal call is a liveness hazard, not a safety preference: retrying it with
                // safety_checks=false freezes the editor and hangs the very request that would
                // report the freeze, so that escape hatch is not advertised for those.
                var modal = CompiledCodeGuard.IsModalMember(reference);
                return Response.Error("SAFETY_CHECK_BLOCKED", new
                {
                    reference,
                    reason = guardReason,
                    stage = "compiled",
                    hint = modal
                        ? "Do not retry with safety_checks=false: this call blocks the editor's message loop, so the request would hang instead of failing."
                        : "Detected in the compiled assembly's metadata, so aliasing or building the name at runtime does not get around it. safety_checks=false lifts the guard, but that is the user's call to make — do not retry with it on your own."
                });
            }

            LoopBudgetGuard.Begin(loopBudget ?? LoopBudgetGuard.DefaultBudget);
            try
            {
                var warnings = compilation.Warnings.Select(placement.ToSnippet).Where(placement.IsInSnippet).ToList();
                return ExecuteCompiledAssembly(compilation.Assembly, className, compilation.CompilerName, compilation.Attempts, warnings);
            }
            finally
            {
                LoopBudgetGuard.End();
            }
        }

        private static object ExecuteCompiledAssembly(
            Assembly compiledAssembly,
            string className,
            string compilerName,
            List<ScriptCompilerAttempt> compilerAttempts,
            List<ScriptCompilationError> warnings)
        {
            // Prefer IKitWrightCommand path: any class in the compiled assembly that implements it
            Type commandType = null;
            try
            {
                commandType = compiledAssembly.GetTypes()
                    .FirstOrDefault(t => typeof(IKitWrightCommand).IsAssignableFrom(t)
                                         && !t.IsInterface && !t.IsAbstract);
            }
            catch (ReflectionTypeLoadException)
            {
                // Fall through to legacy Run() path
            }
            if (commandType != null)
                return ExecuteAsCommand(commandType, compilerName, warnings);

            // Legacy path: class with `static Run()`
            var type = compiledAssembly.GetType(className);
            if (type == null)
                return Response.Error("CLASS_NOT_FOUND",
                    new { className, available = GetTypeNames(compiledAssembly), compiler = compilerName, compiler_attempts = compilerAttempts });

            var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                return Response.Error("RUN_METHOD_NOT_FOUND", new { className, compiler = compilerName });

            try
            {
                var result = method.Invoke(null, null)?.ToString() ?? "OK";
                return Response.Success("Executed (legacy Run()).", warnings.Count == 0
                    ? (object)new { result, compiler = compilerName }
                    : new { result, compiler = compilerName, warnings = Capped(warnings), warning_count = warnings.Count });
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                return Response.Error("RUNTIME_ERROR",
                    new { message = inner.Message, stack = inner.StackTrace, compiler = compilerName });
            }
        }

        private static object ExecuteAsCommand(Type commandType, string compilerName, List<ScriptCompilationError> warnings)
        {
            IKitWrightCommand instance;
            try { instance = (IKitWrightCommand)Activator.CreateInstance(commandType); }
            catch (Exception ex)
            {
                return Response.Error("COMMAND_INSTANTIATION_FAILED",
                    new { type = commandType.FullName, error = ex.Message, compiler = compilerName });
            }

            var ctx = new ExecutionContext();
            try
            {
                instance.Execute(ctx);
            }
            catch (Exception ex)
            {
                return Response.Error("COMMAND_RUNTIME_ERROR", new
                {
                    message = ex.Message,
                    stack = ex.StackTrace,
                    compiler = compilerName,
                    logs = ctx.Logs,
                    created = ctx.CreatedInstanceIds,
                    modified = ctx.ModifiedInstanceIds,
                    destroyed = ctx.DestroyedInstanceIds
                });
            }

            var message = DescribeCommandOutcome(ctx.Logs, out var loggedErrors, out var firstError);
            // Two shapes so a clean snippet's answer does not grow a warnings field on every call.
            return Response.Success(message, warnings.Count == 0
                ? (object)new
                {
                    compiler = compilerName,
                    logged_error_count = loggedErrors,
                    first_logged_error = firstError,
                    logs = ctx.Logs,
                    created = ctx.CreatedInstanceIds,
                    modified = ctx.ModifiedInstanceIds,
                    destroyed = ctx.DestroyedInstanceIds,
                    returnValue = ctx.ReturnValue
                }
                : new
                {
                    compiler = compilerName,
                    logged_error_count = loggedErrors,
                    first_logged_error = firstError,
                    logs = ctx.Logs,
                    created = ctx.CreatedInstanceIds,
                    modified = ctx.ModifiedInstanceIds,
                    destroyed = ctx.DestroyedInstanceIds,
                    returnValue = ctx.ReturnValue,
                    warnings = Capped(warnings),
                    warning_count = warnings.Count
                });
        }

        private const int MaxWarnings = 20;

        internal static List<ScriptCompilationError> Capped(List<ScriptCompilationError> warnings) =>
            warnings.Count > MaxWarnings ? warnings.GetRange(0, MaxWarnings) : warnings;

        // A snippet that only logs errors still ran, so success stays true — but the message has to
        // say so up front, or the caller reads "Command executed." and assumes a clean run.
        internal static string DescribeCommandOutcome(
            IReadOnlyList<ExecutionContext.LogEntry> logs, out int errorCount, out string firstError)
        {
            errorCount = 0;
            firstError = null;

            if (logs != null)
            {
                foreach (var entry in logs)
                {
                    if (entry == null || entry.Level != "error")
                        continue;
                    errorCount++;
                    if (firstError == null)
                        firstError = entry.Message;
                }
            }

            return errorCount == 0
                ? "Command executed."
                : $"[{errorCount} logged error{(errorCount == 1 ? "" : "s")}] Command executed. First error: {firstError}";
        }

        private static string[] GetTypeNames(Assembly assembly)
        {
            try
            {
                return Array.ConvertAll(assembly.GetTypes(), t => t.FullName);
            }
            catch
            {
                return new[] { "(unable to list types)" };
            }
        }

        internal static string BuildCodeForCompilation(
            string code,
            string className,
            bool injectProjectNamespaces,
            out string actualClassName)
        {
            actualClassName = className;
            var projectUsings = injectProjectNamespaces ? GetReachableProjectNamespaceUsings() : string.Empty;

            if (code.Contains("class "))
            {
                var match = Regex.Match(code, @"class\s+(\w+)");
                if (match.Success)
                    actualClassName = match.Groups[1].Value;

                var requiredUsings = GetRequiredSnippetUsings(code);
                var withUsings = PrependMissingUsings(code, requiredUsings + projectUsings);
                return NeedsObjectAlias(code) ? "using Object = UnityEngine.Object;\n" + withUsings : withUsings;
            }

            return WrapCode(code, className, projectUsings);
        }

        /// <summary>
        /// Where the caller's snippet sits in the source that was compiled. The compiler reports lines
        /// of that source: a bare body starts on line 18, under the usings and the wrapper class, and
        /// its first line is indented 8. Reported as-is, an error on the snippet's line 3 read as line
        /// 19, and the agent spent a round trip finding the line it had written.
        /// </summary>
        internal readonly struct SnippetPlacement
        {
            private readonly int _linesBefore;
            private readonly int _firstLineIndent;
            private readonly int _snippetLines;

            private SnippetPlacement(int linesBefore, int firstLineIndent, int snippetLines)
            {
                _linesBefore = linesBefore;
                _firstLineIndent = firstLineIndent;
                _snippetLines = snippetLines;
            }

            internal static SnippetPlacement Of(string snippet, string compiled)
            {
                var at = string.IsNullOrEmpty(snippet) ? -1 : compiled.IndexOf(snippet, StringComparison.Ordinal);
                if (at < 0)
                    return default;

                var lineStart = at == 0 ? 0 : compiled.LastIndexOf('\n', at - 1) + 1;
                var linesBefore = 0;
                for (var i = 0; i < at; i++)
                    if (compiled[i] == '\n')
                        linesBefore++;
                var snippetLines = snippet.TrimEnd('\r', '\n').Count(c => c == '\n') + 1;
                return new SnippetPlacement(linesBefore, at - lineStart, snippetLines);
            }

            // The wrapper adds code after a bare body too (its trailing "return null;" is unreachable behind the
            // snippet's own return, CS0162), so a mapped line past the snippet is not the caller's either.
            internal bool IsInSnippet(ScriptCompilationError mapped) =>
                mapped.line >= 1 && (_snippetLines == 0 || mapped.line <= _snippetLines);

            // Lines inside the generated wrapper come back as 0: there is no snippet line to point at.
            internal ScriptCompilationError ToSnippet(ScriptCompilationError error)
            {
                var line = error.line - _linesBefore;
                return new ScriptCompilationError
                {
                    line = Math.Max(0, line),
                    column = line == 1 ? Math.Max(1, error.column - _firstLineIndent) : error.column,
                    text = error.text,
                    code = error.code
                };
            }
        }

        private static string WrapCode(string code, string className, string projectUsings)
        {
            return $@"using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using Object = UnityEngine.Object;
using {KitWrightScriptingNamespace};
{projectUsings}
public static class {className}
{{
    public static object Run()
    {{
        {code}
        return null;
    }}
}}";
        }

        // Project script assemblies only change with a domain reload, which also clears this.
        private static string s_projectNamespaceUsings;

        internal static string GetReachableProjectNamespaceUsings()
        {
            return s_projectNamespaceUsings ?? (s_projectNamespaceUsings = GetReachableProjectNamespaceUsings(
                AppDomain.CurrentDomain.GetAssemblies(), ApplicationPaths.ProjectRoot));
        }

        internal static string GetReachableProjectNamespaceUsings(IEnumerable<Assembly> assemblies, string projectRoot)
        {
            if (assemblies == null || string.IsNullOrEmpty(projectRoot))
                return string.Empty;

            var namespaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in assemblies)
            {
                if (!IsProjectScriptAssembly(assembly, projectRoot))
                    continue;

                foreach (var type in GetLoadableTypes(assembly))
                {
                    if (!string.IsNullOrEmpty(type.Namespace) && IsValidNamespace(type.Namespace))
                        namespaces.Add(type.Namespace);
                }
            }

            if (namespaces.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            foreach (var ns in namespaces.OrderBy(value => value, StringComparer.Ordinal))
                sb.AppendLine($"using {ns};");
            return sb.ToString();
        }

        private static bool IsProjectScriptAssembly(Assembly assembly, string projectRoot)
        {
            if (assembly == null || assembly.IsDynamic)
                return false;

            try
            {
                var location = assembly.Location;
                if (string.IsNullOrEmpty(location) || !File.Exists(location))
                    return false;

                return IsProjectScriptAssemblyPath(location, projectRoot);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsProjectScriptAssemblyPath(string location, string projectRoot)
        {
            if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(projectRoot))
                return false;

            var normalizedLocation = NormalizePath(location);
            var normalizedProjectRoot = NormalizePath(projectRoot);
            return normalizedLocation.StartsWith(normalizedProjectRoot + "/", StringComparison.OrdinalIgnoreCase) &&
                   normalizedLocation.IndexOf("/Library/ScriptAssemblies/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null);
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static bool IsValidNamespace(string ns)
        {
            return Regex.IsMatch(ns, @"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)*$");
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }

        private static string PrependMissingUsings(string code, string projectUsings)
        {
            if (string.IsNullOrEmpty(projectUsings))
                return code;

            var existing = new HashSet<string>();
            var matches = Regex.Matches(code, @"^\s*using\s+([\w.]+)\s*;", RegexOptions.Multiline);
            foreach (Match match in matches)
                existing.Add(match.Groups[1].Value);

            var missing = new StringBuilder();
            foreach (var line in projectUsings.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                var nsMatch = Regex.Match(trimmed, @"using\s+([\w.]+)\s*;");
                if (nsMatch.Success && !existing.Contains(nsMatch.Groups[1].Value))
                    missing.AppendLine(trimmed);
            }

            return missing.Length == 0 ? code : missing + code;
        }

        private static string GetRequiredSnippetUsings(string code)
        {
            if (!UsesUnqualifiedIKitWrightCommand(code))
                return string.Empty;

            return $"using {KitWrightScriptingNamespace};\n";
        }

        // Only the exact case that would otherwise be CS0104, so a snippet that means System.Object
        // without importing UnityEngine keeps its meaning.
        internal static bool NeedsObjectAlias(string code)
        {
            return Regex.IsMatch(code, @"(?<![\w.])Object(?!\w)") &&
                   Regex.IsMatch(code, @"^\s*using\s+System\s*;", RegexOptions.Multiline) &&
                   Regex.IsMatch(code, @"^\s*using\s+UnityEngine\s*;", RegexOptions.Multiline) &&
                   !Regex.IsMatch(code, @"^\s*using\s+Object\s*=", RegexOptions.Multiline);
        }

        internal static string HintForCompileErrors(IEnumerable<ScriptCompilationError> errors)
        {
            foreach (var error in errors)
            {
                if ((error.code == "CS0535" || error.code == "CS0738") &&
                    error.text != null && error.text.Contains("IKitWrightCommand"))
                    return "IKitWrightCommand requires `void Execute(ExecutionContext ctx)`; return data through ctx.ReturnValue.";
            }

            return null;
        }

        internal static bool UsesUnqualifiedIKitWrightCommand(string code)
        {
            if (string.IsNullOrEmpty(code))
                return false;

            return Regex.IsMatch(code, @"(?<![\w.])IKitWrightCommand(?!\w)");
        }
    }
}
