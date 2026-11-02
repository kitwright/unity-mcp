// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Services;
using KitWright.Editor.Tools;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.State
{
    /// <summary>
    /// Saves and restores running state across Unity domain reloads (triggered by script recompilation).
    /// Uses SessionState (persists within editor session, cleared on editor restart).
    /// </summary>
    internal static class DomainReloadHandler
    {
        private const string StateKey = "KitWright_ReloadState";
        private const string TimestampKey = "KitWright_ReloadTimestamp";
        private const string PendingFunctionKey = "KitWright_ReloadPendingFunction";
        private const string LastRecoveryInfoKey = "KitWright_LastRecoveryInfo";
        private const string ResumeCountKey = "KitWright_ConsecutiveResumeCount";
        private const string LastResumeTimestampKey = "KitWright_LastResumeTimestamp";

        private const int MaxConsecutiveResumes = 5;
        private const double ResumeCountResetSeconds = 120;
        private const double DeferredCompletionTimeoutSeconds = 15;

        private static bool _registered;
        private static bool _deferredCompletionRegistered;
        private static DateTime _deferredCompletionStartedAt;
        private static StateController _deferredCompletionStateController;

        /// <summary>
        /// Register to receive reload events. Call once (idempotent).
        /// </summary>
        public static void Register(StateController stateController)
        {
            if (_registered) return;
            _registered = true;

            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                SaveState(stateController.CurrentState);
            };
        }

        public static void SaveState(KitWrightState state)
        {
            SessionState.SetString(StateKey, state.ToString());
            SessionState.SetString(TimestampKey, DateTime.UtcNow.ToString("O"));
        }

        public static void SavePendingFunction(FunctionCall functionCall)
        {
            if (functionCall == null)
                return;

            var payload = new Dictionary<string, object>
            {
                ["functionName"] = functionCall.FunctionName ?? string.Empty
            };

            SessionState.SetString(PendingFunctionKey, JsonCodec.Serialize(payload));
        }

        public static void ClearPendingFunction()
        {
            SessionState.EraseString(PendingFunctionKey);
        }

        public static void CompletePendingFunction(StateController stateController)
        {
            // The tool has returned, so it is no longer pending: clear the marker now, before
            // any deferral. A domain reload wipes the deferred callback below but not
            // SessionState, so a marker left set makes recovery report a finished tool as
            // "interrupted" and invite the caller to run it a second time.
            ClearPendingFunction();

            if (ShouldDeferPendingCompletion())
            {
                DeferPendingCompletion(stateController);
                return;
            }

            stateController?.ReturnToPreviousState();
        }

        public static void StoreRecoveryInfo(string toolName, string status, string summary)
        {
            var payload = new Dictionary<string, object>
            {
                ["toolName"] = toolName ?? string.Empty,
                ["status"] = status ?? string.Empty,
                ["summary"] = summary ?? string.Empty,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            };

            SessionState.SetString(LastRecoveryInfoKey, JsonCodec.Serialize(payload));
        }

        public static RecoveryInfo GetLastRecoveryInfo(bool consume = false)
        {
            var infoStr = SessionState.GetString(LastRecoveryInfoKey, "");
            if (consume)
                SessionState.EraseString(LastRecoveryInfoKey);

            if (string.IsNullOrEmpty(infoStr))
                return null;

            try
            {
                var dict = JsonCodec.Deserialize(infoStr) as Dictionary<string, object>;
                if (dict == null)
                    return null;

                var result = new RecoveryInfo
                {
                    ToolName = GetString(dict, "toolName"),
                    Status = GetString(dict, "status"),
                    Summary = GetString(dict, "summary")
                };

                if (TryParseUtc(GetString(dict, "timestamp"), out var timestampUtc))
                {
                    result.TimestampUtc = timestampUtc;
                    result.Timestamp = timestampUtc.ToLocalTime();
                }

                return result;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KitWright] Failed to parse recovery info: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Checks whether auto-resume is allowed based on the consecutive resume counter.
        /// </summary>
        public static bool CanAutoResume()
        {
            var count = SessionState.GetInt(ResumeCountKey, 0);
            var lastTimestampStr = SessionState.GetString(LastResumeTimestampKey, "");

            if (DateTime.TryParse(lastTimestampStr, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var lastTs))
            {
                if ((DateTime.Now - lastTs).TotalSeconds > ResumeCountResetSeconds)
                    count = 0;
            }

            return count < MaxConsecutiveResumes;
        }

        public static void RecordAutoResume()
        {
            var count = SessionState.GetInt(ResumeCountKey, 0);
            var lastTimestampStr = SessionState.GetString(LastResumeTimestampKey, "");

            if (DateTime.TryParse(lastTimestampStr, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var lastTs))
            {
                if ((DateTime.Now - lastTs).TotalSeconds > ResumeCountResetSeconds)
                    count = 0;
            }

            count++;
            SessionState.SetInt(ResumeCountKey, count);
            SessionState.SetString(LastResumeTimestampKey, DateTime.Now.ToString("O"));
        }

        public static void ResetResumeCounter()
        {
            SessionState.SetInt(ResumeCountKey, 0);
            SessionState.EraseString(LastResumeTimestampKey);
        }

        /// <summary>
        /// Checks if there was an interrupted operation before the last domain reload.
        /// Returns the state that was active, or null if nothing was running.
        /// Clears the saved state after reading (one-shot).
        /// </summary>
        public static InterruptedState ConsumeInterruptedState()
        {
            var stateStr = SessionState.GetString(StateKey, "");
            var timestampStr = SessionState.GetString(TimestampKey, "");
            var pendingFunctionStr = SessionState.GetString(PendingFunctionKey, "");
            var pendingFunction = ParsePendingFunction(pendingFunctionStr);

            // Clear after reading
            SessionState.EraseString(StateKey);
            SessionState.EraseString(TimestampKey);
            SessionState.EraseString(PendingFunctionKey);

            if (string.IsNullOrEmpty(stateStr) && pendingFunction == null) return null;

            if (!Enum.TryParse<KitWrightState>(stateStr, out var state))
                state = pendingFunction != null ? KitWrightState.ExecutingFunction : KitWrightState.Initialized;

            if (state == KitWrightState.Initialized && pendingFunction == null)
                return null;

            // A tool that has returned clears its marker first; only the state reset waits behind Play Mode.
            // Nothing was running at the reload, and calling it interrupted invites a rerun of finished work.
            if (state == KitWrightState.ExecutingFunction && pendingFunction == null)
                return null;

            // Discard if too old (> 120 seconds)
            var reloadedAt = DateTime.MinValue;
            if (TryParseUtc(timestampStr, out var ts))
            {
                if ((DateTime.UtcNow - ts).TotalSeconds > 120)
                    return null;

                reloadedAt = ts;
            }

            return new InterruptedState
            {
                State = state,
                PendingFunction = pendingFunction,
                ReloadedAt = reloadedAt
            };
        }

        // Stored UTC so two stamps either side of a DST fall-back still order correctly; a record a
        // previous domain wrote in local time (offset included) reads as the same instant. Anything
        // unparsable stays DateTime.MinValue, which never counts as recorded.
        internal static bool TryParseUtc(string text, out DateTime utc)
        {
            if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
            {
                utc = parsed.UtcDateTime;
                return true;
            }

            utc = DateTime.MinValue;
            return false;
        }

        // The new domain's own recovery code may already have recorded this tool's outcome, with
        // data (Play Mode errors) that the generic interrupted record would overwrite.
        internal static bool IsRecordedSince(RecoveryInfo info, string toolName, DateTime since)
        {
            return info != null &&
                   since > DateTime.MinValue &&
                   info.TimestampUtc >= since.ToUniversalTime() &&
                   string.Equals(info.ToolName, toolName, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ShouldDeferPendingCompletion()
        {
            return EditorApplication.isPlayingOrWillChangePlaymode ||
                   CompilationService.IsActuallyCompiling ||
                   EditorApplication.isUpdating;
        }

        private static void DeferPendingCompletion(StateController stateController)
        {
            _deferredCompletionStateController = stateController;
            _deferredCompletionStartedAt = DateTime.Now;

            if (_deferredCompletionRegistered)
                return;

            _deferredCompletionRegistered = true;
            EditorApplication.update += CompletePendingWhenEditorIsStable;
        }

        private static void CompletePendingWhenEditorIsStable()
        {
            if (ShouldDeferPendingCompletion() &&
                (DateTime.Now - _deferredCompletionStartedAt).TotalSeconds < DeferredCompletionTimeoutSeconds)
            {
                return;
            }

            EditorApplication.update -= CompletePendingWhenEditorIsStable;
            _deferredCompletionRegistered = false;

            _deferredCompletionStateController?.ReturnToPreviousState();
            _deferredCompletionStateController = null;
        }

        private static PendingFunctionInfo ParsePendingFunction(string pendingFunctionStr)
        {
            if (string.IsNullOrEmpty(pendingFunctionStr))
                return null;

            try
            {
                var dict = JsonCodec.Deserialize(pendingFunctionStr) as Dictionary<string, object>;
                if (dict == null)
                    return null;

                return new PendingFunctionInfo
                {
                    FunctionName = GetString(dict, "functionName")
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KitWright] Failed to parse pending function state: {ex.Message}");
                return null;
            }
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            return dict.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
        }

        internal class InterruptedState
        {
            public KitWrightState State;
            public PendingFunctionInfo PendingFunction;
            public DateTime ReloadedAt;

            public string GetDescription()
            {
                if (!string.IsNullOrEmpty(PendingFunction?.FunctionName))
                    return $"Tool '{PendingFunction.FunctionName}' was interrupted by script recompilation.";

                switch (State)
                {
                    case KitWrightState.ExecutingFunction:
                        return "Function execution was interrupted by script recompilation.";
                    default:
                        return "Operation was interrupted by script recompilation.";
                }
            }
        }

        internal class PendingFunctionInfo
        {
            public string FunctionName;
        }

        internal class RecoveryInfo
        {
            public string ToolName;
            public string Status;
            public string Summary;
            // Local, for display and for callers that subtract it from DateTime.Now; ordering uses TimestampUtc.
            public DateTime Timestamp;
            public DateTime TimestampUtc;
        }
    }
}
