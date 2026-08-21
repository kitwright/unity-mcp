// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KitWright.Editor.MCP.Server.SSE;
using KitWright.Editor.Settings;
using UnityEngine;

namespace KitWright.Editor.MCP.Server
{
    /// HTTP transport implementation for MCP using a loopback TCP listener.
    /// Listens for JSON-RPC requests over HTTP.
    internal class HttpMCPTransport : IMCPTransport
    {
        // Hot Reload replaces the transport without a domain reload, leaking the old bound
        // listener; the static survives the patch so the next bind can close the orphan.
        private static TcpListener s_activeListener;
        private static readonly object s_activeListenerLock = new object();

        // Reclaiming the active listener steals whatever socket is bound on the port, so a plain
        // restart paths (post-reload / settings change), which know the previous owner is gone or
        // going, arm this for the next start to sweep a listener a hot-patch leaked.
        private static bool s_reclaimOrphanOnNextStart;

        internal static void ArmOrphanReclaim()
        {
            lock (s_activeListenerLock)
                s_reclaimOrphanOnNextStart = true;
        }

        private static bool ConsumeOrphanReclaimArm()
        {
            lock (s_activeListenerLock)
            {
                var armed = s_reclaimOrphanOnNextStart;
                s_reclaimOrphanOnNextStart = false;
                return armed;
            }
        }

        // Peek, so the service can sweep the orphan before it probes ports; StartAsync still
        // consumes the arm, and closing an already-closed listener is a no-op.
        internal static bool IsOrphanReclaimArmed()
        {
            lock (s_activeListenerLock)
                return s_reclaimOrphanOnNextStart;
        }

        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly int _port;
        private readonly string _projectPin;
        private bool _isRunning;
        private const int StartRetryAttempts = 40;
        private const int StartRetryDelayMs = 250;
        private const int MaxHeaderBytes = 64 * 1024;
        private const int MaxBodyBytes = 64 * 1024 * 1024;
        private const int MaxConsecutiveAcceptErrors = 10;
        private const int AcceptErrorRetryDelayMs = 200;
        private const int RequestReadTimeoutMs = 30_000;

        public bool IsRunning => _isRunning;
        public bool IsAttachedToExistingServer => false;
        public event Action<MCPRequest, Action<MCPResponse>> OnRequestReceived;

        public HttpMCPTransport(int port, string expectedProjectIdentity = null)
        {
            _port = port;
            _projectPin = string.IsNullOrEmpty(expectedProjectIdentity) ||
                          expectedProjectIdentity.Length < ProjectIdentity.PinLength
                ? string.Empty
                : expectedProjectIdentity.Substring(0, ProjectIdentity.PinLength);
        }

        // A client configured for THIS project posts to /p/<pin>/. Ports are assigned by
        // first-come scan, so a config written when this project held a different port would
        // otherwise reach whichever sibling editor now owns it — and that editor would answer,
        // applying the edits to the wrong project. Refusing a mismatched pin turns that silent
        // wrong-project write into a visible 404.
        // A path with no pin is accepted: configs written before pinning exist in the wild, and
        // re-running Configure is what upgrades them.
        internal bool PathTargetsAnotherProject(string path)
        {
            if (_projectPin.Length == 0)
                return false;

            var pin = ExtractPin(path);
            return pin.Length > 0 && !string.Equals(pin, _projectPin, StringComparison.OrdinalIgnoreCase);
        }

        internal static string ExtractPin(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            var query = path.IndexOf('?');
            if (query >= 0)
                path = path.Substring(0, query);

            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                // Case-insensitive: the pin comparison below is, so an ordinal marker match here
                // would let "/P/<pin>/" read as pinless and walk straight past the check.
                if (string.Equals(segments[i], "p", StringComparison.OrdinalIgnoreCase))
                    return segments[i + 1];
            }

            return string.Empty;
        }

        public async Task<bool> StartAsync(CancellationToken ct = default)
        {
            if (_isRunning) return true;

            for (var attempt = 1; attempt <= StartRetryAttempts; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    if (attempt == 1 && ConsumeOrphanReclaimArm())
                        CloseActiveListener(_listener);

                    _listener = new TcpListener(IPAddress.Loopback, _port);
                    _listener.Server.NoDelay = true;
                    _listener.Start();
                    DisableHandleInheritance(_listener.Server);

                    lock (s_activeListenerLock)
                        s_activeListener = _listener;

                    _cts = new CancellationTokenSource();
                    _isRunning = true;

                    _ = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);

                    PluginDebugLogger.Log($"[KitWright MCP Server] HTTP transport started on http://127.0.0.1:{_port}/");
                    return true;
                }
                catch (OperationCanceledException)
                {
                    CleanupFailedStart();
                    _isRunning = false;
                    return false;
                }
                catch (Exception ex) when (IsAddressInUse(ex))
                {
                    CleanupFailedStart();
                    if (attempt >= StartRetryAttempts)
                    {
                        Debug.LogError($"[KitWright MCP Server] Failed to start HTTP transport: {ex.Message}");
                        _isRunning = false;
                        return false;
                    }

                    if (attempt == 1)
                    {
                        Debug.LogWarning(
                            $"[KitWright MCP Server] Port {_port} is temporarily in use; retrying for up to {(StartRetryAttempts * StartRetryDelayMs) / 1000f:0.#} seconds.");
                    }

                    if (!await DelayBeforeRetryAsync(ct).ConfigureAwait(false))
                        return false;
                }
                catch (Exception ex)
                {
                    CleanupFailedStart();
                    Debug.LogError($"[KitWright MCP Server] Failed to start HTTP transport: {ex.Message}");
                    _isRunning = false;
                    return false;
                }
            }

            _isRunning = false;
            return false;
        }

        public void Stop()
        {
            if (!_isRunning && _listener == null && _cts == null)
                return;

            try
            {
                _isRunning = false;
                _cts?.Cancel();
                CloseListener();
                PluginDebugLogger.Log("[KitWright MCP Server] HTTP transport stopped");
            }
            catch (ObjectDisposedException)
            {
                PluginDebugLogger.Log("[KitWright MCP Server] HTTP transport was already disposed");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KitWright MCP Server] Error stopping HTTP transport: {ex.Message}");
            }
            finally
            {
                _listener = null;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void CleanupFailedStart()
        {
            try
            {
                _isRunning = false;
                CloseListener();
            }
            catch
            {
            }
            finally
            {
                _listener = null;
            }
        }

        private static async Task<bool> DelayBeforeRetryAsync(CancellationToken ct, int delayMs = StartRetryDelayMs)
        {
            try
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private static bool IsAddressInUse(Exception ex)
        {
            var message = ex?.Message ?? string.Empty;
            if (message.IndexOf("Only one usage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("Address already in use", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // 10013 (WSAEACCES): Windows returns this instead of 10048 when the port is held
            // by a socket bound without SO_REUSEADDR (e.g. HttpListener/http.sys).
            return ex is SocketException socketException &&
                   (socketException.ErrorCode == 48 ||
                    socketException.ErrorCode == 98 ||
                    socketException.ErrorCode == 183 ||
                    socketException.ErrorCode == 10013 ||
                    socketException.ErrorCode == 10048);
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            var consecutiveErrors = 0;
            try
            {
                while (!ct.IsCancellationRequested && _isRunning)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        consecutiveErrors = 0;
                        // No token here: Task.Run drops the delegate when ct is already
                        // cancelled, and then nothing runs the `using` that closes client.
                        _ = Task.Run(() => HandleClientAsync(client, ct));
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted ||
                                                     ex.SocketErrorCode == SocketError.OperationAborted)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (ct.IsCancellationRequested || !_isRunning)
                            break;

                        consecutiveErrors++;
                        if (consecutiveErrors >= MaxConsecutiveAcceptErrors)
                        {
                            Debug.LogError($"[KitWright MCP Server] Listen loop aborting after {consecutiveErrors} consecutive accept errors: {ex.Message}");
                            break;
                        }

                        if (!await DelayBeforeRetryAsync(ct, AcceptErrorRetryDelayMs).ConfigureAwait(false))
                            break;
                    }
                }
            }
            finally
            {
                if (!ct.IsCancellationRequested && _isRunning)
                {
                    _isRunning = false;
                    CloseListener();
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            MCPRequest request = null;
            NetworkStream stream = null;
            try
            {
                using (client)
                {
                    stream = client.GetStream();
                    var httpRequest = await ReadRequestWithTimeoutAsync(client, stream, ct);
                    if (httpRequest == null)
                        return;

                    if (!IsValidOrigin(httpRequest.Origin))
                    {
                        await SendHtmlStatusAsync(stream, HttpStatusCode.Forbidden, "Forbidden", "Origin not allowed", ct);
                        return;
                    }

                    if (httpRequest.Method == "OPTIONS")
                    {
                        await SendOptionsResponseAsync(stream, ct);
                        return;
                    }

                    if (PathTargetsAnotherProject(httpRequest.Path))
                    {
                        await SendWrongProjectAsync(stream, httpRequest.Path, ct);
                        return;
                    }

                    if (httpRequest.Method == "GET")
                    {
                        if (httpRequest.AcceptsEventStream)
                        {
                            var attachResult = SSESessionManager.Instance.TryAttachStream(
                                httpRequest.SessionId, stream, out var session);
                            if (attachResult == AttachStreamResult.SessionNotFound)
                            {
                                await SendHtmlStatusAsync(stream, HttpStatusCode.NotFound, "Not Found",
                                    "Session not found or expired. Please re-initialize.", ct);
                                return;
                            }
                            if (attachResult == AttachStreamResult.StreamAlreadyAttached)
                            {
                                await SendHtmlStatusAsync(stream, HttpStatusCode.Conflict, "Conflict",
                                    "Another stream is already connected to this session.", ct);
                                return;
                            }

                            await HandleSseStreamAsync(session, stream, ct);
                            return;
                        }

                        await SendStatusPageAsync(stream, ct);
                        return;
                    }

                    if (httpRequest.Method != "POST")
                    {
                        await SendMethodNotAllowedAsync(stream, "GET, POST, OPTIONS", ct);
                        return;
                    }

                    if (!await Security.ClientApprovalGate.AuthorizeAsync(client, _port))
                    {
                        await SendHtmlStatusAsync(stream, HttpStatusCode.Forbidden, "Forbidden",
                            "This client was not approved in the Unity editor. Approve it in the KitWright dialog, or disable client approval in the MCP Settings tab.", ct);
                        return;
                    }

                    request = ParseJsonRequest(httpRequest.Body);
                    if (request == null)
                    {
                        await SendErrorResponseAsync(stream, null, -32700, "Parse error", ct);
                        return;
                    }

                    request.SessionId = httpRequest.SessionId;
                    var extraHeaders = string.Empty;

                    if (string.Equals(request.Method, "initialize", StringComparison.Ordinal))
                    {
                        var newSession = SSESessionManager.Instance.CreateSession();
                        extraHeaders = $"Mcp-Session-Id: {newSession.SessionId}\r\n";
                    }
                    else if (!string.IsNullOrEmpty(httpRequest.SessionId)
                             && !SSESessionManager.Instance.TryGetSession(httpRequest.SessionId, out _))
                    {
                        // Same answer the event-stream GET gives, so a client whose session died with
                        // a domain reload re-runs initialize instead of being served forever on a
                        // dead id while its notification stream 404s.
                        await SendHtmlStatusAsync(stream, HttpStatusCode.NotFound, "Not Found",
                            "Session not found or expired. Please re-initialize.", ct);
                        return;
                    }

                    var requestReceived = OnRequestReceived;
                    if (requestReceived == null)
                    {
                        await SendErrorResponseAsync(stream, request.Id, -32000, "MCP server is stopping or not ready.", ct);
                        return;
                    }

                    var responseTcs = new TaskCompletionSource<MCPResponse>();
                    requestReceived.Invoke(request, r => responseTcs.TrySetResult(r));

                    var budgetSeconds = Tools.ToolRegistry.TimeoutSecondsForRequest(request.Method, request.Params);
                    using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(budgetSeconds)))
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                    {
                        try
                        {
                            var responseTask = responseTcs.Task;
                            var completedTask = await Task.WhenAny(responseTask, Task.Delay(-1, linkedCts.Token));
                            if (completedTask == responseTask)
                            {
                                var response = await responseTask;
                                if (response == null)
                                {
                                    await SendAcceptedAsync(stream, ct);
                                }
                                else if (httpRequest.AcceptsEventStream &&
                                         !string.Equals(request.Method, "initialize", StringComparison.Ordinal) &&
                                         MCPToolListChangeNotifier.TryConsumePending())
                                {
                                    await SendSseResponseAsync(stream, response, ct, extraHeaders);
                                }
                                else
                                {
                                    await SendResponseAsync(stream, response, ct, extraHeaders);
                                }
                            }
                            else
                            {
                                throw new OperationCanceledException();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            var errResponse = timeoutCts.IsCancellationRequested
                                ? CreateErrorResponse(request.Id, -32000, "Request timeout")
                                : CreateErrorResponse(request.Id, -32000, "Request cancelled");
                            await SendResponseAsync(stream, errResponse, CancellationToken.None);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KitWright MCP Server] Error handling request: {ex.Message}");
                if (stream != null)
                    await SendErrorResponseAsync(stream, request?.Id, -32603, $"Internal error: {ex.Message}", CancellationToken.None);
            }
        }

        // A peer that connects and then sends nothing would otherwise pin this handler task
        // and its socket for the life of the domain. Mono ignores the token on an in-flight
        // NetworkStream read, so closing the socket is what actually unblocks it.
        private async Task<HttpRequestData> ReadRequestWithTimeoutAsync(TcpClient client, NetworkStream stream, CancellationToken ct)
        {
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readCts.CancelAfter(RequestReadTimeoutMs);
                using (readCts.Token.Register(() => { try { client.Close(); } catch { } }))
                {
                    try
                    {
                        return await ReadHttpRequestAsync(stream, readCts.Token);
                    }
                    catch when (readCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        return null;
                    }
                }
            }
        }

        private async Task<HttpRequestData> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var rawRequest = new MemoryStream();
            var headerEnd = -1;

            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read == 0)
                    return null;

                rawRequest.Write(buffer, 0, read);
                if (rawRequest.Length > MaxHeaderBytes)
                    throw new InvalidOperationException("HTTP header is too large.");

                headerEnd = FindHeaderEnd(rawRequest.GetBuffer(), (int)rawRequest.Length);
            }

            var requestBytes = rawRequest.ToArray();
            var headerText = Encoding.ASCII.GetString(requestBytes, 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
                return null;

            var requestLineParts = lines[0].Split(' ');
            if (requestLineParts.Length < 1)
                return null;

            var contentLength = 0;
            var acceptsEventStream = false;
            string sessionId = null;
            string origin = null;
            for (var i = 1; i < lines.Length; i++)
            {
                var separator = lines[i].IndexOf(':');
                if (separator <= 0)
                    continue;

                var name = lines[i].Substring(0, separator).Trim();
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(lines[i].Substring(separator + 1).Trim(), out contentLength);
                    if (contentLength < 0 || contentLength > MaxBodyBytes)
                        return null;
                }
                else if (string.Equals(name, "Accept", StringComparison.OrdinalIgnoreCase))
                {
                    acceptsEventStream = lines[i].Substring(separator + 1)
                        .IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                else if (string.Equals(name, "Mcp-Session-Id", StringComparison.OrdinalIgnoreCase))
                {
                    sessionId = lines[i].Substring(separator + 1).Trim();
                }
                else if (string.Equals(name, "Origin", StringComparison.OrdinalIgnoreCase))
                {
                    origin = lines[i].Substring(separator + 1).Trim();
                }
            }

            var bodyStart = headerEnd + 4;
            var bodyBytes = new byte[contentLength];
            var copied = Math.Min(contentLength, requestBytes.Length - bodyStart);
            if (copied > 0)
                Buffer.BlockCopy(requestBytes, bodyStart, bodyBytes, 0, copied);

            while (copied < contentLength)
            {
                var read = await stream.ReadAsync(bodyBytes, copied, contentLength - copied, ct);
                if (read == 0)
                    break;
                copied += read;
            }

            return new HttpRequestData
            {
                Method = requestLineParts[0],
                Path = requestLineParts.Length > 1 ? requestLineParts[1] : "/",
                AcceptsEventStream = acceptsEventStream,
                SessionId = sessionId,
                Origin = origin,
                Body = Encoding.UTF8.GetString(bodyBytes, 0, copied)
            };
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (var i = 3; i < length; i++)
            {
                if (buffer[i - 3] == '\r' &&
                    buffer[i - 2] == '\n' &&
                    buffer[i - 1] == '\r' &&
                    buffer[i] == '\n')
                {
                    return i - 3;
                }
            }

            return -1;
        }

        private MCPRequest ParseJsonRequest(string json)
        {
            try
            {
                var dict = JsonCodec.Deserialize(json) as Dictionary<string, object>;
                if (dict == null) return null;

                return new MCPRequest
                {
                    JsonRpc = dict.ContainsKey("jsonrpc") ? dict["jsonrpc"]?.ToString() : "2.0",
                    Id = dict.ContainsKey("id") ? dict["id"] : null,
                    Method = dict.ContainsKey("method") ? dict["method"]?.ToString() : null,
                    Params = dict.ContainsKey("params") ? dict["params"] as Dictionary<string, object> : new Dictionary<string, object>()
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KitWright MCP Server] JSON parse error: {ex.Message}");
                return null;
            }
        }

        private async Task SendResponseAsync(NetworkStream stream, MCPResponse mcpResponse, CancellationToken ct, string extraHeaders = "")
        {
            try
            {
                var json = SerializeResponse(mcpResponse);
                await SendRawResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, ct, extraHeaders);
            }
            catch (Exception ex) when (IsExpectedClientDisconnect(ex, ct))
            {
                PluginDebugLogger.Log($"[KitWright MCP Server] Response not sent because the client disconnected: {ex.Message}");
            }
        }

        /// Streamable-HTTP style response: an SSE body that carries the pending
        /// tools/list_changed notification followed by the JSON-RPC response.
        /// Only used when the client declared Accept: text/event-stream.
        private async Task SendSseResponseAsync(NetworkStream stream, MCPResponse mcpResponse, CancellationToken ct, string extraHeaders = "")
        {
            try
            {
                var body = MCPToolListChangeNotifier.BuildSseBody(SerializeResponse(mcpResponse));
                await SendRawResponseAsync(stream, 200, "OK", "text/event-stream", body, ct, extraHeaders);
                PluginDebugLogger.Log("[KitWright MCP Server] Delivered tools/list_changed notification via SSE response.");
            }
            catch (Exception ex) when (IsExpectedClientDisconnect(ex, ct))
            {
                MCPToolListChangeNotifier.RestorePending();
                PluginDebugLogger.Log($"[KitWright MCP Server] SSE response not sent because the client disconnected: {ex.Message}");
            }
            catch (Exception ex)
            {
                MCPToolListChangeNotifier.RestorePending();
                Debug.LogError($"[KitWright MCP Server] Failed to send response: {ex.Message}");
            }
        }

        internal static bool IsValidOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin))
                return true; // Absent origin is allowed for native CLI/IDE clients

            if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                var host = uri.Host;
                return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private Task SendHtmlStatusAsync(NetworkStream stream, HttpStatusCode code, string reason, string message, CancellationToken ct)
        {
            var body = $"<html><body><h1>{(int)code} {reason}</h1><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
            return SendRawResponseAsync(stream, (int)code, reason, "text/html; charset=utf-8", body, ct);
        }

        private async Task HandleSseStreamAsync(SSESessionManager.SSESession session, NetworkStream stream, CancellationToken ct)
        {
            try
            {
                var headers = "HTTP/1.1 200 OK\r\n" +
                              "Content-Type: text/event-stream; charset=utf-8\r\n" +
                              "Cache-Control: no-cache, no-transform\r\n" +
                              "Connection: keep-alive\r\n" +
                              "Access-Control-Allow-Origin: *\r\n" +
                              "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                              "Access-Control-Allow-Headers: Content-Type, Accept, Mcp-Session-Id\r\n" +
                              $"Mcp-Session-Id: {session.SessionId}\r\n" +
                              "\r\n";

                var headerBytes = Encoding.ASCII.GetBytes(headers);
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                var pingLoop = SSESessionManager.Instance.RunSsePingLoopAsync(session, ct);
                await Task.WhenAny(pingLoop, WaitForClientEofAsync(stream, ct)).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpectedClientDisconnect(ex, ct))
            {
                PluginDebugLogger.Log($"[KitWright MCP Server] SSE client disconnected: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KitWright MCP Server] Error in SSE stream: {ex.Message}");
            }
            finally
            {
                SSESessionManager.Instance.DetachStream(session);
            }
        }

        // FIN stops the peer sending, not receiving, so pings keep succeeding after it. A read does not.
        private static async Task WaitForClientEofAsync(NetworkStream stream, CancellationToken ct)
        {
            var scratch = new byte[1];
            try
            {
                while (await stream.ReadAsync(scratch, 0, 1, ct).ConfigureAwait(false) > 0)
                {
                }
            }
            catch
            {
            }
        }

        private bool IsExpectedClientDisconnect(Exception ex, CancellationToken ct)
        {
            return ct.IsCancellationRequested || !_isRunning || IsClientDisconnectException(ex);
        }

        internal static bool IsClientDisconnectException(Exception ex)
        {
            while (ex != null)
            {
                if (ex is OperationCanceledException ||
                    ex is ObjectDisposedException)
                {
                    return true;
                }

                if (ex is SocketException socketException &&
                    IsClientDisconnectSocketError(socketException.SocketErrorCode))
                {
                    return true;
                }

                var message = ex.Message ?? string.Empty;
                if (message.IndexOf("socket has been shut down", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("broken pipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("connection reset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("connection was aborted", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("cannot access a disposed object", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                ex = ex.InnerException;
            }

            return false;
        }

        private static bool IsClientDisconnectSocketError(SocketError error)
        {
            return error == SocketError.ConnectionReset ||
                   error == SocketError.ConnectionAborted ||
                   error == SocketError.NetworkReset ||
                   error == SocketError.NotConnected ||
                   error == SocketError.Shutdown ||
                   error == SocketError.OperationAborted ||
                   error == SocketError.Interrupted;
        }

        private Task SendOptionsResponseAsync(NetworkStream stream, CancellationToken ct)
        {
            return SendRawResponseAsync(stream, (int)HttpStatusCode.NoContent, "No Content", "text/plain", string.Empty, ct);
        }

        private Task SendMethodNotAllowedAsync(NetworkStream stream, string allowHeader, CancellationToken ct)
        {
            return SendRawResponseAsync(stream, (int)HttpStatusCode.MethodNotAllowed, "Method Not Allowed", "text/plain", string.Empty, ct, "Allow: " + allowHeader + "\r\n");
        }

        private Task SendWrongProjectAsync(NetworkStream stream, string path, CancellationToken ct)
        {
            Debug.LogWarning(
                $"[KitWright MCP Server] Refused a request for project pin '{ExtractPin(path)}' — this server " +
                $"serves pin '{_projectPin}' on port {_port}. The client's MCP config points at the wrong port; " +
                "re-run Configure in the KitWright MCP window for that project.");

            return SendRawResponseAsync(stream, (int)HttpStatusCode.NotFound, "Not Found", "text/plain",
                $"This KitWright MCP server serves project pin {_projectPin}, not {ExtractPin(path)}.", ct);
        }

        private Task SendAcceptedAsync(NetworkStream stream, CancellationToken ct)
        {
            return SendRawResponseAsync(stream, (int)HttpStatusCode.Accepted, "Accepted", "text/plain", string.Empty, ct);
        }

        // MCP clients only POST, so return a plain status page instead of an SSE stream that spins forever.
        private Task SendStatusPageAsync(NetworkStream stream, CancellationToken ct)
        {
            var live = OnRequestReceived != null;
            var status = live ? "RUNNING" : "STARTING";
            var color = live ? "#4ec94e" : "#c9a24e";
            var faviconSvg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'>" +
                $"<circle cx='16' cy='16' r='12' fill='{color}'/></svg>";
            var favicon = "data:image/svg+xml," + Uri.EscapeDataString(faviconSvg);
            var body =
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>KitWright MCP</title>" +
                $"<link rel=\"icon\" href=\"{favicon}\"></head>" +
                "<body style=\"font-family:system-ui;background:#1b1b1e;color:#ddd;text-align:center;padding-top:80px\">" +
                $"<h1 style=\"color:{color}\">KitWright MCP: {status}</h1>" +
                $"<p>Listening on http://127.0.0.1:{_port}/{(_projectPin.Length == 0 ? string.Empty : "p/" + _projectPin + "/")}</p>" +
                "</body></html>";
            return SendRawResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", body, ct);
        }

        private async Task SendErrorResponseAsync(NetworkStream stream, object requestId, int code, string message, CancellationToken ct)
        {
            await SendResponseAsync(stream, CreateErrorResponse(requestId, code, message), ct);
        }

        private static async Task SendRawResponseAsync(
            NetworkStream stream,
            int statusCode,
            string reasonPhrase,
            string contentType,
            string body,
            CancellationToken ct,
            string extraHeaders = "")
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            var header =
                $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: Content-Type, Accept, Mcp-Session-Id\r\n" +
                extraHeaders +
                "\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct);
            if (bodyBytes.Length > 0)
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, ct);
        }

        private MCPResponse CreateErrorResponse(object requestId, int code, string message)
        {
            return new MCPResponse
            {
                JsonRpc = "2.0",
                Id = requestId,
                Error = new MCPError { Code = code, Message = message }
            };
        }

        private string SerializeResponse(MCPResponse response)
        {
            var dict = new Dictionary<string, object>
            {
                ["jsonrpc"] = response.JsonRpc,
                ["id"] = response.Id
            };

            if (response.Error != null)
            {
                var errorDict = new Dictionary<string, object>
                {
                    ["code"] = response.Error.Code,
                    ["message"] = response.Error.Message
                };
                if (response.Error.Data != null) errorDict["data"] = response.Error.Data;
                dict["error"] = errorDict;
            }
            else
            {
                dict["result"] = response.Result;
            }

            return JsonCodec.Serialize(dict);
        }

        public void Dispose()
        {
            Stop();
        }

        private void CloseListener()
        {
            if (_listener == null)
                return;

            lock (s_activeListenerLock)
            {
                if (ReferenceEquals(s_activeListener, _listener))
                    s_activeListener = null;
            }

            try { _listener.Stop(); } catch { }
        }

        [DllImport("kernel32.dll")]
        private static extern void SetHandleInformation(IntPtr handle, int mask, int flags);

        // Mono binds listening sockets with the inherit flag set, so every process Unity spawns
        // afterwards (other MCP servers, compiler workers, node) receives a duplicate of this
        // children lives, and the next editor has to fall forward to a different port.
        // Windows-only on purpose: on macOS/Linux Mono already opens sockets with FD_CLOEXEC,
        // so there is nothing to clear and no portable equivalent worth P/Invoking for.
        internal static void DisableHandleInheritance(Socket socket)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                return;

            try { SetHandleInformation(socket.Handle, 0x1 /* HANDLE_FLAG_INHERIT */, 0); } catch { }
        }

        // Two callers, same job: a start reclaiming a listener a hot-patch leaked, and a domain
        // reload closing whatever is still bound. The reload path must not go through the service,
        // which reports not-running while a start is mid-flight even though the listener is bound.
        internal static void CloseActiveListener(TcpListener except = null)
        {
            TcpListener orphan;
            lock (s_activeListenerLock)
            {
                orphan = s_activeListener;
                s_activeListener = null;
            }

            if (orphan == null || ReferenceEquals(orphan, except))
                return;

            try
            {
                orphan.Stop();
                Debug.LogWarning("[KitWright MCP Server] Closed a listener left bound by a previous transport.");
            }
            catch { }
        }

        private sealed class HttpRequestData
        {
            public string Method { get; set; }
            public string Path { get; set; }
            public bool AcceptsEventStream { get; set; }
            public string SessionId { get; set; }
            public string Origin { get; set; }
            public string Body { get; set; }
        }
    }
}

