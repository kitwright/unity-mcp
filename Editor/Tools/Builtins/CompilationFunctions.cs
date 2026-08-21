// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.DI;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Services;
using KitWright.Editor.State;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Compilation")]
    internal static class CompilationFunctions
    {
        private const string ExternalSyncPendingKey = "KitWright_ExternalSync_Pending";
        private const string ExternalSyncStartedAtKey = "KitWright_ExternalSync_StartedAt";
        private const double ExternalSyncRecoveryMaxAgeSeconds = 120;

        [Description("Force Unity to refresh and wait until script compilation is complete without blocking the editor thread. " +
                     "Use this after editing scripts to ensure the latest code is active before entering Play Mode. " +
                     "Returns compilation errors if any, or a success message.")]
        [ReadOnlyTool]
        public static async Task<string> WaitForCompilation(
            [ToolParam("Force a reimport/refresh before waiting", Required = false)] bool force_refresh = true,
            [ToolParam("Maximum seconds to wait for compilation", Required = false)] int timeout_seconds = 30)
        {
            try
            {
                timeout_seconds = Mathf.Clamp(timeout_seconds, 5, 120);
                NoThrottleLease.Acquire(TimeSpan.FromSeconds(timeout_seconds + 60));

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
                return $"Compilation complete ({elapsed:F1}s). No errors detected.";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("IMPORTANT: This should be the AI's default next step immediately after editing project files outside the Unity Editor. " +
                     "Treat this as required after any external code or asset change. " +
                     "Call it after modifying .cs, .asmdef, .shader, prefabs, scenes, ScriptableObjects, or other Assets files, and before running tests, entering Play Mode, executing follow-up tools, or assuming Unity has imported the latest state. " +
                     "It forces Unity to import external file changes and handles any resulting script compilation or domain reload recovery.")]
        public static async Task<string> RequestRecompile(
            [ToolParam("Maximum seconds to wait for compilation", Required = false)] int timeout_seconds = 30)
        {
            if (EditorApplication.isPlaying)
            {
                return ToolResultFormatter.Error("PLAY_MODE_ACTIVE", new
                {
                    hint = "Unity does not process script recompilation or domain reloads while playing. Call exit_play_mode first, then retry request_recompile."
                });
            }

            MarkExternalSyncPending();
            timeout_seconds = Mathf.Clamp(timeout_seconds, 5, 120);
            NoThrottleLease.Acquire(TimeSpan.FromSeconds(timeout_seconds + 60));

            var refreshResult = await EditorRefreshPipeline.RefreshAndRequestCompilationAsync(
                    forceUpdate: true,
                    verifyScriptChanges: true);

            if (refreshResult.KnownHotReloadDetected && !refreshResult.CompilationOrImportStarted)
            {
                // Within the grace window the plugin either patches or escalates to its own full recompile.
                bool escalatedToCompile = await WaitForHotReloadOutcomeAsync(
                    () => EditorApplication.isCompiling, TimeSpan.FromSeconds(3));

                if (!escalatedToCompile)
                {
                    ClearExternalSyncPending();
                    return ToolResultFormatter.Success(
                        $"Code-patching plugin active ({Interop.HotReload.DisplayName}): this call did not start a Unity compilation — the plugin detours refresh/compile and hot-patches method bodies instead. " +
                        "Its patch outcome (read after a 3s grace period; the newest entries are the most recent file changes) is in patch_status below: " +
                        "a PatchApplied entry naming your member means it is live; PartiallySupportedChange/UndetectedChange/Failure/Error means the running code does NOT match the file.",
                        new { patch_status = Interop.HotReload.GetStatus() });
                }
                // Escalated to a real compile: fall through to the normal wait path.
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

            if (EditorApplication.isCompiling || refreshResult.CompilationOrImportStarted)
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

            bool completed = await compilationService
                .WaitForCompilationAsync(forceRefresh: false, timeoutSeconds: timeout_seconds);

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

            return "External changes imported. No compilation errors or warnings detected.";
        }

        [Description("Get the latest Unity script compilation errors from the most recent compilation cycle.")]
        [ReadOnlyTool]
        public static string GetCompilationErrors(
            [ToolParam("Maximum number of issues to return", Required = false)] int max_entries = 50,
            [ToolParam("Include warnings in addition to errors", Required = false)] bool include_warnings = false)
        {
            var compilationService = GetCompilationService();
            if (compilationService == null)
                return ToolResultFormatter.Error("COMPILATION_SERVICE_UNAVAILABLE");

            if (compilationService.IsCompiling)
                return "Currently compiling... Please wait and try again.";

            return EditorRefreshPipeline.AnnotatePendingScriptChanges(
                EditorRefreshPipeline.CaptureScriptChangeState(scanForUnknownProjectScripts: false),
                compilationService.GetCompilationErrors(max_entries, include_warnings));
        }

        [Description("Get the latest domain reload recovery event, if any. Useful after Unity recompiles scripts and an MCP request gets interrupted.")]
        [ReadOnlyTool]
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

        private static CompilationService GetCompilationService()
        {
            return RootScopeServices.Services?.GetService(typeof(CompilationService)) as CompilationService
                   ?? CompilationService.Instance;
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
            SessionState.SetString(ExternalSyncStartedAtKey, DateTime.Now.ToString("O"));
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

            var startedAtStr = SessionState.GetString(ExternalSyncStartedAtKey, "");
            if (!DateTime.TryParse(startedAtStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var startedAt))
                return true;

            if ((DateTime.Now - startedAt).TotalSeconds <= ExternalSyncRecoveryMaxAgeSeconds)
                return true;

            ClearExternalSyncPending();
            return false;
        }
    }

    [InitializeOnLoad]
    internal static class ExternalSyncRecoveryTracker
    {
        static ExternalSyncRecoveryTracker()
        {
            TryCompletePendingRecovery();
        }

        internal static void TryCompletePendingRecovery()
        {
            if (!CompilationFunctions.HasPendingExternalSync())
                return;

            if (!ShouldWaitForCompilation())
            {
                CompleteRecovery();
                return;
            }

            EditorApplication.update += WaitUntilCompilationEnds;
        }

        /// <summary>Recovery gate, extracted so it is testable: while a compile is in flight the
        /// outcome is not known yet, so recovery info must wait instead of being written early.</summary>
        internal static bool ShouldWaitForCompilation()
        {
            return CompilationService.IsActuallyCompiling;
        }

        private static void WaitUntilCompilationEnds()
        {
            if (ShouldWaitForCompilation())
                return;

            EditorApplication.update -= WaitUntilCompilationEnds;
            CompleteRecovery();
        }

        private static void CompleteRecovery()
        {
            if (!CompilationFunctions.HasPendingExternalSync())
                return;

            CompilationFunctions.ClearExternalSyncPending();

            var compilationService = RootScopeServices.Services?.GetService(typeof(CompilationService)) as CompilationService
                                     ?? CompilationService.Instance;
            if (compilationService == null)
            {
                DomainReloadHandler.StoreRecoveryInfo(
                    "request_recompile",
                    MCPToolCallStatus.Error.ToString(),
                    "External changes were imported, but compilation service was unavailable after domain reload.");
                return;
            }

            var issues = compilationService.GetCompilationErrors(includeWarnings: true);
            var hasIssues = !string.Equals(issues, "No compilation errors or warnings detected.", StringComparison.Ordinal) &&
                            !string.Equals(issues, "No compilation errors detected.", StringComparison.Ordinal);

            if (hasIssues)
            {
                DomainReloadHandler.StoreRecoveryInfo(
                    "request_recompile",
                    MCPToolCallStatus.Error.ToString(),
                    "External changes were imported, but compilation reported issues.\n" + issues);
                return;
            }

            DomainReloadHandler.StoreRecoveryInfo(
                "request_recompile",
                MCPToolCallStatus.Success.ToString(),
                "External changes were imported and script compilation finished successfully after domain reload.");
        }
    }
}
