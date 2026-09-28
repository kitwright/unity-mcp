// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using KitWright.Editor.Settings;
using UnityEditor;

namespace KitWright.Editor.MCP.Server
{
    /// <summary>
    /// Tracks changes to the exposed MCP tool list across server restarts and domain
    /// reloads, so transports can piggyback a <c>notifications/tools/list_changed</c>
    /// message onto the next client request (as an SSE-formatted response). Connected
    /// clients then refresh their tool list without reconnecting -- previously the only
    /// way for an already-connected client to see newly added or re-exposed tools was
    /// a full client restart.
    ///
    /// Each change bumps a version, and each client session remembers the version it was last
    /// told about, so every session gets the notification once. A single pending flag used to be
    /// consumed by whichever client asked first, and the other clients on the editor kept a stale
    /// list. Requests with no Mcp-Session-Id share one slot, as they did before sessions existed.
    ///
    /// Threading: transports consume from background threads, so the hot path only touches the
    /// concurrent map and an interlocked version. SessionState (main-thread-only API) is read at
    /// server start and written from an EditorApplication.update sync, which is what lets the
    /// version and the per-session marks survive a domain reload within an editor session.
    /// </summary>
    internal static class MCPToolListChangeNotifier
    {
        private const string HashKey = "KitWright.MCP.ExposedToolsHash";
        private const string VersionKey = "KitWright.MCP.ToolListVersion";
        private const string SeenKey = "KitWright.MCP.ToolListVersionSeen";

        // Below any version, so a restored mark always re-notifies.
        private const long Unseen = -1;

        internal const string NotificationJson =
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}";

        private static long _version;
        private static readonly ConcurrentDictionary<string, long> _seen =
            new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        private static volatile bool _persistedFlagDirty;
        private static bool _loaded;
        private static bool _updateHooked;

        /// <summary>
        /// Compare the currently exposed tool names against the last observed set.
        /// Called on the main thread when the server (re)starts with a freshly built
        /// exporter. The first observation in an editor session only records the baseline.
        /// </summary>
        public static void CheckForChanges(MCPToolExporter toolExporter)
        {
            try
            {
                LoadPersistedState();

                var hash = ComputeToolListHash(toolExporter);
                var previous = SessionState.GetString(HashKey, null);

                if (string.IsNullOrEmpty(previous))
                {
                    SessionState.SetString(HashKey, hash);
                }
                else if (!string.Equals(previous, hash, StringComparison.Ordinal))
                {
                    SessionState.SetString(HashKey, hash);
                    MarkChanged();
                    PluginDebugLogger.Log(
                        "[KitWright MCP Server] Exposed tool list changed; clients will be notified via tools/list_changed.");
                }

                if (!_updateHooked)
                {
                    _updateHooked = true;
                    EditorApplication.update += SyncPersistedFlag;
                }
            }
            catch (Exception ex)
            {
                PluginDebugLogger.Log("[KitWright MCP Server] Tool list change check failed: " + ex.Message);
            }
        }

        /// <summary>Main thread only: persists the new version.</summary>
        internal static void MarkChanged()
        {
            LoadPersistedState();
            SessionState.SetInt(VersionKey, (int)Interlocked.Increment(ref _version));
        }

        /// <summary>
        /// Marks a session as holding the current list. Called when it initializes, since a client
        /// lists the tools right after that and a notification on its next call would be redundant.
        /// </summary>
        public static void Observe(string sessionId)
        {
            _seen[Key(sessionId)] = Interlocked.Read(ref _version);
            _persistedFlagDirty = true;
        }

        /// <summary>
        /// True at most once per change for this session. A session never seen before counts as
        /// holding the list from the start of the editor session, so it is told about any change since.
        /// Thread-safe.
        /// </summary>
        public static bool TryConsumePending(string sessionId = null)
        {
            var key = Key(sessionId);
            var current = Interlocked.Read(ref _version);
            var seen = _seen.GetOrAdd(key, 0L);
            if (seen >= current || !_seen.TryUpdate(key, current, seen))
                return false;

            _persistedFlagDirty = true;
            return true;
        }

        /// <summary>Re-arm this session's notification when the piggybacked send failed before reaching the client. Thread-safe.</summary>
        public static void RestorePending(string sessionId = null)
        {
            _seen[Key(sessionId)] = Unseen;
            _persistedFlagDirty = true;
        }

        /// <summary>
        /// Wrap a JSON-RPC response as an SSE body that first delivers the
        /// tools/list_changed notification, then the response itself.
        /// </summary>
        public static string BuildSseBody(string responseJson)
        {
            return "data: " + NotificationJson + "\n\n" +
                   "data: " + (responseJson ?? string.Empty) + "\n\n";
        }

        private static string Key(string sessionId) => sessionId ?? string.Empty;

        private static void LoadPersistedState()
        {
            if (_loaded)
                return;

            _loaded = true;
            Interlocked.Exchange(ref _version, SessionState.GetInt(VersionKey, 0));
            foreach (var line in SessionState.GetString(SeenKey, string.Empty).Split('\n'))
            {
                var tab = line.IndexOf('\t');
                if (tab >= 0 && long.TryParse(line.Substring(tab + 1), out var seen))
                    _seen.TryAdd(line.Substring(0, tab), seen);
            }
        }

        /// <summary>Main-thread pump that mirrors the per-session marks into SessionState.</summary>
        private static void SyncPersistedFlag()
        {
            if (!_persistedFlagDirty)
                return;

            _persistedFlagDirty = false;
            var lines = new StringBuilder();
            foreach (var entry in _seen)
                lines.Append(entry.Key).Append('\t').Append(entry.Value).Append('\n');
            SessionState.SetString(SeenKey, lines.ToString());
        }

        private static string ComputeToolListHash(MCPToolExporter toolExporter)
        {
            var tools = toolExporter.ExportTools();
            var names = new List<string>(tools.Count);
            foreach (var tool in tools)
            {
                if (tool.TryGetValue("name", out var name) && name is string s)
                    names.Add(s);
            }
            names.Sort(StringComparer.Ordinal);

            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", names)));
                return Convert.ToBase64String(bytes);
            }
        }
    }
}
