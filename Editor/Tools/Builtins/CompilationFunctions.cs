// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.DI;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Services;
using KitWright.Editor.Services.UnityLogs;
using KitWright.Editor.State;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Compilation")]
    internal static class CompilationFunctions
    {
        private const string ExternalSyncPendingKey = "KitWright_ExternalSync_Pending";
        internal const string ExternalSyncStartedAtKey = "KitWright_ExternalSync_StartedAt";
        private const double ExternalSyncRecoveryMaxAgeSeconds = 120;
        private const int PlayModeErrorCount = 10;
        internal const string PlayModeErrorsKey = "KitWright_ExternalSync_PlayModeErrors";

        [Description("Force Unity to refresh and wait until script compilation is complete without blocking the editor thread. " +
                     "Use this after editing scripts to ensure the latest code is active before entering Play Mode. " +
                     "Returns compilation errors if any, or a success message.")]
        [ReplaySafeTool]
        public static async Task<string> WaitForCompilation(
            [ToolParam("Force a reimport/refresh before waiting", Required = false)] bool force_refresh = true,
            [ToolParam("Maximum seconds to wait for compilation (5-120; a value outside that range is clamped)", Required = false)] int timeout_seconds = 30)
        {
            try
            {
                timeout_seconds = Mathf.Clamp(timeout_seconds, 5, 120);

                var compilationService = GetCompilationService();
                if (compilationService == null)
                    return ToolResultFormatter.Error("COMPILATION_SERVICE_UNAVAILABLE");

                var startTime = DateTime.UtcNow;
                bool completed = await compilationService
                    .WaitForCompilationAsync(force_refresh, timeout_seconds);

                if (!completed)
                {
                    if (compilationService.LastRefreshResult?.ScriptChangesStillPending == true)
                    {
                        return ToolResultFormatter.Error("REFRESH_DID_NOT_START_COMPILATION",
                            new
                            {
                                timeout_seconds,
                                refresh = compilationService.LastRefreshResult.ToResponseData(),
                                hint = "Unity did not begin script compilation after refresh. If a hot-reload or auto-refresh interception plugin is active, trigger a normal Unity script compilation or disable that interception for this operation."
                            });
                    }

                    return ToolResultFormatter.Error("COMPILATION_TIMEOUT", new { timeout_seconds });
                }

                var issues = compilationService.GetCompilationErrors();
                if (!string.Equals(issues, "No compilation errors detected.", StringComparison.Ordinal))
                    return ToolResultFormatter.Error("COMPILATION_FAILED", new { issues });

                double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                return ScriptsNewerThanCompilationError() ?? $"Compilation complete ({elapsed:F1}s). No errors detected.";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("IMPORTANT: This should be the AI's default next step immediately after editing project files outside the Unity Editor. " +
                     "Treat this as required after any external code or asset change. " +
                     "Call it after modifying .cs, .asmdef, .shader, prefabs, scenes, ScriptableObjects, or other Assets files, and before running tests, entering Play Mode, executing follow-up tools, or assuming Unity has imported the latest state. " +
                     "It forces Unity to import external file changes and handles any resulting script compilation or domain reload recovery. " +
                     "wait=true holds the call until compilation finishes and answers with its result (in broker mode it survives the domain reload). " +
                     "exit_play_mode=true ends Play Mode first, keeping its latest console errors in the response, instead of answering PLAY_MODE_ACTIVE. " +
                     "While Hot Reload (SingularityGroup) is patching code, Play Mode included, it answers that plugin's verdict on the latest script edit: " +
                     "verdict applied, COMPILATION_FAILED, or HOT_RELOAD_NEEDS_RECOMPILE when the running code does not match the file. " +
                     "When the plugin is holding patches back (debugger attached, paused in Edit Mode), Edit Mode falls back to a full recompile and Play Mode answers HOT_RELOAD_PAUSED.")]
        public static async Task<string> RequestRecompile(
            [ToolParam("Maximum seconds to wait for compilation (5-120; a value outside that range is clamped)", Required = false)] int timeout_seconds = 30,
            [ToolParam("Hold the call until compilation finishes and answer with the compile result. In broker mode the call is held across the domain reload; without a broker, leave false and call wait_for_compilation.", Required = false)] bool wait = false,
            [ToolParam("When Unity is in Play Mode: keep its latest error/exception console entries in the response, exit Play Mode, then recompile. false (default) answers PLAY_MODE_ACTIVE and leaves Play Mode running.", Required = false)] bool exit_play_mode = false)
        {
            timeout_seconds = Mathf.Clamp(timeout_seconds, 5, 120);

            string playModeErrors = null;
            if (EditorApplication.isPlaying)
            {
                // The plugin patches the running game, so a Play Mode edit gets its verdict rather than a refusal.
                if (!exit_play_mode && Interop.HotReload.IsPatching)
                {
                    return await AwaitHotReloadPatchAsync(timeout_seconds, inPlayMode: true) ??
                           "Unity started compiling the script changes while playing (Script Changes While Playing recompiles them). " +
                           "If this request is interrupted, call get_reload_recovery_status for the final outcome.";
                }

                if (!exit_play_mode)
                {
                    return ToolResultFormatter.Error("PLAY_MODE_ACTIVE", new
                    {
                        hint = "Unity does not process script recompilation or domain reloads while playing. Call exit_play_mode first, then retry request_recompile, or pass exit_play_mode=true."
                    });
                }

                playModeErrors = ReadPlayModeErrors();
                if (playModeErrors != null)
                    SessionState.SetString(PlayModeErrorsKey, playModeErrors);

                MarkExternalSyncPending();
                EditorApplication.isPlaying = false;
                // isPlayingOrWillChangePlaymode only reports a pending switch INTO Play Mode, so it is
                // already false while the exit is still queued and isPlaying has to be waited on.
                Func<bool> inEditMode = () => !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode;
                if (!await WaitForHotReloadOutcomeAsync(inEditMode, TimeSpan.FromSeconds(timeout_seconds)))
                {
                    ClearExternalSyncPending();
                    SessionState.EraseString(PlayModeErrorsKey);
                    return AttachPlayModeErrors(
                        ToolResultFormatter.Error("PLAY_MODE_EXIT_TIMEOUT", new { timeout_seconds }), playModeErrors);
                }
            }

            var result = await RecompileAsync(timeout_seconds, wait);
            SessionState.EraseString(PlayModeErrorsKey);
            return AttachPlayModeErrors(result, playModeErrors);
        }

        private static async Task<string> RecompileAsync(int timeout_seconds, bool wait)
        {
            if (Interop.KitWrightEngine.IsEnabled)
                return ToolResultFormatter.Error("PATCH_ENGINE_ENABLED", new
                {
                    hint = "The KitWright patch engine is running: saved edits are patched live and nothing is compiled. " +
                           "To force a compile, press Ctrl+R in the Editor (Assets > Refresh)."
                });

            MarkExternalSyncPending();

            var refreshResult = await EditorRefreshPipeline.RefreshAndRequestCompilationAsync(
                    forceUpdate: true,
                    verifyScriptChanges: true);

            if (refreshResult.KnownHotReloadDetected && !refreshResult.CompilationOrImportStarted)
            {
                var hotReloadAnswer = await AwaitHotReloadPatchAsync(timeout_seconds, inPlayMode: false);
                if (hotReloadAnswer != null)
                {
                    ClearExternalSyncPending();
                    return hotReloadAnswer;
                }
                // A real compile started: fall through to the normal wait path.
            }

            if (refreshResult.ScriptChangesStillPending)
            {
                ClearExternalSyncPending();
                return ToolResultFormatter.Error("REFRESH_DID_NOT_START_COMPILATION",
                    new
                    {
                        refresh = refreshResult.ToResponseData(),
                        hint = "Unity did not begin script compilation after importing external changes. A hot-reload or auto-refresh interception plugin may be swallowing refresh/compile requests."
                    });
            }

            if (!wait && (EditorApplication.isCompiling || refreshResult.CompilationOrImportStarted))
            {
                ExternalSyncRecoveryTracker.TryCompletePendingRecovery();
                return "External changes imported. Unity started importing or recompiling and may reload the domain. " +
                       $"Refresh strategy: {refreshResult.BuildStrategySummary()}. " +
                       "If this request is interrupted, call get_reload_recovery_status for the final outcome.";
            }

            var compilationService = GetCompilationService();
            if (compilationService == null)
            {
                ClearExternalSyncPending();
                return "External changes imported. Compilation service is unavailable, so compilation status could not be checked.";
            }

            bool completed = wait && refreshResult.CompilationOrImportStarted
                ? await compilationService.WaitAfterRefreshAsync(timeout_seconds)
                : await compilationService.WaitForCompilationAsync(forceRefresh: false, timeoutSeconds: timeout_seconds);

            if (!completed)
            {
                ExternalSyncRecoveryTracker.TryCompletePendingRecovery();
                return "External changes imported. Unity is still compiling in the background. " +
                       "Call get_reload_recovery_status or get_compilation_errors after it finishes.";
            }

            var issues = compilationService.GetCompilationErrors(includeWarnings: true);
            ClearExternalSyncPending();

            if (!string.Equals(issues, "No compilation errors or warnings detected.", StringComparison.Ordinal) &&
                !string.Equals(issues, "No compilation errors detected.", StringComparison.Ordinal))
            {
                return ToolResultFormatter.Error("COMPILATION_FAILED", new { issues });
            }

            return ScriptsNewerThanCompilationError() ?? (refreshResult.CompilationOrImportStarted
                ? "External changes imported. No compilation errors or warnings detected."
                : "No external changes to import. No compilation errors or warnings detected.");
        }

        // A compile that never ran leaves an empty error list, which would read as success.
        internal static string ScriptsNewerThanCompilationError(ScriptChangeState state = null)
        {
            var pending = PendingScriptFileCount(state);
            if (pending == 0)
                return null;

            return ToolResultFormatter.Error("SCRIPT_CHANGES_NOT_COMPILED", new
            {
                pending_script_files = pending,
                hint = EditorApplication.isPlaying
                    ? "Unity does not compile while playing. Call exit_play_mode, then request_recompile."
                    : "These files are newer than the last compilation. Call request_recompile, then wait_for_compilation(force_refresh=false)."
            });
        }

        internal static int PendingScriptFileCount(ScriptChangeState state = null)
        {
            state = state ?? EditorRefreshPipeline.CaptureScriptChangeState(scanForUnknownProjectScripts: false);
            return state.OutOfDateSourceCount + state.UnknownProjectScriptCount;
        }

        internal const string NotCompiledCode = "SCRIPT_CHANGES_NOT_COMPILED";

        internal static bool IsNotCompiledOutcome(string summary) =>
            summary != null && summary.StartsWith(NotCompiledCode, StringComparison.Ordinal);

        // Written after a domain reload, when the error list is empty whether or not a compile ran:
        // leaving Play Mode reloads before RecompileAsync starts, and that is not a finished compile.
        internal static (string Summary, MCPToolCallStatus Status) DescribeRecoveredCompile(string issues, int pendingScriptFiles)
        {
            var hasIssues = !string.Equals(issues, "No compilation errors or warnings detected.", StringComparison.Ordinal) &&
                            !string.Equals(issues, "No compilation errors detected.", StringComparison.Ordinal);

            if (hasIssues)
                return ("External changes were imported, but compilation reported issues.\n" + issues, MCPToolCallStatus.Error);

            if (pendingScriptFiles > 0)
            {
                return (NotCompiledCode + ": the call was cut short by a domain reload before the recompile ran, " +
                        $"and {pendingScriptFiles} script file(s) on disk are still newer than the last compilation. " +
                        "Call request_recompile again.", MCPToolCallStatus.Error);
            }

            return ("External changes were imported and script compilation finished successfully after domain reload.", MCPToolCallStatus.Success);
        }

        [Description("Get the latest Unity script compilation errors from the most recent compilation cycle. " +
                     "While Unity is compiling, the call waits up to wait_seconds for the compile to finish instead of answering 'try again'. " +
                     "If a domain reload interrupts the call, the compile finished and the new code loaded: call it once more for the result. " +
                     "A page cut short by max_entries reports a next_cursor to pass back as cursor for the rest.")]
        [ReadOnlyTool]
        [ReplaySafeTool]
        public static async Task<string> GetCompilationErrors(
            [ToolParam("Maximum number of issues to return", Required = false)] int max_entries = 50,
            [ToolParam("Include warnings in addition to errors", Required = false)] bool include_warnings = false,
            [ToolParam(Paging.CursorParam, Required = false)] int cursor = 0,
            [ToolParam("Seconds to wait for a running compile to finish before answering (0-30, default 20). 0 answers at once.", Required = false)] int wait_seconds = 20)
        {
            var compilationService = GetCompilationService();
            if (compilationService == null)
                return ToolResultFormatter.Error("COMPILATION_SERVICE_UNAVAILABLE");

            if (compilationService.IsCompiling)
            {
                var startedAt = DateTime.UtcNow;
                wait_seconds = Mathf.Clamp(wait_seconds, 0, 30);
                // No ConfigureAwait(false): CaptureScriptChangeState below needs the editor thread back.
                if (wait_seconds == 0 ||
                    !await compilationService.WaitForCompilationAsync(forceRefresh: false, timeoutSeconds: wait_seconds))
                {
                    return "Currently compiling... Please wait and try again. " +
                           $"Waited {(DateTime.UtcNow - startedAt).TotalSeconds:F1}s for it to finish.";
                }
            }

            return EditorRefreshPipeline.AnnotatePendingScriptChanges(
                EditorRefreshPipeline.CaptureScriptChangeState(scanForUnknownProjectScripts: false),
                compilationService.GetCompilationErrors(max_entries, include_warnings, cursor));
        }

        [Description("Get the latest domain reload recovery event, if any. Useful after Unity recompiles scripts and an MCP request gets interrupted.")]
        [ReadOnlyTool]
        [ReplaySafeTool]
        public static string GetReloadRecoveryStatus(
            [ToolParam("Consume and clear the stored recovery event after reading", Required = false)] bool consume = false)
        {
            var info = DomainReloadHandler.GetLastRecoveryInfo(consume);
            if (info == null)
                return "No reload recovery event recorded.";

            return $"Recovery event:\n" +
                   $"- Tool: {info.ToolName}\n" +
                   $"- Status: {info.Status}\n" +
                   $"- Time: {info.Timestamp:O}\n" +
                   $"- Summary: {info.Summary}";
        }

        private static string ReadPlayModeErrors()
        {
            var repository = RootScopeServices.Services?.GetService(typeof(UnityLogsRepository)) as UnityLogsRepository;
            return repository?.GetRecentLogs("error", PlayModeErrorCount, includeStackTrace: true);
        }

        internal static string AttachPlayModeErrors(string result, string playModeErrors)
        {
            if (string.IsNullOrEmpty(playModeErrors))
                return result;

            if (!ToolResultFormatter.IsError(result))
                return ToolResultFormatter.Success(result, new { play_mode_console_errors = playModeErrors });

            var response = JObject.Parse(result);
            var data = response["data"] as JObject;
            if (data == null)
            {
                data = new JObject();
                response["data"] = data;
            }

            data["play_mode_console_errors"] = playModeErrors;
            return response.ToString(Formatting.None);
        }

        private static CompilationService GetCompilationService()
        {
            return RootScopeServices.Services?.GetService(typeof(CompilationService)) as CompilationService
                   ?? CompilationService.Instance;
        }

        // How long the plugin may stay idle with no verdict before the edit counts as one it never saw.
        private static readonly TimeSpan HotReloadQuietGrace = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan HotReloadInlineCheck = TimeSpan.FromSeconds(1.2);

        /// <summary>
        /// Hot Reload's verdict on the newest script edit. Null means Unity started a full compile (the plugin
        /// escalated, or this asked it to outside Play Mode), which the caller then waits for.
        /// </summary>
        private static async Task<string> AwaitHotReloadPatchAsync(int timeoutSeconds, bool inPlayMode)
        {
            // The newest pending script's write time, not this call's: the plugin may have patched it already.
            var since = EditorRefreshPipeline.CaptureScriptChangeState(scanForUnknownProjectScripts: true).NewestChangeUtc;
            if (since == null)
            {
                return ToolResultFormatter.Success(
                    $"{Interop.HotReload.DisplayName} is patching code, and no script changed since the last compilation, so there is nothing to patch.",
                    new { patch_status = Interop.HotReload.GetStatus() });
            }

            var changedAt = since.Value.ToString("O");
            var paused = Interop.HotReload.PausedReason();
            if (paused != null && Interop.HotReload.OutcomeSince(since.Value) == null)
            {
                if (!inPlayMode && Interop.HotReload.TryRequestFullRecompile() &&
                    await WaitForHotReloadOutcomeAsync(() => EditorApplication.isCompiling, HotReloadQuietGrace))
                    return null;
                return ToolResultFormatter.Error("HOT_RELOAD_PAUSED",
                    new { reason = paused, changed_at = changedAt, patch_status = Interop.HotReload.GetStatus() },
                    inPlayMode
                        ? $"{Interop.HotReload.DisplayName} is not patching because {paused}, so the running game still runs the old code. " +
                          "A full recompile ends Play Mode: call request_recompile with exit_play_mode=true."
                        : $"{Interop.HotReload.DisplayName} is not patching because {paused}, and Unity did not start a compile. " +
                          "Use Recompile in the Hot Reload window, then call request_recompile again.");
            }

            string verdict = null;
            var start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < TimeSpan.FromSeconds(timeoutSeconds))
            {
                if (EditorApplication.isCompiling)
                    return null;
                verdict = Interop.HotReload.OutcomeSince(since.Value);
                if (verdict != null)
                    break;
                if (DateTime.UtcNow - start > HotReloadQuietGrace && Interop.HotReload.TryGetBusy() == false)
                    break;
                await Task.Delay(250);
            }

            // The plugin checks for inlined methods on its next 500 ms tick and counts one as a failed patch,
            // so an applied verdict is read again once that tick has passed.
            if (verdict == Interop.HotReload.Applied)
            {
                await Task.Delay(HotReloadInlineCheck);
                verdict = Interop.HotReload.OutcomeSince(since.Value) ?? verdict;
            }

            if (verdict == null)
            {
                return ToolResultFormatter.Error("HOT_RELOAD_NO_OUTCOME",
                    new { changed_at = changedAt, patch_status = Interop.HotReload.GetStatus() },
                    $"{Interop.HotReload.DisplayName} gave no result for the script change made at {changedAt}. Check its window; " +
                    "the running code may not match the file until it patches or Unity recompiles.");
            }

            if (verdict == Interop.HotReload.Applied)
            {
                return ToolResultFormatter.Success(
                    $"{Interop.HotReload.DisplayName} patched the change: the running code matches the file. Unity did not recompile.",
                    new { verdict, changed_at = changedAt, patch_status = Interop.HotReload.GetStatus() });
            }

            if (verdict == Interop.HotReload.NoChange)
            {
                return ToolResultFormatter.Success(
                    $"{Interop.HotReload.DisplayName} found no code change to patch (a comment or formatting edit, or code put back to what was already patched). Unity did not recompile.",
                    new { verdict, changed_at = changedAt, patch_status = Interop.HotReload.GetStatus() });
            }

            if (verdict == Interop.HotReload.CompileError)
            {
                return ToolResultFormatter.Error("COMPILATION_FAILED",
                    new { verdict, issues = Interop.HotReload.ErrorsSince(since.Value, compileErrorsOnly: true), patch_status = Interop.HotReload.GetStatus() });
            }

            // The running code does not match the file. The plugin may recompile on its own; give it a moment.
            if (await WaitForHotReloadOutcomeAsync(() => EditorApplication.isCompiling, TimeSpan.FromSeconds(2)))
                return null;
            if (!inPlayMode && Interop.HotReload.TryRequestFullRecompile() &&
                await WaitForHotReloadOutcomeAsync(() => EditorApplication.isCompiling, HotReloadQuietGrace))
                return null;

            var why = verdict == "inlined"
                ? "patched the methods, but Unity had inlined them into their callers (Release code optimization), so those calls still run the old body; " +
                  "Debug code optimization (the bug icon in the status bar) avoids this"
                : $"could not fully apply the change ({verdict}), so the running code does not match the file";
            // The reasons can name a file someone else changed at the same moment, which the agent should not chase.
            var reasons = Interop.HotReload.ErrorsSince(since.Value, compileErrorsOnly: false);
            return ToolResultFormatter.Error("HOT_RELOAD_NEEDS_RECOMPILE",
                new { verdict, reasons, changed_at = changedAt, patch_status = Interop.HotReload.GetStatus() },
                inPlayMode
                    ? $"{Interop.HotReload.DisplayName} {why}. A full recompile ends Play Mode: call request_recompile with exit_play_mode=true."
                    : $"{Interop.HotReload.DisplayName} {why}, and Unity did not start a compile. " +
                      "Use Recompile in the Hot Reload window, or stop Hot Reload, then call request_recompile again.");
        }

        internal static async Task<bool> WaitForHotReloadOutcomeAsync(Func<bool> compilationStarted, TimeSpan grace)
        {
            var deadline = DateTime.UtcNow + grace;
            while (DateTime.UtcNow < deadline)
            {
                if (compilationStarted())
                    return true;
                await Task.Delay(250);
            }
            return compilationStarted();
        }

        internal static void MarkExternalSyncPending()
        {
            SessionState.SetBool(ExternalSyncPendingKey, true);
            SessionState.SetString(ExternalSyncStartedAtKey, DateTime.UtcNow.ToString("O"));
        }

        internal static void ClearExternalSyncPending()
        {
            SessionState.EraseBool(ExternalSyncPendingKey);
            SessionState.EraseString(ExternalSyncStartedAtKey);
        }

        internal static bool HasPendingExternalSync()
        {
            if (!SessionState.GetBool(ExternalSyncPendingKey, false))
                return false;

            if (IsExternalSyncStillPending(SessionState.GetString(ExternalSyncStartedAtKey, ""), DateTime.UtcNow))
                return true;

            ClearExternalSyncPending();
            return false;
        }

        // An unreadable stamp counts as expired: it used to read as pending for good, and nothing ever cleared it.
        internal static bool IsExternalSyncStillPending(string startedAtText, DateTime nowUtc) =>
            DomainReloadHandler.TryParseUtc(startedAtText, out var startedAt) &&
            (nowUtc - startedAt).TotalSeconds <= ExternalSyncRecoveryMaxAgeSeconds;
    }

    [InitializeOnLoad]
    internal static class ExternalSyncRecoveryTracker
    {
        internal const double NotCompiledGraceSeconds = 25;
        private const double PendingScanIntervalSeconds = 1;
        private const string CleanReport = "No compilation errors detected.";

        private static DateTime _waitStartedAt;
        private static DateTime _nextPendingScanAt;
        private static int _pendingScriptFiles;
        private static bool _compileSeen;
        private static bool _waiting;

        internal static bool IsWaiting => _waiting;

        static ExternalSyncRecoveryTracker()
        {
            TryCompletePendingRecovery();
        }

        internal enum RecoveryStep { Wait, RecordOutcome, RecordNotCompiled }

        internal static void TryCompletePendingRecovery()
        {
            if (!CompilationFunctions.HasPendingExternalSync())
                return;

            _waitStartedAt = DateTime.UtcNow;
            if (!_waiting)
            {
                _compileSeen = false;
                _nextPendingScanAt = DateTime.MinValue;
            }

            if (TryRecord())
            {
                StopWaiting();
                return;
            }

            if (_waiting)
                return;

            _waiting = true;
            EditorApplication.update += WaitForCompileOutcome;
        }

        /// <summary>Recovery gate, extracted so it is testable: while a compile is in flight the
        /// outcome is not known yet, so recovery info must wait instead of being written early.</summary>
        internal static bool ShouldWaitForCompilation()
        {
            return CompilationService.IsActuallyCompiling;
        }

        // Leaving Play Mode reloads the domain before Unity's own compile starts, so stale scripts with
        // no compile seen yet is not an outcome: the recompile is still to come.
        internal static RecoveryStep DecideRecovery(bool compiling, bool compileSeen, int pendingScriptFiles, double waitedSeconds)
        {
            if (compiling)
                return RecoveryStep.Wait;

            if (compileSeen || pendingScriptFiles == 0)
                return RecoveryStep.RecordOutcome;

            return waitedSeconds < NotCompiledGraceSeconds ? RecoveryStep.Wait : RecoveryStep.RecordNotCompiled;
        }

        private static void WaitForCompileOutcome()
        {
            if (!CompilationFunctions.HasPendingExternalSync() || TryRecord())
                StopWaiting();
        }

        private static void StopWaiting()
        {
            _waiting = false;
            EditorApplication.update -= WaitForCompileOutcome;
        }

        private static bool TryRecord()
        {
            var compiling = ShouldWaitForCompilation();
            _compileSeen |= compiling;

            var now = DateTime.UtcNow;
            if (!compiling && !_compileSeen && now >= _nextPendingScanAt)
            {
                _pendingScriptFiles = CompilationFunctions.PendingScriptFileCount();
                _nextPendingScanAt = now.AddSeconds(PendingScanIntervalSeconds);
            }

            switch (DecideRecovery(compiling, _compileSeen, _pendingScriptFiles, (now - _waitStartedAt).TotalSeconds))
            {
                case RecoveryStep.Wait:
                    return false;
                case RecoveryStep.RecordNotCompiled:
                    CompleteRecovery(compileRan: false);
                    return true;
                default:
                    CompleteRecovery(compileRan: true);
                    return true;
            }
        }

        private static void CompleteRecovery(bool compileRan)
        {
            if (!CompilationFunctions.HasPendingExternalSync())
                return;

            CompilationFunctions.ClearExternalSyncPending();

            var playModeErrors = SessionState.GetString(CompilationFunctions.PlayModeErrorsKey, string.Empty);
            SessionState.EraseString(CompilationFunctions.PlayModeErrorsKey);
            var playModeNote = playModeErrors.Length == 0
                ? string.Empty
                : "\n\nPlay Mode console errors before it was exited:\n" + playModeErrors;

            var compilationService = RootScopeServices.Services?.GetService(typeof(CompilationService)) as CompilationService
                                     ?? CompilationService.Instance;
            if (compilationService == null)
            {
                DomainReloadHandler.StoreRecoveryInfo(
                    "request_recompile",
                    MCPToolCallStatus.Error.ToString(),
                    "External changes were imported, but compilation service was unavailable after domain reload." + playModeNote);
                return;
            }

            var outcome = CompilationFunctions.DescribeRecoveredCompile(
                compileRan ? compilationService.GetCompilationErrors(includeWarnings: true) : CleanReport,
                CompilationFunctions.PendingScriptFileCount());
            DomainReloadHandler.StoreRecoveryInfo("request_recompile", outcome.Status.ToString(), outcome.Summary + playModeNote);
        }
    }
}
