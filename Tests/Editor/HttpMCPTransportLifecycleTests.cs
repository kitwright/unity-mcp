// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.MCP.Server.SSE;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace KitWright.Editor
{
    public sealed class HttpMCPTransportLifecycleTests
    {
        private const string ServerName = "KitWright MCP Server - Test Project";
        private const string ProjectIdentityA = "project-a";

        // Client approval would show a dialog if identifying the test's own connection ever
        // fails; these tests exercise the transport, not the gate.
        [OneTimeSetUp]
        public void DisableClientApproval() =>
            MCP.Server.Security.ClientApprovalGate.RequireApprovalOverride = () => false;

        [OneTimeTearDown]
        public void RestoreClientApproval() =>
            MCP.Server.Security.ClientApprovalGate.RequireApprovalOverride = null;

        // A full 64-hex identity; only the first ProjectIdentity.PinLength chars form the pin.
        private const string IdentityAaaa = "aaaa1111" + "00000000000000000000000000000000000000000000000000000000";

        [TearDown]
        public void ClearSseSessions()
        {
            LogAssert.ignoreFailingMessages = false;
            SSESessionManager.Instance.PingIntervalMs = 15_000;
            SSESessionManager.Instance.ResetForTests();
        }

        [Test]
        public void ExtractPin_ReadsThePSegmentAndIgnoresTheQuery()
        {
            Assert.AreEqual("aaaa1111", HttpMCPTransport.ExtractPin("/p/aaaa1111/"));
            Assert.AreEqual("aaaa1111", HttpMCPTransport.ExtractPin("/p/aaaa1111"));
            Assert.AreEqual("aaaa1111", HttpMCPTransport.ExtractPin("/p/aaaa1111/?x=1"));
            Assert.AreEqual(string.Empty, HttpMCPTransport.ExtractPin("/"));
            Assert.AreEqual(string.Empty, HttpMCPTransport.ExtractPin("/p"));
            Assert.AreEqual(string.Empty, HttpMCPTransport.ExtractPin(null));

            // An uppercase marker must not read as pinless: that would be a one-keystroke way
            // around the wrong-project check.
            Assert.AreEqual("aaaa1111", HttpMCPTransport.ExtractPin("/P/aaaa1111/"));
        }

        [Test]
        public void PinnedPathForAnotherProjectIsRefused()
        {
            var transport = new HttpMCPTransport(0, IdentityAaaa);

            Assert.IsTrue(transport.PathTargetsAnotherProject("/p/bbbb2222/"),
                "A request pinned to another project must be refused, not answered.");
            Assert.IsFalse(transport.PathTargetsAnotherProject("/p/aaaa1111/"));
            Assert.IsFalse(transport.PathTargetsAnotherProject("/p/AAAA1111/"), "Pin match is case-insensitive.");
            Assert.IsFalse(transport.PathTargetsAnotherProject("/"),
                "An unpinned path stays accepted so configs written before pinning keep working.");
        }

        [Test]
        public void ServerWithoutAnIdentityAcceptsEveryPath()
        {
            var transport = new HttpMCPTransport(0, null);

            Assert.IsFalse(transport.PathTargetsAnotherProject("/p/bbbb2222/"));
            Assert.IsFalse(transport.PathTargetsAnotherProject("/"));
        }

        [Test]
        public void ClientDisconnectDetection_CoversExpectedResponseWriteFailures()
        {
            Assert.IsTrue(HttpMCPTransport.IsClientDisconnectException(
                new IOException("Unable to read data from the transport connection: The socket has been shut down.")));
            Assert.IsTrue(HttpMCPTransport.IsClientDisconnectException(
                new ObjectDisposedException("NetworkStream")));
            Assert.IsFalse(HttpMCPTransport.IsClientDisconnectException(
                new InvalidOperationException("Unexpected transport failure.")));
        }

        [Test]
        public void RecentActivityBadge_InterruptedIsNotDisplayedAsOk()
        {
            Assert.AreEqual("OK", RecentActivityPanel.GetBadgeText(MCPToolCallStatus.Success));
            Assert.AreEqual("INT", RecentActivityPanel.GetBadgeText(MCPToolCallStatus.Interrupted));
            Assert.AreEqual("ERR", RecentActivityPanel.GetBadgeText(MCPToolCallStatus.Error));
        }

        [Test]
        public void InterruptedToolRecoveryStatus_EmptyContinuationIsInterrupted()
        {
            Assert.AreEqual(
                MCPToolCallStatus.Interrupted,
                MCPServerService.DetermineInterruptedToolRecoveryStatus(null));
            Assert.AreEqual(
                MCPToolCallStatus.Success,
                MCPServerService.DetermineInterruptedToolRecoveryStatus("Continuation completed."));
            Assert.AreEqual(
                MCPToolCallStatus.Error,
                MCPServerService.DetermineInterruptedToolRecoveryStatus(ToolResultFormatter.Error("TEST_ERROR")));
        }

        [UnityTest]
        public IEnumerator StartAsync_WhenPortIsAlreadyOwned_ReturnsFalseWithoutStoppingOwner()
        {
            var port = GetFreeTcpPort();
            var firstTransport = new HttpMCPTransport(port, ProjectIdentityA);
            var secondTransport = new HttpMCPTransport(port, ProjectIdentityA);

            firstTransport.OnRequestReceived += (request, sendResponse) =>
                HandleInitializeRequest(request, sendResponse, ProjectIdentityA);

            try
            {
                var firstStart = firstTransport.StartAsync();
                yield return WaitForTask(firstStart);
                Assert.IsTrue(firstStart.Result, "The first transport should bind a free port.");

                var stopwatch = Stopwatch.StartNew();
                using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(900)))
                {
                    var secondStart = secondTransport.StartAsync(cts.Token);
                    yield return WaitForTask(secondStart);
                    Assert.IsFalse(secondStart.Result, "A second transport must not report running when it does not own the listener.");
                }
                stopwatch.Stop();

                Assert.IsFalse(secondTransport.IsAttachedToExistingServer);
                Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(2));

                secondTransport.Stop();

                var probeTask = SendInitializeRequestAsync(port);
                yield return WaitForTask(probeTask);
                Assert.That(
                    probeTask.Result,
                    Does.Contain(ProjectIdentityA),
                    "Stopping a failed second transport must not stop the owning listener.");
            }
            finally
            {
                secondTransport.Dispose();
                firstTransport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Stop_ReleasesOwnedPortForRestart()
        {
            var port = GetFreeTcpPort();
            var firstTransport = new HttpMCPTransport(port, ProjectIdentityA);
            var secondTransport = new HttpMCPTransport(port, ProjectIdentityA);

            firstTransport.OnRequestReceived += (request, sendResponse) =>
                HandleInitializeRequest(request, sendResponse, ProjectIdentityA);

            try
            {
                var firstStart = firstTransport.StartAsync();
                yield return WaitForTask(firstStart);
                Assert.IsTrue(firstStart.Result);

                firstTransport.Stop();

                var secondStart = secondTransport.StartAsync();
                yield return WaitForTask(secondStart);
                Assert.IsTrue(secondStart.Result, "Stopping the owner should release the port for a fresh transport.");
            }
            finally
            {
                secondTransport.Dispose();
                firstTransport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator StartAsync_UnresponsivePortOwnerFailsWithoutReportingRunning()
        {
            var port = GetFreeTcpPort();
            using (var listener = CreateHttpListener(port))
            using (var listenerCts = new CancellationTokenSource())
            {
                listener.Start();
                var serverTask = HoldRequestsOpenAsync(listener, listenerCts.Token);
                var transport = new HttpMCPTransport(port, ProjectIdentityA);

                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200)))
                    {
                        var startTask = transport.StartAsync(cts.Token);
                        yield return WaitForTask(startTask);
                        Assert.IsFalse(startTask.Result);
                    }

                    Assert.IsFalse(transport.IsRunning);
                }
                finally
                {
                    transport.Dispose();
                    listenerCts.Cancel();
                    listener.Close();
                    serverTask.Wait(100);
                }
            }
        }

        [UnityTest]
        public IEnumerator RequestWithoutSubscriber_ReturnsServerNotReadyErrorWithoutWaitingForTimeout()
        {
            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port, ProjectIdentityA);

            try
            {
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                var stopwatch = Stopwatch.StartNew();
                var probeTask = SendInitializeRequestAsync(port);
                yield return WaitForTask(probeTask, 2f);
                stopwatch.Stop();

                Assert.That(probeTask.Result, Does.Contain("MCP server is stopping or not ready."));
                Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(2));
            }
            finally
            {
                transport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator SseAcceptingRequest_DeliversToolsListChangedOnce()
        {
            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port, ProjectIdentityA);
            transport.OnRequestReceived += (request, sendResponse) =>
            {
                sendResponse(new MCPResponse
                {
                    Id = request.Id,
                    Result = new Dictionary<string, object> { ["ok"] = true }
                });
            };

            try
            {
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                MCPToolListChangeNotifier.RestorePending();

                var firstRequest = SendToolListRequestAsync(port, acceptSse: true);
                yield return WaitForTask(firstRequest, 2f);
                Assert.AreEqual("text/event-stream", firstRequest.Result.ContentType);
                Assert.That(firstRequest.Result.Body, Does.Contain(MCPToolListChangeNotifier.NotificationJson));
                Assert.That(firstRequest.Result.Body, Does.Contain("\"id\":\"test\""));
                Assert.Less(
                    firstRequest.Result.Body.IndexOf(MCPToolListChangeNotifier.NotificationJson, StringComparison.Ordinal),
                    firstRequest.Result.Body.IndexOf("\"id\":\"test\"", StringComparison.Ordinal));

                var secondRequest = SendToolListRequestAsync(port, acceptSse: true);
                yield return WaitForTask(secondRequest, 2f);
                Assert.AreEqual("application/json", secondRequest.Result.ContentType);
                Assert.That(secondRequest.Result.Body, Does.Not.Contain(MCPToolListChangeNotifier.NotificationJson));
                Assert.That(secondRequest.Result.Body, Does.Contain("\"id\":\"test\""));
            }
            finally
            {
                while (MCPToolListChangeNotifier.TryConsumePending())
                {
                }

                transport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator StartStopSamePort_RebindsAndServesAcrossRepeatedCycles()
        {
            var port = GetFreeTcpPort();

            for (var cycle = 1; cycle <= 5; cycle++)
            {
                var transport = new HttpMCPTransport(port, ProjectIdentityA);
                transport.OnRequestReceived += (request, sendResponse) =>
                    HandleInitializeRequest(request, sendResponse, ProjectIdentityA);

                try
                {
                    var startTask = transport.StartAsync();
                    yield return WaitForTask(startTask);
                    Assert.IsTrue(startTask.Result, $"Cycle {cycle}: transport must rebind the port.");

                    var probeTask = SendInitializeRequestAsync(port);
                    yield return WaitForTask(probeTask, 3f);
                    Assert.That(
                        probeTask.Result,
                        Does.Contain(ProjectIdentityA),
                        $"Cycle {cycle}: rebound port must serve requests, not hang.");
                }
                finally
                {
                    transport.Stop();
                    transport.Dispose();
                }
            }
        }

        private static IEnumerator WaitForTask(Task task, float timeoutSeconds = 5f)
        {
            var start = Time.realtimeSinceStartup;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup - start > timeoutSeconds)
                    throw new TimeoutException("Timed out waiting for async test task.");

                yield return null;
            }

            if (task.IsFaulted)
                throw task.Exception;
        }

        private static void HandleInitializeRequest(
            MCPRequest request,
            Action<MCPResponse> sendResponse,
            string projectIdentity)
        {
            if (request.Method != "initialize")
            {
                sendResponse(new MCPResponse
                {
                    Id = request.Id,
                    Error = new MCPError { Code = -32601, Message = "Method not found" }
                });
                return;
            }

            sendResponse(new MCPResponse
            {
                Id = request.Id,
                Result = new Dictionary<string, object>
                {
                    ["serverInfo"] = new Dictionary<string, object>
                    {
                        ["name"] = ServerName,
                        ["version"] = "test"
                    },
                    ["kitwright"] = new Dictionary<string, object>
                    {
                        ["projectIdentity"] = projectIdentity,
                        ["projectIdentityVersion"] = ProjectIdentity.IdentityVersion
                    }
                }
            });
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static HttpListener CreateHttpListener(int port)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add($"http://localhost:{port}/");
            return listener;
        }

        private static async Task<string> SendInitializeRequestAsync(int port)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) })
            using (var content = new StringContent(
                       "{\"jsonrpc\":\"2.0\",\"id\":\"test\",\"method\":\"initialize\",\"params\":{}}",
                       Encoding.UTF8,
                       "application/json"))
            {
                var response = await client.PostAsync($"http://127.0.0.1:{port}/", content);
                return await response.Content.ReadAsStringAsync();
            }
        }

        private static async Task<HttpResult> SendToolListRequestAsync(int port, bool acceptSse)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
            using (var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/"))
            {
                request.Content = new StringContent(
                    "{\"jsonrpc\":\"2.0\",\"id\":\"test\",\"method\":\"tools/list\",\"params\":{}}",
                    Encoding.UTF8,
                    "application/json");

                if (acceptSse)
                    request.Headers.Accept.ParseAdd("text/event-stream");

                var response = await client.SendAsync(request);
                return new HttpResult
                {
                    ContentType = response.Content.Headers.ContentType?.MediaType,
                    Body = await response.Content.ReadAsStringAsync()
                };
            }
        }

        private static async Task HoldRequestsOpenAsync(HttpListener listener, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && listener.IsListening)
                {
                    var context = await listener.GetContextAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), ct);
                            context.Response.StatusCode = 204;
                            context.Response.Close();
                        }
                        catch
                        {
                            try { context.Response.Close(); } catch { }
                        }
                    }, ct);
                }
            }
            catch
            {
                // Listener shutdown during test cleanup.
            }
        }

        private static bool IsBindable(int port)
        {
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetHandleInformation(IntPtr handle, out int flags);

        // Regression: the post-reload port used to be written back into settings, so every reload
        // that fell forward raised the configured base permanently (8765 -> 8767 -> ...).
        [Test]
        public void SelectStartupBasePort_HintIsConsumedOnceAndLeavesConfiguredPortIntact()
        {
            MCPServerService.PreferredStartupPort = 8770;

            Assert.AreEqual(8770, MCPServerService.SelectStartupBasePort(8765));
            Assert.AreEqual(8765, MCPServerService.SelectStartupBasePort(8765));
        }

        // A P/Invoke that silently no-ops looks exactly like a working one: the port only leaks
        // once Unity exits with a child process still holding the inherited handle.
        [Test]
        public void DisableHandleInheritance_ClearsTheInheritFlagOnTheListeningSocket()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("Handle inheritance is a Windows-only concern.");

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                Assert.IsTrue(GetHandleInformation(listener.Server.Handle, out var before));
                Assume.That(before & 0x1, Is.EqualTo(0x1),
                    "Runtime already binds sockets non-inheritable; the production call is then a no-op.");

                HttpMCPTransport.DisableHandleInheritance(listener.Server);

                Assert.IsTrue(GetHandleInformation(listener.Server.Handle, out var after));
                Assert.AreEqual(0, after & 0x1,
                    "Listening socket is still inheritable, so children will keep the port bound.");
            }
            finally
            {
                listener.Stop();
            }
        }

        // The leak this guards against only shows up when the reload lands mid-start, so the
        // service-level stop is not enough: the transport must be closed off its own static.
        [UnityTest]
        public IEnumerator CloseActiveListener_FreesAPortTheServiceNeverMarkedRunning()
        {
            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port);

            try
            {
                var start = transport.StartAsync();
                yield return WaitForTask(start);
                Assert.IsTrue(start.Result, "Transport failed to bind a free port.");
                Assert.IsFalse(IsBindable(port), "Port should be held while the transport is up.");

                HttpMCPTransport.CloseActiveListener();

                Assert.IsTrue(IsBindable(port), "Listener survived the reload hook and orphaned the port.");
            }
            finally
            {
                transport.Dispose();
            }
        }

        [Test]
        public void OriginValidation_AcceptsAbsentAndLocalhost_RejectsExternalDomain()
        {
            Assert.IsTrue(HttpMCPTransport.IsValidOrigin(null), "Absent origin must be allowed.");
            Assert.IsTrue(HttpMCPTransport.IsValidOrigin(""), "Empty origin must be allowed.");
            Assert.IsTrue(HttpMCPTransport.IsValidOrigin("http://localhost:8765"));
            Assert.IsTrue(HttpMCPTransport.IsValidOrigin("http://127.0.0.1:8765"));
            Assert.IsTrue(HttpMCPTransport.IsValidOrigin("http://[::1]:8765"));
            Assert.IsFalse(HttpMCPTransport.IsValidOrigin("http://evil-site.com"));
            Assert.IsFalse(HttpMCPTransport.IsValidOrigin("http://attacker.local"));
        }

        [UnityTest]
        public IEnumerator PostInitialize_ReturnsMcpSessionIdHeader()
        {
            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port, ProjectIdentityA);
            transport.OnRequestReceived += (request, sendResponse) =>
                HandleInitializeRequest(request, sendResponse, ProjectIdentityA);

            try
            {
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                using (var content = new StringContent(
                    "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"method\":\"initialize\",\"params\":{}}",
                    Encoding.UTF8,
                    "application/json"))
                {
                    var responseTask = client.PostAsync("http://127.0.0.1:" + port + "/", content);
                    yield return WaitForTask(responseTask, 3f);
                    var response = responseTask.Result;

                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    Assert.IsTrue(response.Headers.Contains("Mcp-Session-Id"), "Initialize response must carry Mcp-Session-Id header.");
                    var sessionId = string.Join("", response.Headers.GetValues("Mcp-Session-Id"));
                    Assert.IsTrue(SSESessionManager.Instance.TryGetSession(sessionId, out _), "Session must be registered in SSESessionManager.");
                }
            }
            finally
            {
                transport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator GetStream_WithoutValidSession_Returns404()
        {
            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port, ProjectIdentityA);

            try
            {
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + port + "/");
                    req.Headers.Add("Accept", "text/event-stream");
                    req.Headers.Add("Mcp-Session-Id", "non-existent-session-id");

                    var responseTask = client.SendAsync(req);
                    yield return WaitForTask(responseTask, 3f);
                    var response = responseTask.Result;

                    Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, "Invalid Mcp-Session-Id must return 404 Not Found.");
                }
            }
            finally
            {
                transport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator GetStream_WithValidSession_ReceivesSseHeadersAndPing()
        {
            SSESessionManager.Instance.PingIntervalMs = 100; // fast ping for testing
            var session = SSESessionManager.Instance.CreateSession();

            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port, ProjectIdentityA);

            try
            {
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                using (var client = new HttpClient())
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + port + "/");
                    req.Headers.Add("Accept", "text/event-stream");
                    req.Headers.Add("Mcp-Session-Id", session.SessionId);

                    var responseTask = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                    yield return WaitForTask(responseTask, 3f);
                    var response = responseTask.Result;

                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);

                    using (var stream = response.Content.ReadAsStreamAsync().Result)
                    {
                        var buffer = new byte[256];
                        var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                        yield return WaitForTask(readTask, 2f);
                        var text = Encoding.UTF8.GetString(buffer, 0, readTask.Result);
                        Assert.That(text, Does.Contain(": ping\n\n"), "Stream should receive ping heartbeat.");
                    }
                }
            }
            finally
            {
                transport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator PostRequest_WithoutSessionId_StillSucceeds()
        {
            // A session exists, so the server has handed an id out; a client that never echoes it
            // must still be served, or every config written before sessions existed breaks.
            SSESessionManager.Instance.CreateSession();

            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port, ProjectIdentityA);
            transport.OnRequestReceived += (request, sendResponse) =>
                sendResponse(new MCPResponse
                {
                    Id = request.Id,
                    Result = new Dictionary<string, object> { ["served"] = true }
                });

            try
            {
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                using (var content = new StringContent(
                    "{\"jsonrpc\":\"2.0\",\"id\":\"no-session\",\"method\":\"tools/list\",\"params\":{}}",
                    Encoding.UTF8,
                    "application/json"))
                {
                    var responseTask = client.PostAsync("http://127.0.0.1:" + port + "/", content);
                    yield return WaitForTask(responseTask, 3f);
                    var response = responseTask.Result;

                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                        "A POST without Mcp-Session-Id must still be served.");
                    Assert.That(response.Content.ReadAsStringAsync().Result, Does.Contain("served"));
                }
            }
            finally
            {
                transport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator PostRequest_WithUnknownSessionId_Returns404()
        {
            // Sessions die with the domain, so a client that kept its id must be told to
            // re-initialize instead of being served on an id whose SSE stream 404s forever.
            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port, ProjectIdentityA);
            transport.OnRequestReceived += (request, sendResponse) =>
                sendResponse(new MCPResponse
                {
                    Id = request.Id,
                    Result = new Dictionary<string, object> { ["served"] = true }
                });

            try
            {
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    var stale = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + port + "/")
                    {
                        Content = new StringContent(
                            "{\"jsonrpc\":\"2.0\",\"id\":\"stale\",\"method\":\"tools/list\",\"params\":{}}",
                            Encoding.UTF8,
                            "application/json")
                    };
                    stale.Headers.Add("Mcp-Session-Id", "deadbeefdeadbeefdeadbeefdeadbeef");

                    var staleTask = client.SendAsync(stale);
                    yield return WaitForTask(staleTask, 3f);
                    var staleResponse = staleTask.Result;

                    Assert.AreEqual(HttpStatusCode.NotFound, staleResponse.StatusCode,
                        "A POST bearing an unknown Mcp-Session-Id must return 404, not be served.");
                    Assert.That(staleResponse.Content.ReadAsStringAsync().Result,
                        Does.Contain("re-initialize"));

                    using (var content = new StringContent(
                        "{\"jsonrpc\":\"2.0\",\"id\":\"no-session\",\"method\":\"tools/list\",\"params\":{}}",
                        Encoding.UTF8,
                        "application/json"))
                    {
                        var sessionlessTask = client.PostAsync("http://127.0.0.1:" + port + "/", content);
                        yield return WaitForTask(sessionlessTask, 3f);
                        var sessionless = sessionlessTask.Result;

                        Assert.AreEqual(HttpStatusCode.OK, sessionless.StatusCode,
                            "A POST without Mcp-Session-Id must still be served.");
                        Assert.That(sessionless.Content.ReadAsStringAsync().Result, Does.Contain("served"));
                    }
                }
            }
            finally
            {
                transport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator LoggingSetLevel_FiltersAndPushesLogNotification()
        {
            SSESessionManager.Instance.PingIntervalMs = 30_000; // keep pings out of the read
            var session = SSESessionManager.Instance.CreateSession();
            SSESessionManager.Instance.SetLoggingLevel(session.SessionId, "warning");

            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port, ProjectIdentityA);

            try
            {
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                using (var client = new HttpClient())
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + port + "/");
                    req.Headers.Add("Accept", "text/event-stream");
                    req.Headers.Add("Mcp-Session-Id", session.SessionId);

                    var responseTask = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                    yield return WaitForTask(responseTask, 3f);
                    Assert.AreEqual(HttpStatusCode.OK, responseTask.Result.StatusCode);

                    using (var stream = responseTask.Result.Content.ReadAsStreamAsync().Result)
                    {
                        // Below the configured level: must never reach the client.
                        var belowTask = SSESessionManager.Instance.BroadcastLogNotificationAsync(
                            LogType.Log, "BelowThresholdMessage", null);
                        yield return WaitForTask(belowTask, 2f);

                        var aboveTask = SSESessionManager.Instance.BroadcastLogNotificationAsync(
                            LogType.Error, "AboveThresholdMessage", null);
                        yield return WaitForTask(aboveTask, 2f);

                        var buffer = new byte[2048];
                        var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                        yield return WaitForTask(readTask, 2f);
                        var text = Encoding.UTF8.GetString(buffer, 0, readTask.Result);

                        Assert.That(text, Does.Contain("notifications/message"));
                        Assert.That(text, Does.Contain("AboveThresholdMessage"));
                        Assert.That(text, Does.Contain("\"level\":\"error\""));
                        Assert.That(text, Does.Not.Contain("BelowThresholdMessage"),
                            "A log under the level the client set must be dropped.");
                    }
                }
            }
            finally
            {
                transport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator GetStream_DuplicateSession_Returns409Conflict()
        {
            SSESessionManager.Instance.PingIntervalMs = 1000;
            var session = SSESessionManager.Instance.CreateSession();

            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port, ProjectIdentityA);

            try
            {
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                using (var client1 = new HttpClient())
                using (var client2 = new HttpClient())
                {
                    var req1 = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + port + "/");
                    req1.Headers.Add("Accept", "text/event-stream");
                    req1.Headers.Add("Mcp-Session-Id", session.SessionId);

                    var responseTask1 = client1.SendAsync(req1, HttpCompletionOption.ResponseHeadersRead);
                    yield return WaitForTask(responseTask1, 3f);
                    Assert.AreEqual(HttpStatusCode.OK, responseTask1.Result.StatusCode);

                    var req2 = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + port + "/");
                    req2.Headers.Add("Accept", "text/event-stream");
                    req2.Headers.Add("Mcp-Session-Id", session.SessionId);

                    var responseTask2 = client2.SendAsync(req2);
                    yield return WaitForTask(responseTask2, 3f);
                    Assert.AreEqual(HttpStatusCode.Conflict, responseTask2.Result.StatusCode, "Second stream for same session must return 409 Conflict.");
                }
            }
            finally
            {
                transport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator GetStream_ReceivesPingAndEvictsOnDisconnect()
        {
            SSESessionManager.Instance.PingIntervalMs = 50;
            var session = SSESessionManager.Instance.CreateSession();

            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port, ProjectIdentityA);

            try
            {
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                var client = new HttpClient();
                var req = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + port + "/");
                req.Headers.Add("Accept", "text/event-stream");
                req.Headers.Add("Mcp-Session-Id", session.SessionId);

                var responseTask = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                yield return WaitForTask(responseTask, 3f);
                Assert.AreEqual(HttpStatusCode.OK, responseTask.Result.StatusCode);
                Assert.IsNotNull(session.ActiveStream, "Session should have active stream.");

                responseTask.Result.Dispose();
                client.Dispose();

                var waited = 0f;
                while (session.ActiveStream != null && waited < 2f)
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    waited += 0.1f;
                }

                Assert.IsNull(session.ActiveStream, "Disconnected client stream must be evicted.");
            }
            finally
            {
                transport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator RepeatedLog_IsCollapsedButReportsHowManyWereSuppressed()
        {
            // This test drives the log pipe on purpose and asserts on SSE frames, not on the console.
            // Any error another process in the project writes while it runs (Hot Reload's server exit,
            // an import worker dropping) would otherwise fail it for an unrelated reason.
            LogAssert.ignoreFailingMessages = true;

            SSESessionManager.Instance.PingIntervalMs = 30_000;
            // Wide enough that the three sends below are inside the window whatever the frame loop
            // costs. At the shipped 100ms this test was asserting on machine speed.
            SSESessionManager.Instance.LogDedupWindowMs = 60_000;

            // UnityLogsRepository hooks Application.logMessageReceivedThreaded and fans every
            // editor log out to the same SSE stream, so any unrelated log lands in the frames this
            // test counts. Widening the dedup window above turned that from a 100ms exposure into a
            // minute-long one, so the pipe is muted for the duration and restored in the finally.
            var foreignLogs = DI.RootScopeServices.Services?.GetService(
                typeof(Services.UnityLogs.UnityLogsRepository)) as Services.UnityLogs.UnityLogsRepository;
            foreignLogs?.StopListening();

            var session = SSESessionManager.Instance.CreateSession();
            SSESessionManager.Instance.SetLoggingLevel(session.SessionId, "info");

            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port, ProjectIdentityA);

            try
            {
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                using (var client = new HttpClient())
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + port + "/");
                    req.Headers.Add("Accept", "text/event-stream");
                    req.Headers.Add("Mcp-Session-Id", session.SessionId);

                    var responseTask = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                    yield return WaitForTask(responseTask, 3f);
                    Assert.AreEqual(HttpStatusCode.OK, responseTask.Result.StatusCode);

                    using (var stream = responseTask.Result.Content.ReadAsStreamAsync().Result)
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            var task = SSESessionManager.Instance.BroadcastLogNotificationAsync(
                                LogType.Error, "SpammedMessage", null);
                            yield return WaitForTask(task, 2f);
                        }

                        var buffer = new byte[4096];
                        var firstRead = stream.ReadAsync(buffer, 0, buffer.Length);
                        yield return WaitForTask(firstRead, 2f);
                        var first = Encoding.UTF8.GetString(buffer, 0, firstRead.Result);

                        Assert.That(first, Does.Contain("SpammedMessage"));
                        Assert.That(first, Does.Not.Contain("repeated"),
                            "Nothing was suppressed before the first send.");
                        Assert.AreEqual(1, CountOccurrences(first, "notifications/message"),
                            "Two repeats inside the dedup window must not each get a frame.");

                        // Closing the window rather than waiting one out: the assertion is that the
                        // suppressed count survives to the next send, not how long a sleep takes.
                        SSESessionManager.Instance.LogDedupWindowMs = 0;

                        var repeatTask = SSESessionManager.Instance.BroadcastLogNotificationAsync(
                            LogType.Error, "SpammedMessage", null);
                        yield return WaitForTask(repeatTask, 2f);

                        var secondRead = stream.ReadAsync(buffer, 0, buffer.Length);
                        yield return WaitForTask(secondRead, 2f);
                        var second = Encoding.UTF8.GetString(buffer, 0, secondRead.Result);

                        Assert.That(second, Does.Contain("[previous message repeated 2x]"),
                            "The dropped repeats must be counted, not lost.");
                    }
                }
            }
            finally
            {
                transport.Dispose();
                foreignLogs?.StartListening();
            }
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }
            return count;
        }

        private sealed class HttpResult
        {
            public string ContentType;
            public string Body;
        }
    }
}
