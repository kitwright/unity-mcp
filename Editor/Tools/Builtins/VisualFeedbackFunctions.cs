// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.DI;
using KitWright.Editor.Services.UnityLogs;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Visual")]
    internal static class VisualFeedbackFunctions
    {
        [Description("Select a GameObject in the scene hierarchy and inspector")]
        [ReadOnlyTool]
        public static string SelectObject(
            [ToolParam("GameObject name, hierarchy path, or instance ID. Finds inactive objects too.")] string name)
        {
            var go = ObjectsHelper.FindTarget(name);
            if (go == null)
                return ObjectsHelper.NotFoundText("name", name);

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            return $"Selected '{go.name}'";
        }

        [Description("Focus the Scene View camera on a specific GameObject")]
        [ReadOnlyTool]
        public static string FocusOnObject(
            [ToolParam("GameObject name, hierarchy path, or instance ID. Finds inactive objects too.")] string name)
        {
            var go = ObjectsHelper.FindTarget(name);
            if (go == null)
                return ObjectsHelper.NotFoundText("name", name);

            Selection.activeGameObject = go;
            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.FrameSelected();
            }
            return $"Focused scene view on '{go.name}'";
        }

        [Description("Ping/highlight an asset in the Project window")]
        [ReadOnlyTool]
        public static string PingAsset(
            [ToolParam("Path to the asset")] string path)
        {
            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (obj == null)
                return ToolResultFormatter.Error("ASSET_NOT_FOUND", new { path });

            EditorGUIUtility.PingObject(obj);
            return $"Pinged asset at '{path}'";
        }

        [Description("Log a message to the Unity console")]
        [ReadOnlyTool]
        public static string LogMessage(
            [ToolParam("Message to log")] string message,
            [ToolParam("Log type: info, warning, error", Required = false)] string log_type = "info")
        {
            // Not the "[KitWright]" prefix the plugin uses for its own chatter: both read paths of
            // get_console_logs drop that prefix on purpose, so a message logged through this tool
            // could never be read back through the tool that exists to read it.
            switch (log_type.ToLowerInvariant())
            {
                case "warning": Debug.LogWarning($"[MCP] {message}"); break;
                case "error": Debug.LogError($"[MCP] {message}"); break;
                default: Debug.Log($"[MCP] {message}"); break;
            }
            return $"Logged {log_type}: {message}";
        }

        // A modal dialog blocks the editor main loop, which stalls the MCP request pump until a
        // human clicks it, so this shows a non-blocking Scene View notification instead.
        [Description("Show a message to the user as a non-blocking notification in the Scene View (also written to the console)")]
        [ReadOnlyTool]
        public static string ShowDialog(
            [ToolParam("Message title")] string title,
            [ToolParam("Message body")] string message)
        {
            EditorWindow window = SceneView.lastActiveSceneView;
            if (window == null)
                window = EditorWindow.focusedWindow;
            if (window != null)
                window.ShowNotification(new GUIContent($"{title}\n{message}"));
            Debug.Log($"[MCP] {title}: {message}");
            return $"Showed notification: {title}";
        }

        [Description("Get recent console log messages from Unity. " +
                     "Returns Debug.Log, Debug.LogWarning, and Debug.LogError output. " +
                     "Useful for checking runtime behavior after play mode actions. " +
                     "Supports reading from the live log cache, clearing the cache, time-based filtering, " +
                     "case-insensitive text filtering, collapsing repeated identical messages into one " +
                     "'message (xN)' line so spammy warnings don't drown out unique entries, and optionally " +
                     "including each entry's stack trace (truncated separately from the message) or the time " +
                     "each entry was logged. Unity rich-text markup (<color>, <b>, ...) is stripped from every " +
                     "message, so filter_text matches the readable text rather than the markup.")]
        [ReadOnlyTool]
        public static string GetConsoleLogs(
            [ToolParam("Filter by log type: 'all', 'log', 'warning', 'error'", Required = false)] string log_type = "all",
            [ToolParam("Maximum number of entries to return", Required = false)] int count = 30,
            [ToolParam("Source: 'auto', 'cache', or 'console'", Required = false)] string source = "auto",
            [ToolParam("Clear the cached logs before reading", Required = false)] bool clear_cache = false,
            [ToolParam("Only include cached log entries from the last N seconds (cache/auto only)", Required = false)] int since_seconds = 0,
            [ToolParam("Only include entries whose message contains this text (case-insensitive)", Required = false)] string filter_text = null,
            [ToolParam("Collapse repeated identical messages into one line with a (xN) count", Required = false)] bool group_duplicates = false,
            [ToolParam("Include each entry's stack trace, indented below the message (its own truncation cap, separate from the message's).", Required = false)] bool include_stack_trace = false,
            [ToolParam("Prefix each entry with the HH:mm:ss it was logged (cache/auto only; the Editor console keeps no timestamps).", Required = false)] bool include_timestamps = false)
        {
            count = Mathf.Clamp(count, 1, 200);
            since_seconds = Mathf.Clamp(since_seconds, 0, 86400);
            source = string.IsNullOrEmpty(source) ? "auto" : source.ToLowerInvariant();

            var logsRepository = RootScopeServices.Services?.GetService(typeof(UnityLogsRepository)) as UnityLogsRepository;
            logsRepository?.StartListening();

            if (clear_cache)
                logsRepository?.Clear();

            if (source != "auto" && source != "cache" && source != "console")
                return ToolResultFormatter.Error("INVALID_SOURCE", new { source, accepted = new[] { "auto", "cache", "console" } });

            if (source == "cache" || source == "auto")
            {
                var cachedLogs = logsRepository?.GetRecentLogs(log_type, count, since_seconds, filter_text, group_duplicates, include_stack_trace, include_timestamps);
                if (!string.IsNullOrEmpty(cachedLogs))
                    return cachedLogs;

                if (source == "cache")
                    return since_seconds > 0
                        ? $"No {log_type} entries found in cached logs from the last {since_seconds} second(s)"
                        : $"No {log_type} entries found in cached logs";
            }

            if (since_seconds > 0)
                return ToolResultFormatter.Error("INVALID_SINCE_SECONDS", new { since_seconds, hint = "since_seconds is only supported when reading from cache or auto mode with cached results." });

            var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
            if (logEntriesType == null)
                return ToolResultFormatter.Error("UNITY_CONSOLE_UNAVAILABLE", new { message = "LogEntries API not found" });

            var getCountMethod = logEntriesType.GetMethod("GetCount",
                BindingFlags.Public | BindingFlags.Static);
            var startMethod = logEntriesType.GetMethod("StartGettingEntries",
                BindingFlags.Public | BindingFlags.Static);
            var endMethod = logEntriesType.GetMethod("EndGettingEntries",
                BindingFlags.Public | BindingFlags.Static);
            var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal",
                BindingFlags.Public | BindingFlags.Static);

            if (getCountMethod == null || startMethod == null || endMethod == null || getEntryMethod == null)
                return ToolResultFormatter.Error("UNITY_CONSOLE_API_INCOMPATIBLE", new { message = "LogEntries API methods not found" });

            var logEntryType = Type.GetType("UnityEditor.LogEntry, UnityEditor");
            if (logEntryType == null) return ToolResultFormatter.Error("UNITY_CONSOLE_API_INCOMPATIBLE", new { message = "LogEntry type not found" });

            var modeField = logEntryType.GetField("mode",
                BindingFlags.Public | BindingFlags.Instance);
            var messageField = logEntryType.GetField("message",
                BindingFlags.Public | BindingFlags.Instance);

            if (modeField == null || messageField == null) return ToolResultFormatter.Error("UNITY_CONSOLE_API_INCOMPATIBLE", new { message = "LogEntry fields not found" });

            // LogEntries mirrors the Console window: its severity toggles filter GetCount and
            // GetEntryInternal, so a user who muted Log+Warning while chasing an error would be
            // told the console is empty while it is full. Force the level bits on for the read and
            // restore the user's flags after. Mechanism from CoplayDev/unity-mcp issue #1239 (MIT).
            var forcedBits = ForceConsoleLevelBits(logEntriesType, out var setFlagMethod);
            try
            {
                int totalCount = (int)getCountMethod.Invoke(null, null);
                if (totalCount == 0)
                    return "Console reports no entries (the Console window's search box, if set, also filters this read)";

                startMethod.Invoke(null, null);
                try
                {
                var lines = new System.Collections.Generic.List<string>();

                for (int i = totalCount - 1; i >= 0 && lines.Count < count; i--)
                {
                    var entry = Activator.CreateInstance(logEntryType);
                    getEntryMethod.Invoke(null, new object[] { i, entry });

                    int mode = (int)modeField.GetValue(entry);
                    string message = (string)messageField.GetValue(entry);

                    if (message != null &&
                        message.StartsWith("[KitWright", StringComparison.Ordinal)) continue;

                    // Classify: ERROR (bits 0,1,4,8,11), WARN (bits 9,12), LOG (others)
                    const int errorMask = 1 | (1 << 1) | (1 << 4) | (1 << 8) | (1 << 11);
                    const int warningMask = (1 << 9) | (1 << 12);

                    bool isError = (mode & errorMask) != 0;
                    bool isWarning = !isError && (mode & warningMask) != 0;

                    string typeLabel;
                    if (isError) typeLabel = "ERROR";
                    else if (isWarning) typeLabel = "WARN";
                    else typeLabel = "LOG";

                    string filterLower = log_type.ToLowerInvariant();
                    if (filterLower == "error" && !isError) continue;
                    if (filterLower == "warning" && !isWarning) continue;
                    if (filterLower == "log" && (isError || isWarning)) continue;

                    // LogEntries concatenates "message\nstackTrace" into a single string; split it
                    // at the first stack frame so a multi-line message body survives intact and the
                    // (optional) trace still gets its own truncation cap.
                    UnityLogsRepository.SplitMessageAndStackTrace(message, out var messageBody, out var messageStack);
                    var body = UnityLogsRepository.StripRichText(messageBody ?? string.Empty);
                    if (!UnityLogsRepository.MatchesTextFilter(body, filter_text))
                        continue;

                    var stackSuffix = include_stack_trace
                        ? UnityLogsRepository.FormatStackTrace(messageStack)
                        : string.Empty;

                    lines.Add($"[{typeLabel}] {UnityLogsRepository.TruncateLine(body)}{stackSuffix}");
                }

                if (lines.Count == 0)
                    return $"No {log_type} entries matched in console (the Console window's search box, if set, also filters this read)";

                var sb = new StringBuilder();
                var uniqueCount = UnityLogsRepository.AppendLines(sb, lines, group_duplicates);

                var textSuffix = string.IsNullOrEmpty(filter_text) ? string.Empty : $", text: '{filter_text}'";
                var groupSuffix = group_duplicates && uniqueCount < lines.Count
                    ? $", {uniqueCount} unique"
                    : string.Empty;
                return $"Console logs ({lines.Count} entries{groupSuffix}, filter: {log_type}, source: console{textSuffix}):\n{sb}";
                }
                finally
                {
                    // Only paired with StartGettingEntries, so it stays inside that branch.
                    endMethod.Invoke(null, null);
                }
            }
            finally
            {
                foreach (var bit in forcedBits)
                    setFlagMethod.Invoke(null, new object[] { bit, false });
            }
        }

        // LogLevelLog, LogLevelWarning, LogLevelError in UnityEditor's console flags.
        internal static readonly int[] ConsoleLevelBits = { 1 << 7, 1 << 8, 1 << 9 };

        internal static int[] MissingConsoleLevelBits(int consoleFlags)
        {
            return ConsoleLevelBits.Where(bit => (consoleFlags & bit) == 0).ToArray();
        }

        private static int[] ForceConsoleLevelBits(Type logEntriesType, out MethodInfo setFlagMethod)
        {
            const BindingFlags anyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            setFlagMethod = logEntriesType.GetMethod("SetConsoleFlag", anyStatic);
            var flagsProperty = logEntriesType.GetProperty("consoleFlags", anyStatic);
            if (setFlagMethod == null || !(flagsProperty?.GetValue(null, null) is int consoleFlags))
                return Array.Empty<int>();

            var forced = MissingConsoleLevelBits(consoleFlags);
            foreach (var bit in forced)
                setFlagMethod.Invoke(null, new object[] { bit, true });
            return forced;
        }

        [Description("Clear the Unity Editor console. Removes all Debug.Log/Warning/Error entries from the Console window and the live log cache.")]
        public static object ClearConsole()
        {
            var logsRepository = RootScopeServices.Services?.GetService(typeof(UnityLogsRepository)) as UnityLogsRepository;
            logsRepository?.Clear();

            var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
            if (logEntriesType == null)
                return Response.Error("UNITY_CONSOLE_UNAVAILABLE", new { message = "LogEntries API not found" });

            var clearMethod = logEntriesType.GetMethod("Clear", BindingFlags.Public | BindingFlags.Static);
            if (clearMethod == null)
                return Response.Error("UNITY_CONSOLE_API_INCOMPATIBLE", new { message = "LogEntries.Clear method not found" });

            clearMethod.Invoke(null, null);
            return Response.Success("Console cleared.");
        }
    }
}
