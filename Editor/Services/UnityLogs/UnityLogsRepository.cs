// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace KitWright.Editor.Services.UnityLogs
{
    internal class UnityLogsRepository : IDisposable
    {
        private const int MaxLogs = 200;

        private readonly List<LogEntry> _logs = new List<LogEntry>();
        private readonly object _lock = new object();
        private bool _isListening;

        public void StartListening()
        {
            if (_isListening)
                return;

            _isListening = true;
            // Threaded, so a log raised while a modal owns the main thread still reaches subscribers.
            Application.logMessageReceivedThreaded += OnLogReceived;
        }

        public void StopListening()
        {
            if (!_isListening)
                return;

            _isListening = false;
            Application.logMessageReceivedThreaded -= OnLogReceived;
        }

        public string GetRecentLogs(string logType = "all", int count = 30, int sinceSeconds = 0,
            string filterText = null, bool groupDuplicates = false, bool includeStackTrace = false,
            bool includeTimestamps = false)
        {
            count = Mathf.Clamp(count, 1, 200);
            var filter = (logType ?? "all").ToLowerInvariant();
            var cutoff = sinceSeconds > 0 ? DateTime.Now.AddSeconds(-sinceSeconds) : (DateTime?)null;

            List<LogEntry> snapshot;
            lock (_lock)
            {
                snapshot = new List<LogEntry>(_logs);
            }

            if (snapshot.Count == 0)
                return null;

            var lines = new List<string>();
            var stamps = includeTimestamps ? new List<string>() : null;

            for (int i = snapshot.Count - 1; i >= 0 && lines.Count < count; i--)
            {
                var entry = snapshot[i];
                if (cutoff.HasValue && entry.Timestamp < cutoff.Value)
                    continue;

                if (!MatchesFilter(entry.Type, filter))
                    continue;

                // The whole body, not just its first line: the trace is a separate field here, so
                // dropping lines would lose message text no flag can bring back.
                var body = StripRichText(entry.Message);
                if (!MatchesTextFilter(body, filterText))
                    continue;

                var stackSuffix = includeStackTrace ? FormatStackTrace(entry.StackTrace) : string.Empty;
                lines.Add($"[{ToLabel(entry.Type)}] {TruncateLine(body)}{stackSuffix}");
                stamps?.Add(entry.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " ");
            }

            // null, not a sentence: a non-empty string reads as an answer, and source "auto" returns
            // the first non-empty result. Wording it here meant that as soon as one log was cached,
            // a filter matching nothing in the cache ended the call instead of falling through to
            // the console - which still held the matches. Both callers phrase the empty case
            // themselves.
            if (lines.Count == 0)
                return null;

            var sb = new StringBuilder();
            var uniqueCount = AppendLines(sb, lines, groupDuplicates, stamps);

            var timeSuffix = sinceSeconds > 0 ? $", last {sinceSeconds}s" : string.Empty;
            var textSuffix = string.IsNullOrEmpty(filterText) ? string.Empty : $", text: '{filterText}'";
            var groupSuffix = groupDuplicates && uniqueCount < lines.Count
                ? $", {uniqueCount} unique"
                : string.Empty;
            return $"Console logs ({lines.Count} entries{groupSuffix}, filter: {filter}, source: cache{timeSuffix}{textSuffix}):\n{sb}";
        }

        // Whitelisted tag names only, so a log containing List<int> or XML survives.
        private static readonly Regex RichTextTag = new Regex(
            @"</?(?:b|i|u|s|color|size|material|quad|sprite|align|alpha|allcaps|cspace|font|gradient|indent|line-height|line-indent|link|lowercase|uppercase|smallcaps|margin|mark|mspace|nobr|noparse|page|pos|rotate|space|style|sub|sup|voffset|width|br)(?:=[^<>]*)?\s*/?>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static string StripRichText(string line)
        {
            if (string.IsNullOrEmpty(line) || line.IndexOf('<') < 0)
                return line;
            return RichTextTag.Replace(line, string.Empty);
        }

        // Some sources (UnityEditor.LogEntries) hand back "message\nstackTrace" as one blob, and the
        // message body itself can span several lines. Split at the first line that looks like a stack
        // frame; with none found the whole blob stays the body.
        // Heuristic adapted from CoplayDev/unity-mcp, MCPForUnity/Editor/Tools/ReadConsole.cs (MIT).
        internal static void SplitMessageAndStackTrace(string blob, out string body, out string stackTrace)
        {
            body = blob;
            stackTrace = null;
            if (string.IsNullOrEmpty(blob))
                return;

            var lines = blob.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith("at ", StringComparison.Ordinal) &&
                    !trimmed.StartsWith("UnityEngine.", StringComparison.Ordinal) &&
                    !trimmed.StartsWith("UnityEditor.", StringComparison.Ordinal) &&
                    trimmed.IndexOf("(at ", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                body = string.Join("\n", lines, 0, i);
                stackTrace = string.Join("\n", lines, i, lines.Length - i);
                return;
            }
        }

        internal static bool MatchesTextFilter(string line, string filterText)
        {
            if (string.IsNullOrEmpty(filterText))
                return true;
            return line != null && line.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // A single log line can be enormous (e.g. an entire save-file JSON dumped to the console).
        // Cap emitted lines so one such entry cannot blow up the whole tool response.
        private const int MaxEmittedLineLength = 300;

        internal static string TruncateLine(string line)
        {
            if (string.IsNullOrEmpty(line) || line.Length <= MaxEmittedLineLength)
                return line;
            return line.Substring(0, MaxEmittedLineLength) + $"... (+{line.Length - MaxEmittedLineLength} chars)";
        }

        // Stack traces get their own, larger cap: MaxEmittedLineLength (300) is sized for a
        // single message line, but a stack trace is legitimately many lines and callers ask
        // for it specifically to see those frames -- still capped so one giant trace can't
        private const int MaxStackTraceLength = 2000;

        internal static string FormatStackTrace(string stackTrace)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
                return string.Empty;

            var trimmed = stackTrace.TrimEnd();
            var truncated = trimmed.Length > MaxStackTraceLength
                ? trimmed.Substring(0, MaxStackTraceLength) + $"... (+{trimmed.Length - MaxStackTraceLength} chars)"
                : trimmed;

            var sb = new StringBuilder();
            var normalized = truncated.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (var line in normalized.Split('\n'))
            {
                sb.Append('\n');
                sb.Append("    ");
                sb.Append(line);
            }
            return sb.ToString();
        }

        // Appends lines to the builder; with grouping, identical lines collapse to one
        // "line (xN)" entry in first-seen order. Returns the number of lines written.
        // Prefixes stay out of the grouping key so timestamps don't split identical spam.
        internal static int AppendLines(StringBuilder sb, List<string> lines, bool groupDuplicates,
            List<string> prefixes = null)
        {
            if (!groupDuplicates)
            {
                for (int i = 0; i < lines.Count; i++)
                    sb.AppendLine(PrefixAt(prefixes, i) + lines[i]);
                return lines.Count;
            }

            var order = new List<string>();
            var counts = new Dictionary<string, int>();
            var firstIndex = new Dictionary<string, int>();
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (counts.TryGetValue(line, out var existing))
                {
                    counts[line] = existing + 1;
                }
                else
                {
                    counts[line] = 1;
                    firstIndex[line] = i;
                    order.Add(line);
                }
            }

            foreach (var line in order)
            {
                var n = counts[line];
                sb.AppendLine(PrefixAt(prefixes, firstIndex[line]) + (n > 1 ? $"{line} (x{n})" : line));
            }
            return order.Count;
        }

        private static string PrefixAt(List<string> prefixes, int index)
        {
            return prefixes != null && index < prefixes.Count ? prefixes[index] : string.Empty;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _logs.Clear();
            }
        }

        private void OnLogReceived(string message, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(message))
                return;

            if (message.StartsWith("[KitWright]", StringComparison.Ordinal) ||
                message.StartsWith("[KitWright MCP Server]", StringComparison.Ordinal))
            {
                return;
            }

            lock (_lock)
            {
                _logs.Add(new LogEntry
                {
                    Message = message,
                    StackTrace = stackTrace,
                    Type = type,
                    Timestamp = DateTime.Now
                });

                while (_logs.Count > MaxLogs)
                    _logs.RemoveAt(0);
            }

            // Every Unity log lands here, so don't even allocate the async state machine when
            // no SSE session could receive the notification.
            if (MCP.Server.SSE.SSESessionManager.Instance.HasLogSubscribers)
                _ = MCP.Server.SSE.SSESessionManager.Instance.BroadcastLogNotificationAsync(type, message, stackTrace);
        }

        private static bool MatchesFilter(LogType type, string filter)
        {
            switch (filter)
            {
                case "error":
                    return type == LogType.Error || type == LogType.Assert || type == LogType.Exception;
                case "warning":
                    return type == LogType.Warning;
                case "log":
                    return type == LogType.Log;
                default:
                    return true;
            }
        }

        private static string ToLabel(LogType type)
        {
            switch (type)
            {
                case LogType.Warning:
                    return "WARN";
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    return "ERROR";
                default:
                    return "LOG";
            }
        }

        public void Dispose()
        {
            StopListening();
        }

        private class LogEntry
        {
            public string Message { get; set; }
            public string StackTrace { get; set; }
            public LogType Type { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}
