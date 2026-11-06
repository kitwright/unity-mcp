// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace KitWright.Editor.MCP.Server
{
    // Maps a running tools/call to its client's notifications/cancelled. Static like the bridge's
    // MutatingToolGate: both transports and every session go through one MCPServerService.
    internal static class RequestCancellation
    {
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> Running =
            new ConcurrentDictionary<string, CancellationTokenSource>();

        // A tool runs on the editor thread through reflection and cannot take a token parameter, so the
        // bridge sets this around the synchronous start of the call; a tool that honours cancellation
        // copies it before its first await (execute_code into LoopBudgetGuard, batch into its op loop).
        internal static CancellationToken EntryToken { get; private set; }

        // The call token also fires on a client cancel, which answers the client at once while the tool may
        // still be running; the bridge's mutation gate has to outlast that, so it listens to the ceiling alone.
        private static readonly ConcurrentDictionary<CancellationToken, CancellationToken> Ceilings =
            new ConcurrentDictionary<CancellationToken, CancellationToken>();

        internal static IDisposable Register(string sessionId, object requestId, CancellationTokenSource cts,
            CancellationToken callToken = default, CancellationToken ceilingToken = default)
        {
            if (callToken.CanBeCanceled)
                Ceilings[callToken] = ceilingToken;
            // Without a session, two clients reusing an id would cancel each other's calls; every MCP client
            // initializes and gets one, so only a bare JSON-RPC caller goes without cancellation.
            if (requestId == null || string.IsNullOrEmpty(sessionId))
                return new Unregister(null, null, callToken);

            var key = Key(sessionId, requestId);
            Running[key] = cts;
            return new Unregister(key, cts, callToken);
        }

        internal static CancellationToken CeilingFor(CancellationToken callToken) =>
            Ceilings.TryGetValue(callToken, out var ceiling) ? ceiling : callToken;

        internal static bool Cancel(string sessionId, object requestId)
        {
            if (requestId == null || !Running.TryGetValue(Key(sessionId, requestId), out var cts))
                return false;

            // A notification gets no answer, so a throwing callback must not turn it into an error response.
            try { cts.Cancel(); }
            catch (Exception) { return false; }
            return true;
        }

        internal static IDisposable Enter(CancellationToken token)
        {
            var previous = EntryToken;
            EntryToken = token;
            return new Restore(previous);
        }

        // JSON numbers arrive as long or double depending on the parser path, so 5 and 5.0 must meet.
        private static string Key(string sessionId, object requestId)
        {
            var id = requestId is double d && d == Math.Floor(d)
                ? ((long)d).ToString(CultureInfo.InvariantCulture)
                : Convert.ToString(requestId, CultureInfo.InvariantCulture);
            return (sessionId ?? string.Empty) + "|" + id;
        }

        private sealed class Unregister : IDisposable
        {
            private readonly string _key;
            private readonly CancellationTokenSource _cts;
            private readonly CancellationToken _callToken;

            internal Unregister(string key, CancellationTokenSource cts, CancellationToken callToken)
            {
                _key = key;
                _cts = cts;
                _callToken = callToken;
            }

            public void Dispose()
            {
                if (_callToken.CanBeCanceled)
                    Ceilings.TryRemove(_callToken, out _);
                // Removes only this registration: a reused request id may already map to a newer call.
                if (_key != null)
                    ((ICollection<KeyValuePair<string, CancellationTokenSource>>)Running)
                        .Remove(new KeyValuePair<string, CancellationTokenSource>(_key, _cts));
            }
        }

        private sealed class Restore : IDisposable
        {
            private readonly CancellationToken _previous;

            internal Restore(CancellationToken previous) => _previous = previous;

            public void Dispose() => EntryToken = _previous;
        }
    }
}
