// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace KitWright.Editor.MCP.Server.SSE
{
    public enum AttachStreamResult
    {
        Success,
        SessionNotFound,
        StreamAlreadyAttached
    }

    /// <summary>
    /// Manages active MCP Streamable HTTP / Server-Sent Events sessions.
    /// </summary>
    internal sealed class SSESessionManager
    {
        private static readonly Lazy<SSESessionManager> s_instance =
            new Lazy<SSESessionManager>(() => new SSESessionManager());
        public static SSESessionManager Instance => s_instance.Value;

        private static readonly Dictionary<string, int> SeverityRanks =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "debug", 0 },
                { "info", 1 },
                { "notice", 2 },
                { "warning", 3 },
                { "error", 4 },
                { "critical", 5 },
                { "alert", 6 },
                { "emergency", 7 }
            };

        public sealed class SSESession
        {
            public string SessionId { get; }
            public DateTime LastActiveAt { get; set; }
            public int? MinSeverityLevel { get; set; }
            public NetworkStream ActiveStream { get; set; }
            public object StreamLock { get; } = new object();
            public SemaphoreSlim WriteGate { get; } = new SemaphoreSlim(1, 1);

            public SSESession(string sessionId)
            {
                SessionId = sessionId;
                LastActiveAt = DateTime.UtcNow;
            }
        }

        private readonly ConcurrentDictionary<string, SSESession> _sessions =
            new ConcurrentDictionary<string, SSESession>(StringComparer.OrdinalIgnoreCase);

        private int? _globalMinSeverityLevel;
        private readonly object _logDedupLock = new object();
        private string _lastLogKey;
        private DateTime _lastLogTime = DateTime.MinValue;
        private int _suppressedLogCount;
        // Settable so a test can pin the window instead of racing it: asserting that N sends
        // collapse into one meant getting N coroutine round trips inside 100 real milliseconds,
        // which a slow frame loses.
        public int LogDedupWindowMs { get; set; } = 100;

        public int PingIntervalMs { get; set; } = 15_000;
        public TimeSpan SessionTtl { get; set; } = TimeSpan.FromMinutes(30);

        public SSESession CreateSession()
        {
            CleanupExpiredSessions();
            var sessionId = Guid.NewGuid().ToString("N");
            var session = new SSESession(sessionId);
            _sessions[sessionId] = session;
            return session;
        }

        public bool TryGetSession(string sessionId, out SSESession session)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                session = null;
                return false;
            }

            if (_sessions.TryGetValue(sessionId, out session))
            {
                session.LastActiveAt = DateTime.UtcNow;
                return true;
            }

            return false;
        }

        public AttachStreamResult TryAttachStream(string sessionId, NetworkStream stream, out SSESession session)
        {
            if (!TryGetSession(sessionId, out session))
                return AttachStreamResult.SessionNotFound;

            lock (session.StreamLock)
            {
                if (session.ActiveStream != null)
                    return AttachStreamResult.StreamAlreadyAttached;

                session.ActiveStream = stream;
                return AttachStreamResult.Success;
            }
        }

        public void DetachStream(SSESession session)
        {
            if (session == null) return;

            lock (session.StreamLock)
            {
                session.ActiveStream = null;
            }
        }

        public void SetLoggingLevel(string sessionId, string levelName)
        {
            var rank = ParseSeverityRank(levelName);
            if (!string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out var session))
            {
                session.MinSeverityLevel = rank;
            }
            else
            {
                _globalMinSeverityLevel = rank;
            }
        }

        public static int? ParseSeverityRank(string levelName)
        {
            if (!string.IsNullOrEmpty(levelName) && SeverityRanks.TryGetValue(levelName, out var rank))
                return rank;
            return null;
        }

        public static string MapLogTypeToSeverity(LogType type)
        {
            switch (type)
            {
                case LogType.Warning: return "warning";
                case LogType.Error:
                case LogType.Assert: return "error";
                case LogType.Exception: return "critical";
                default: return "info";
            }
        }

        /// <summary>False when no session (and no global level) could ever receive a log
        /// notification, so callers can skip building one entirely.</summary>
        internal bool HasLogSubscribers => !_sessions.IsEmpty || _globalMinSeverityLevel.HasValue;

        internal static int NotificationsSerialized;

        public async Task BroadcastLogNotificationAsync(LogType type, string condition, string stackTrace)
        {
            if (!HasLogSubscribers)
                return;

            var severity = MapLogTypeToSeverity(type);
            var rank = SeverityRanks[severity];

            int suppressed;
            lock (_logDedupLock)
            {
                var now = DateTime.UtcNow;
                var key = $"{type}:{condition}";
                if (string.Equals(_lastLogKey, key, StringComparison.Ordinal) &&
                    (now - _lastLogTime).TotalMilliseconds < LogDedupWindowMs)
                {
                    _suppressedLogCount++;
                    return;
                }
                suppressed = _suppressedLogCount;
                _suppressedLogCount = 0;
                _lastLogKey = key;
                _lastLogTime = now;
            }

            var data = string.IsNullOrEmpty(stackTrace) ? condition : $"{condition}\n{stackTrace}";
            if (suppressed > 0)
                data = $"[previous message repeated {suppressed}x]\n{data}";

            Interlocked.Increment(ref NotificationsSerialized);
            var notificationPayload = JsonCodec.Serialize(new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/message",
                ["params"] = new Dictionary<string, object>
                {
                    ["level"] = severity,
                    ["logger"] = "UnityConsole",
                    ["data"] = data
                }
            });

            var eventChunk = $"event: message\ndata: {notificationPayload}\n\n";
            var bytes = Encoding.UTF8.GetBytes(eventChunk);

            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                var minRank = session.MinSeverityLevel ?? _globalMinSeverityLevel;
                if (!minRank.HasValue || rank < minRank.Value)
                    continue;

                await SendRawBytesDirectAsync(session, bytes).ConfigureAwait(false);
            }
        }

        /// <summary>Returns false when the frame could not be written, which means the client is gone.</summary>
        public async Task<bool> SendRawBytesDirectAsync(SSESession session, byte[] bytes)
        {
            if (session == null || bytes == null || bytes.Length == 0)
                return false;

            NetworkStream stream;
            lock (session.StreamLock)
            {
                stream = session.ActiveStream;
            }

            if (stream == null || !stream.CanWrite)
                return false;

            await session.WriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                session.WriteGate.Release();
            }
        }

        public async Task RunSsePingLoopAsync(SSESession session, CancellationToken ct)
        {
            var pingBytes = Encoding.UTF8.GetBytes(": ping\n\n");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(PingIntervalMs, ct).ConfigureAwait(false);
                    if (!await SendRawBytesDirectAsync(session, pingBytes).ConfigureAwait(false))
                        break;
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                DetachStream(session);
            }
        }

        public void CleanupExpiredSessions()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                if (session.ActiveStream == null && (now - session.LastActiveAt) > SessionTtl)
                {
                    _sessions.TryRemove(kvp.Key, out _);
                }
            }
        }

        public void ResetForTests()
        {
            foreach (var kvp in _sessions)
                DetachStream(kvp.Value);

            _sessions.Clear();
            _globalMinSeverityLevel = null;
            NotificationsSerialized = 0;

            lock (_logDedupLock)
            {
                _lastLogKey = null;
                _lastLogTime = DateTime.MinValue;
                _suppressedLogCount = 0;
                LogDedupWindowMs = 100;
            }
        }
    }
}
