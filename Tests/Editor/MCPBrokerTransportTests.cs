// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.State;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace KitWright.Editor
{
    public sealed class MCPBrokerTransportTests
    {
        [OneTimeSetUp]
        public void DisableClientApproval() =>
            MCP.Server.Security.ClientApprovalGate.RequireApprovalOverride = () => false;

        [OneTimeTearDown]
        public void RestoreClientApproval() =>
            MCP.Server.Security.ClientApprovalGate.RequireApprovalOverride = null;

        [UnityTest]
        public IEnumerator BrokerProcess_StartsWithHealthTokenAndStops()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));

                Assert.IsFalse(MCPBrokerProcessManager.TryProbeBroker(port, "wrong-token", out _));
                Assert.IsTrue(MCPBrokerProcessManager.TryProbeBroker(port, connection.Token, out var health));
                Assert.AreEqual(connection.Pid, health.Pid);

                MCPBrokerProcessManager.Stop(paths);
                yield return null;

                Assert.IsFalse(MCPBrokerProcessManager.TryProbeBroker(port, connection.Token, out _));
            }
            finally
            {
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerProcess_DoesNotAdoptArbitraryOpenPort()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            var listener = new TcpListener(IPAddress.Loopback, port);

            try
            {
                listener.Start();

                Assert.IsFalse(MCPBrokerProcessManager.TryProbeBroker(port, "token", out _));
                Assert.IsFalse(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths));
                StringAssert.Contains("Port is already in use", MCPBrokerProcessManager.LastError);
            }
            finally
            {
                listener.Stop();
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator BrokerProcess_PortChangeStopsRecordedBroker()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var oldPort = GetFreeTcpPort();
            var newPort = GetFreeTcpPort();

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(oldPort, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, oldPort, out var oldConnection));

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(newPort, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, newPort, out var newConnection));

                Assert.IsFalse(MCPBrokerProcessManager.TryProbeBroker(oldPort, oldConnection.Token, out _));
                Assert.IsTrue(MCPBrokerProcessManager.TryProbeBroker(newPort, newConnection.Token, out var health));
                Assert.AreEqual(newConnection.Pid, health.Pid);
            }
            finally
            {
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator BrokerProcess_DoesNotKillUnverifiedPidFromStaleState()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var recordedPort = GetFreeTcpPort();
            var requestedPort = GetFreeTcpPort();
            var recordedPortOwner = new TcpListener(IPAddress.Loopback, recordedPort);
            var requestedPortOwner = new TcpListener(IPAddress.Loopback, requestedPort);
            Process unrelatedProcess = null;

            try
            {
                recordedPortOwner.Start();
                requestedPortOwner.Start();
                unrelatedProcess = StartLongRunningProcess();
                WriteBrokerState(paths, unrelatedProcess.Id, recordedPort, "stale-token");

                Assert.IsFalse(MCPBrokerProcessManager.EnsureRunning(requestedPort, string.Empty, paths));
                StringAssert.Contains("Port is already in use", MCPBrokerProcessManager.LastError);
                Assert.IsFalse(unrelatedProcess.HasExited, "A stale pid file must not let broker cleanup kill an unverified process.");
            }
            finally
            {
                recordedPortOwner.Stop();
                requestedPortOwner.Stop();
                StopProcess(unrelatedProcess);
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }

            yield return null;
        }

        [Test]
        public void BrokerProcessStateReader_AllowsStaleProtocolForUpgradeCleanup()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);

            try
            {
                WriteBrokerState(paths, pid: 12345, port: 8765, token: "stale-token", protocol: MCPBrokerProtocol.Version - 1);

                var method = typeof(MCPBrokerProcessManager).GetMethod(
                    "TryReadState",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(method);

                var args = new object[] { paths.PidFilePath, null };
                Assert.IsTrue((bool)method.Invoke(null, args),
                    "Stale protocol records must stay readable so upgrades can shut down old brokers.");
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Test]
        public void ShouldStopOnQuit_LeavesTheBrokerRunningForBatchModeEditors()
        {
            Assert.IsFalse(MCPBrokerProcessManager.ShouldStopOnQuit(true),
                "A -batchmode editor shares the broker with an interactive one, so quitting must not stop it.");
            Assert.IsTrue(MCPBrokerProcessManager.ShouldStopOnQuit(false));
        }

        [UnityTest]
        public IEnumerator BrokerTransport_LongRunningRequestIsNotRedeliveredWithinSameSession()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            MCPBrokerClientTransport transport = null;

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));

                var calls = 0;
                transport = new MCPBrokerClientTransport(port, connection.Token);
                transport.OnRequestReceived += (request, sendResponse) =>
                {
                    calls++;
                    Task.Run(async () =>
                    {
                        await Task.Delay(2200);
                        sendResponse(CreateToolTextResponse(request.Id, "done"));
                    });
                };

                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                var requestTask = SendToolCallBodyAsync(port, "get_editor_state");
                yield return WaitForTask(requestTask, 8f);

                Assert.That(requestTask.Result, Does.Contain("done"));
                Assert.AreEqual(1, calls, "The broker must not redeliver a slow request while the same Unity session is still active.");
            }
            finally
            {
                transport?.Dispose();
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerTransport_RedeliversActiveRequestToNewSession()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            MCPBrokerClientTransport firstTransport = null;
            MCPBrokerClientTransport secondTransport = null;

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));

                var firstReceived = new TaskCompletionSource<bool>();
                var totalCalls = 0;
                firstTransport = new MCPBrokerClientTransport(port, connection.Token);
                firstTransport.OnRequestReceived += (request, sendResponse) =>
                {
                    totalCalls++;
                    firstReceived.TrySetResult(true);
                    // Simulate domain reload: the first AppDomain disappears before it can respond.
                };

                var firstStart = firstTransport.StartAsync();
                yield return WaitForTask(firstStart);
                Assert.IsTrue(firstStart.Result);

                var requestTask = SendToolCallBodyAsync(port, "execute_code");
                yield return WaitForTask(firstReceived.Task, 5f);

                firstTransport.Dispose();

                secondTransport = new MCPBrokerClientTransport(port, connection.Token);
                secondTransport.OnRequestReceived += (request, sendResponse) =>
                {
                    totalCalls++;
                    Assert.IsTrue(request.IsBrokerRedelivery);
                    sendResponse(CreateToolTextResponse(request.Id, "redelivered"));
                };

                var secondStart = secondTransport.StartAsync();
                yield return WaitForTask(secondStart);
                Assert.IsTrue(secondStart.Result);

                yield return WaitForTask(requestTask, 8f);

                Assert.That(requestTask.Result, Does.Contain("redelivered"));
                Assert.AreEqual(2, totalCalls);
            }
            finally
            {
                secondTransport?.Dispose();
                firstTransport?.Dispose();
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerTransport_TellsToolCallsToRetryButErrorsProtocolCallsWhenBackendIsUnavailable()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);

                var startedAt = DateTime.UtcNow;
                var requestTask = SendToolCallBodyAsync(port, "get_editor_state");
                yield return WaitForTask(requestTask, 3f);
                var elapsed = DateTime.UtcNow - startedAt;

                Assert.Less(elapsed.TotalSeconds, 2.0, "Unavailable backend responses should not wait for the client timeout.");
                AssertToolCallToldToRetry(requestTask.Result);

                var listTask = SendToolsListAsync(port);
                yield return WaitForTask(listTask, 3f);
                AssertProtocolCallErrored(listTask.Result);
            }
            finally
            {
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerTransport_DetachMakesNewRequestsFailFast()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            MCPBrokerClientTransport transport = null;

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));

                transport = new MCPBrokerClientTransport(port, connection.Token);
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                yield return new WaitForSecondsRealtime(0.25f);
                transport.Dispose();
                transport = null;

                var startedAt = DateTime.UtcNow;
                var requestTask = SendToolCallBodyAsync(port, "get_editor_state");
                yield return WaitForTask(requestTask, 3f);
                var elapsed = DateTime.UtcNow - startedAt;

                Assert.Less(elapsed.TotalSeconds, 2.0, "Detached backend responses should not wait for the client timeout.");
                AssertToolCallToldToRetry(requestTask.Result);
            }
            finally
            {
                transport?.Dispose();
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerTransport_HoldsProtocolCallsAcrossAReattachInsteadOfErroring()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            MCPBrokerClientTransport firstTransport = null;
            MCPBrokerClientTransport secondTransport = null;

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));

                firstTransport = new MCPBrokerClientTransport(port, connection.Token);
                var firstStart = firstTransport.StartAsync();
                yield return WaitForTask(firstStart);
                Assert.IsTrue(firstStart.Result);

                yield return new WaitForSecondsRealtime(0.25f);

                // Exactly what OnBeforeAssemblyReload does: detach, then vanish for the reload.
                firstTransport.Dispose();
                firstTransport = null;

                var listTask = SendToolsListAsync(port);
                yield return new WaitForSecondsRealtime(1f);

                secondTransport = new MCPBrokerClientTransport(port, connection.Token);
                secondTransport.OnRequestReceived += (request, sendResponse) =>
                    sendResponse(CreateToolTextResponse(request.Id, "tools-after-reload"));

                var secondStart = secondTransport.StartAsync();
                yield return WaitForTask(secondStart);
                Assert.IsTrue(secondStart.Result);

                yield return WaitForTask(listTask, 10f);

                Assert.That(listTask.Result, Does.Contain("\"result\""));
                Assert.That(listTask.Result, Does.Contain("tools-after-reload"));
                Assert.That(listTask.Result, Does.Not.Contain("-32001"));
            }
            finally
            {
                secondTransport?.Dispose();
                firstTransport?.Dispose();
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerTransport_DetachRejectsQueuedRequestsBehindInterruptedSession()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            MCPBrokerClientTransport firstTransport = null;
            MCPBrokerClientTransport secondTransport = null;

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));

                var firstReceived = new TaskCompletionSource<bool>();
                firstTransport = new MCPBrokerClientTransport(port, connection.Token);
                firstTransport.OnRequestReceived += (request, sendResponse) =>
                {
                    firstReceived.TrySetResult(true);
                    // Simulate domain reload before the active request can return.
                };

                var firstStart = firstTransport.StartAsync();
                yield return WaitForTask(firstStart);
                Assert.IsTrue(firstStart.Result);

                var interruptedRequest = SendToolCallBodyAsync(port, "execute_code");
                yield return WaitForTask(firstReceived.Task, 5f);

                var queuedRequest = SendToolCallBodyAsync(port, "get_editor_state");
                yield return new WaitForSecondsRealtime(0.1f);

                firstTransport.Dispose();
                firstTransport = null;

                yield return WaitForTask(queuedRequest, 3f);
                AssertToolCallToldToRetry(queuedRequest.Result);

                secondTransport = new MCPBrokerClientTransport(port, connection.Token);
                secondTransport.OnRequestReceived += (request, sendResponse) =>
                {
                    Assert.IsTrue(request.IsBrokerRedelivery);
                    sendResponse(CreateToolTextResponse(request.Id, "redelivered"));
                };

                var secondStart = secondTransport.StartAsync();
                yield return WaitForTask(secondStart);
                Assert.IsTrue(secondStart.Result);

                yield return WaitForTask(interruptedRequest, 8f);
                Assert.That(interruptedRequest.Result, Does.Contain("redelivered"));
            }
            finally
            {
                secondTransport?.Dispose();
                firstTransport?.Dispose();
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerTransport_RejectsRequestsFromNonLoopbackOrigin()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);

                var hostile = SendToolCallAsync(port, "execute_code", "http://evil.example.com");
                yield return WaitForTask(hostile, 5f);
                Assert.AreEqual(HttpStatusCode.Forbidden, hostile.Result.StatusCode,
                    "A web page must not be able to drive broker tool calls.");

                var loopback = SendToolCallAsync(port, "execute_code", "http://localhost:" + port);
                yield return WaitForTask(loopback, 5f);
                Assert.AreEqual(HttpStatusCode.OK, loopback.Result.StatusCode);
            }
            finally
            {
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        // The refusal has to be an HTTP 404, not a JSON-RPC error inside a 200: 404 is what makes a
        // client re-initialize, and a client served 200 keeps the dead id and fails every later call
        // while the connection still looks healthy. Broker mode could not answer 404 until the editor
        // was given a way to name the status, so pin the status here rather than the decision --
        // BrokerSession_InitializeMintsAnIdAndUnknownIdsAreRefused already covers the decision.
        [UnityTest]
        public IEnumerator BrokerTransport_RefusesADeadSessionWithNotFound()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            MCPBrokerClientTransport transport = null;

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));

                transport = new MCPBrokerClientTransport(port, connection.Token);
                transport.OnRequestReceived += (request, sendResponse) =>
                    sendResponse(CreateToolTextResponse(request.Id, "served"));

                var start = transport.StartAsync();
                yield return WaitForTask(start);
                Assert.IsTrue(start.Result);

                var dead = SendToolCallAsync(port, "get_editor_state", mcpSessionId: "kw-not-a-session");
                yield return WaitForTask(dead, 8f);
                Assert.AreEqual(HttpStatusCode.NotFound, dead.Result.StatusCode,
                    "a dead session id must come back as 404, or the client never re-initializes");

                var sessionless = SendToolCallAsync(port, "get_editor_state");
                yield return WaitForTask(sessionless, 8f);
                Assert.AreEqual(HttpStatusCode.OK, sessionless.Result.StatusCode,
                    "a client that sends no id at all is still served, so the refusal cannot be blanket");
            }
            finally
            {
                transport?.Dispose();
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [Test]
        public void BrokerSession_InitializeMintsAnIdAndUnknownIdsAreRefused()
        {
            Assert.IsTrue(MCPBrokerClientTransport.TryTakeSession(
                new MCPRequest { Method = "initialize" }, out var issued));
            Assert.IsFalse(string.IsNullOrEmpty(issued),
                "initialize must mint a session id, or the broker has none to return to the client");

            var live = new MCPRequest { Method = "tools/list", SessionId = issued };
            Assert.IsTrue(MCPBrokerClientTransport.TryTakeSession(live, out var reissued));
            Assert.IsNull(reissued, "only initialize issues an id");
            Assert.AreEqual(issued, live.SessionId,
                "the client's session has to reach the handler, or every broker client shares one slot");

            Assert.IsFalse(MCPBrokerClientTransport.TryTakeSession(
                new MCPRequest { Method = "tools/list", SessionId = "kw-not-a-session" }, out _),
                "a session the server does not know must be refused, not served");

            Assert.IsTrue(MCPBrokerClientTransport.TryTakeSession(
                new MCPRequest { Method = "tools/list" }, out _),
                "a client that sends no id at all is still served, as it was before sessions crossed the broker");
        }

        [Test]
        public void BrokerRedeliveryResponse_UsesRecoveryInfoAndDoesNotRerunTool()
        {
            DomainReloadHandler.StoreRecoveryInfo("execute_code", MCPToolCallStatus.Success.ToString(), "Compilation finished after reload.");

            var response = MCPServerService.TryCreateBrokerRedeliveryResponse(new MCPRequest
            {
                Id = "1",
                Method = "tools/call",
                IsBrokerRedelivery = true,
                Params = new Dictionary<string, object> { ["name"] = "execute_code" }
            });

            Assert.NotNull(response);
            Assert.IsNull(response.Error);
            var result = response.Result as Dictionary<string, object>;
            Assert.NotNull(result);
            Assert.AreEqual(false, result["isError"]);
            Assert.That(JsonCodec.Serialize(result), Does.Contain("Compilation finished after reload."));
        }

        [Test]
        public void BrokerRedeliveryResponse_ReturnsGenericErrorWhenRecoveryIsUnavailable()
        {
            DomainReloadHandler.GetLastRecoveryInfo(consume: true);

            var response = MCPServerService.TryCreateBrokerRedeliveryResponse(new MCPRequest
            {
                Id = "1",
                Method = "tools/call",
                IsBrokerRedelivery = true,
                Params = new Dictionary<string, object> { ["name"] = "execute_code" }
            });

            Assert.NotNull(response);
            var result = response.Result as Dictionary<string, object>;
            Assert.NotNull(result);
            Assert.AreEqual(true, result["isError"]);
            Assert.That(JsonCodec.Serialize(result), Does.Contain("was not re-run automatically"));
        }

        [Test]
        public void BrokerSource_IsVisibleToAssetDatabaseForUnityPackageExport()
        {
            var assetPath = ResolveBrokerSourceAssetPath();
            var source = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);

            Assert.NotNull(source, "Broker source TextAsset not found at " + assetPath);
            Assert.That(source.text, Does.Contain("kitwright-unity-mcp-broker"));
        }

        [Test]
        public void BrokerSpawn_PassesTheEditorsProtocolVersionToABrokerThatReadsIt()
        {
            var args = MCPBrokerProcessManager.BuildSpawnArguments(@"C:\b\broker.exe", 8765, "tok", "abc123");
            StringAssert.Contains("--protocol " + MCPBrokerProtocol.Version, args,
                "without this the broker answers 0, fails every health probe, and the transport " +
                "silently falls back to in-process HTTP");

            var source = AssetDatabase.LoadAssetAtPath<TextAsset>(ResolveBrokerSourceAssetPath());
            Assert.NotNull(source, "Broker source TextAsset not found");
            StringAssert.Contains("\"--protocol\"", source.text,
                "the broker must still read the argument the editor sends");
            Assert.That(source.text, Does.Not.Match(@"const\s+int\s+ProtocolVersion"),
                "a second declaration here is what made a one-sided bump possible");
        }

        [Test]
        public void BrokerLog_DoesNotWriteToTheStderrItInheritsFromUnity()
        {
            var source = AssetDatabase.LoadAssetAtPath<TextAsset>(ResolveBrokerSourceAssetPath());
            Assert.NotNull(source, "Broker source TextAsset not found");
            Assert.That(source.text, Does.Not.Contain("Console.Error"),
                "the broker is spawned without redirection, so stderr is Unity's own Editor.log");
            StringAssert.Contains("AppDomain.CurrentDomain.BaseDirectory", source.text,
                "broker diagnostics belong in a file beside the broker exe");
        }

        private static MCPResponse CreateToolTextResponse(object id, string text)
        {
            return new MCPResponse
            {
                Id = id,
                Result = new Dictionary<string, object>
                {
                    ["content"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "text",
                            ["text"] = text
                        }
                    }
                }
            };
        }

        private const string ReloadingNotice = "Unity is recompiling scripts, so this tool did not run";

        // Broker protocol v3: a tools/call landing while no backend is attached is a wait, not a
        // failure, so it comes back as ordinary tool output. Protocol calls still error, because a
        // fake result for those would corrupt the handshake.
        private static void AssertToolCallToldToRetry(string body)
        {
            Assert.That(body, Does.Contain("\"id\":\"test\""));
            Assert.That(body, Does.Contain("\"isError\":false"));
            Assert.That(body, Does.Contain(ReloadingNotice));
            Assert.That(body, Does.Not.Contain("\"error\""));
        }

        private static void AssertProtocolCallErrored(string body)
        {
            Assert.That(body, Does.Contain("\"code\":-32001"));
            Assert.That(body, Does.Contain("Unity MCP backend is reloading or reconnecting"));
            Assert.That(body, Does.Contain("\"retryable\":true"));
        }

        private static async Task<string> SendToolsListAsync(int port)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) })
            using (var content = new StringContent(
                       "{\"jsonrpc\":\"2.0\",\"id\":\"test\",\"method\":\"tools/list\",\"params\":{}}",
                       Encoding.UTF8,
                       "application/json"))
            {
                var response = await client.PostAsync("http://127.0.0.1:" + port + "/", content);
                return await response.Content.ReadAsStringAsync();
            }
        }

        private static async Task<HttpResponseMessage> SendToolCallAsync(int port, string toolName,
            string origin = null, string mcpSessionId = null)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) })
            using (var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + port + "/"))
            {
                if (origin != null)
                    request.Headers.Add("Origin", origin);
                if (mcpSessionId != null)
                    request.Headers.Add("Mcp-Session-Id", mcpSessionId);
                request.Content = new StringContent(
                    "{\"jsonrpc\":\"2.0\",\"id\":\"test\",\"method\":\"tools/call\",\"params\":{\"name\":\"" + toolName + "\",\"arguments\":{}}}",
                    Encoding.UTF8,
                    "application/json");

                var response = await client.SendAsync(request);
                // Buffered by SendAsync, so the body survives disposing the client below.
                await response.Content.LoadIntoBufferAsync();
                return response;
            }
        }

        private static async Task<string> SendToolCallBodyAsync(int port, string toolName) =>
            await (await SendToolCallAsync(port, toolName)).Content.ReadAsStringAsync();

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

        private static MCPBrokerProcessManager.MCPBrokerRuntimePaths CreateBrokerPaths(string root)
        {
            var cache = Path.Combine(root, "cache");
            return new MCPBrokerProcessManager.MCPBrokerRuntimePaths(
                Path.Combine(root, "broker.pid"),
                cache,
                ResolveBrokerSourcePath());
        }

        // These tests read the broker source straight off disk (and via AssetDatabase) so a
        // running broker/exported package can be verified against the exact same script. The
        // repo checks this package out at Assets/unity-mcp, but consumers install it as a real
        // UPM package (embedded, git, or registry) rooted at Packages/<name> or
        // Library/PackageCache/<name>@version -- resolve through PackageInfo first and only fall
        // back to the repo's own dev layout.
        private const string BrokerSourceRelativePath = "Editor/MCP/Server/Broker/keepalive-broker.cs.txt";

        private static string ResolveBrokerSourcePath()
        {
            var packageInfo = PackageInfo.FindForAssembly(typeof(MCPBrokerProcessManager).Assembly);
            var path = packageInfo != null
                ? Path.Combine(packageInfo.resolvedPath, BrokerSourceRelativePath.Replace('/', Path.DirectorySeparatorChar))
                : Path.Combine(Application.dataPath, "unity-mcp", BrokerSourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), "Broker source was not found at " + path);
            return path;
        }

        private static string ResolveBrokerSourceAssetPath()
        {
            var packageInfo = PackageInfo.FindForAssembly(typeof(MCPBrokerProcessManager).Assembly);
            return packageInfo != null
                ? packageInfo.assetPath + "/" + BrokerSourceRelativePath
                : "Assets/unity-mcp/" + BrokerSourceRelativePath;
        }

        private static void WriteBrokerState(
            MCPBrokerProcessManager.MCPBrokerRuntimePaths paths,
            int pid,
            int port,
            string token,
            int protocol = MCPBrokerProtocol.Version)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.PidFilePath));
            File.WriteAllText(paths.PidFilePath,
                pid + "\n" +
                port + "\n" +
                token + "\n" +
                protocol + "\n");
        }

        private static Process StartLongRunningProcess()
        {
            var startInfo = Application.platform == RuntimePlatform.WindowsEditor
                ? new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/C ping -n 60 127.0.0.1 > nul",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
                : new ProcessStartInfo
                {
                    FileName = "/bin/sleep",
                    Arguments = "60",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

            var process = Process.Start(startInfo);
            Assert.NotNull(process, "Failed to start a long-running test process.");
            return process;
        }

        private static void StopProcess(Process process)
        {
            if (process == null)
                return;

            try
            {
                if (!process.HasExited)
                    process.Kill();
                process.WaitForExit(2000);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        private static string CreateTempRoot()
        {
            var path = Path.Combine(Path.GetTempPath(), "KitWrightBrokerTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTempRoot(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }
}
