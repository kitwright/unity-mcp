// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Services.UnityLogs
{
    internal class UnityLogsRepository : IDisposable
    {
        // Play Mode spam pushed an error raised before it out of a 200-entry cache, and the console's
        // Clear on Play had already dropped it too, so a read for errors found none.
        internal const int MaxLogs = 1000;
        private const int MaxStoredMessageChars = 2048;
        private const int PersistedStackTraces = 200;
        private const int MaxStoredStackChars = 4096;
        private const string StateKey = "KitWright.UnityLogs.State";

        // Seqs start at the session's start in unix seconds x 1e6, so they keep rising across Editor
        // restarts and a cursor from an older session is recognisable without a session parameter.
        private const long SeqPerSecond = 1000000;

        private readonly List<LogEntry> _logs = new List<LogEntry>();
        private readonly object _lock = new object();
        private bool _isListening;
        private long _sessionBase;
        private long _nextSeq;
        private int _errorCount;
        private int _warningCount;
        private int _logCount;

        public UnityLogsRepository()
        {
            _sessionBase = DateTimeOffset.UtcNow.ToUnixTimeSeconds() * SeqPerSecond;
            _nextSeq = _sessionBase + 1;
        }

        public void StartListening()
        {
            if (_isListening)
                return;

            _isListening = true;
            // A blob that no longer parses only costs the old entries; throwing here would stop the listener.
            try { Restore(SessionState.GetString(StateKey, null)); }
            catch (JsonException) { }
            SessionState.EraseString(StateKey);
            AssemblyReloadEvents.beforeAssemblyReload += Persist;
            // Threaded, so a log raised while a modal owns the main thread still reaches subscribers.
            Application.logMessageReceivedThreaded += OnLogReceived;
        }

        public void StopListening()
        {
            if (!_isListening)
                return;

            _isListening = false;
            AssemblyReloadEvents.beforeAssemblyReload -= Persist;
            Application.logMessageReceivedThreaded -= OnLogReceived;
        }

        private void Persist() => SessionState.SetString(StateKey, Snapshot());

        internal string Snapshot()
        {
            lock (_lock)
            {
                return JsonConvert.SerializeObject(new State
                {
                    SessionBase = _sessionBase,
                    NextSeq = _nextSeq,
                    Errors = _errorCount,
                    Warnings = _warningCount,
                    Logs = _logCount,
                    Entries = PersistedEntries()
                });
            }
        }

        // A spammed console would hand SessionState several MB on the reload's main thread, mostly stack
        // traces; only the newest keep theirs, which are the ones a reader still asks for.
        private List<LogEntry> PersistedEntries()
        {
            var keepStacksFrom = _logs.Count - PersistedStackTraces;
            var entries = new List<LogEntry>(_logs.Count);
            for (var i = 0; i < _logs.Count; i++)
            {
                var entry = _logs[i];
                entries.Add(i >= keepStacksFrom || string.IsNullOrEmpty(entry.StackTrace)
                    ? entry
                    : new LogEntry { Seq = entry.Seq, Message = entry.Message, Type = entry.Type, Timestamp = entry.Timestamp });
            }

            return entries;
        }

        internal void Restore(string snapshot)
        {
            lock (_lock)
            {
                var state = string.IsNullOrEmpty(snapshot) ? null : JsonConvert.DeserializeObject<State>(snapshot);
                if (state == null)
                    return;

                _sessionBase = state.SessionBase;
                _errorCount = state.Errors;
                _warningCount = state.Warnings;
                _logCount = state.Logs;
                _logs.Clear();
                if (state.Entries != null)
                    _logs.AddRange(state.Entries);
                var lastSeq = _logs.Count > 0 ? _logs[_logs.Count - 1].Seq : 0;
                _nextSeq = Math.Max(state.NextSeq, lastSeq + 1);
            }
        }

        // Session totals, counted as entries arrive, so an error that fell out of the cache still shows.
        internal string CountsSuffix()
        {
            lock (_lock)
                return $", this session: {_errorCount} error(s), {_warningCount} warning(s), {_logCount} log(s)";
        }

        public string GetRecentLogs(string logType = "all", int count = 30, int sinceSeconds = 0,
            string filterText = null, bool groupDuplicates = false, bool includeStackTrace = false,
            bool includeTimestamps = false, int cursor = 0, long sinceSeq = 0)
        {
            count = Mathf.Clamp(count, 1, 200);
            var filter = (logType ?? "all").ToLowerInvariant();
            var cutoff = sinceSeconds > 0 ? DateTime.Now.AddSeconds(-sinceSeconds) : (DateTime?)null;

            List<LogEntry> snapshot;
            long latestSeq;
            long sessionBase;
            lock (_lock)
            {
                snapshot = new List<LogEntry>(_logs);
                latestSeq = _nextSeq - 1;
                sessionBase = _sessionBase;
            }

            var seqSuffix = string.Empty;
            if (sinceSeq > 0)
            {
                // A cursor from another session (older base, or ahead of everything here) reads from the start.
                if (sinceSeq < sessionBase || sinceSeq > latestSeq)
                {
                    seqSuffix = ", reset: true (since_seq is from another Editor session)";
                    sinceSeq = 0;
                }
                else
                {
                    var oldest = snapshot.Count > 0 ? snapshot[0].Seq : latestSeq + 1;
                    var dropped = Math.Max(0, oldest - sinceSeq - 1);
                    if (dropped > 0)
                        seqSuffix = $", dropped: {dropped} entries after since_seq fell out of the cache";
                    snapshot.RemoveAll(entry => entry.Seq <= sinceSeq);
                }
            }

            if (snapshot.Count == 0)
                return sinceSeq > 0 ? NothingNew(filter, latestSeq, seqSuffix) : null;

            // Every match, not just the first page: the cache is capped at MaxLogs, so collecting
            // the lot is what makes a total (and therefore a cursor) reportable at all.
            var matches = new List<(LogEntry entry, string body)>();
            for (int i = snapshot.Count - 1; i >= 0; i--)
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

                matches.Add((entry, body));
            }

            // null, not a sentence: a non-empty string reads as an answer, and source "auto" returns
            // the first non-empty result. Wording it here meant that as soon as one log was cached,
            // a filter matching nothing in the cache ended the call instead of falling through to
            // the console - which still held the matches. Both callers phrase the empty case
            // themselves.
            if (matches.Count == 0)
                return sinceSeq > 0 ? NothingNew(filter, latestSeq, seqSuffix) : null;

            var total = matches.Count;
            var readSeq = latestSeq;
            List<(LogEntry entry, string body)> page;
            string pageSuffix;
            if (sinceSeq > 0 && total > count)
            {
                // Newest first would show the latest page and skip the older unread ones under a
                // latest_seq past them, so a follow read takes the oldest unread and moves its seq there.
                var start = total - count;
                page = matches.GetRange(start, count);
                readSeq = page[0].entry.Seq;
                pageSuffix = $" {start} newer entries left; call again with since_seq={readSeq}.";
            }
            else
            {
                page = Paging.Page(matches, cursor, count);
                // An answer, not null: the cache did hold entries, so falling through to the console
                // reader here would silently hand back its page one under a cursor that asked for more.
                if (page.Count == 0)
                    return $"Console logs (filter: {filter}, source: cache):{Paging.Suffix(cursor, 0, total)}";
                pageSuffix = Paging.Suffix(cursor, page.Count, total);
            }

            var pagedLines = new List<string>(page.Count);
            var pagedStamps = includeTimestamps ? new List<string>(page.Count) : null;
            foreach (var (entry, body) in page)
            {
                var stackSuffix = includeStackTrace ? FormatStackTrace(entry.StackTrace) : string.Empty;
                pagedLines.Add($"[{ToLabel(entry.Type)}] {TruncateLine(body)}{stackSuffix}");
                pagedStamps?.Add(entry.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " ");
            }

            seqSuffix = $", latest_seq: {readSeq}" + seqSuffix + CountsSuffix();

            var sb = new StringBuilder();
            var uniqueCount = AppendLines(sb, pagedLines, groupDuplicates, pagedStamps);

            var timeSuffix = sinceSeconds > 0 ? $", last {sinceSeconds}s" : string.Empty;
            var textSuffix = string.IsNullOrEmpty(filterText) ? string.Empty : $", text: '{filterText}'";
            var groupSuffix = groupDuplicates && uniqueCount < pagedLines.Count
                ? $", {uniqueCount} unique"
                : string.Empty;
            return $"Console logs ({pagedLines.Count} entries{groupSuffix}, filter: {filter}, source: cache{timeSuffix}{textSuffix}{seqSuffix}):" +
                   $"{pageSuffix}\n{sb}";
        }

        // Never null: with a since_seq the caller asked the cache only, and the console holds no seqs.
        private string NothingNew(string filter, long latestSeq, string seqSuffix) =>
            $"No new {filter} entries (latest_seq: {latestSeq}{seqSuffix}{CountsSuffix()})";

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
                    Seq = _nextSeq++,
                    Message = Cap(message, MaxStoredMessageChars),
                    StackTrace = Cap(stackTrace, MaxStoredStackChars),
                    Type = type,
                    Timestamp = DateTime.Now
                });

                if (type == LogType.Warning) _warningCount++;
                else if (type == LogType.Log) _logCount++;
                else _errorCount++;

                if (_logs.Count > MaxLogs)
                    _logs.RemoveRange(0, _logs.Count - MaxLogs);
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

        private static string Cap(string text, int max) =>
            text == null || text.Length <= max ? text : text.Substring(0, max);

        private sealed class State
        {
            public long SessionBase { get; set; }
            public long NextSeq { get; set; }
            public int Errors { get; set; }
            public int Warnings { get; set; }
            public int Logs { get; set; }
            public List<LogEntry> Entries { get; set; }
        }

        private sealed class LogEntry
        {
            public long Seq { get; set; }
            public string Message { get; set; }
            public string StackTrace { get; set; }
            public LogType Type { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}
