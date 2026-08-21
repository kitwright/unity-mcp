// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.MCP.Server.Security;
using KitWright.Editor.Settings;
using KitWright.Editor.State;
using KitWright.Editor.Threading;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class McpServerTests
    {
        private string _tempRoot;
        private Func<int, int, Task<bool>> _defaultBrokerAuthorize;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "kitwright-approval-" + Guid.NewGuid().ToString("N"));
            ClientApprovalStore.RootOverride = _tempRoot;
            _defaultBrokerAuthorize = MCPBrokerClientTransport.AuthorizeClient;
        }

        [TearDown]
        public void TearDown()
        {
            ClientApprovalStore.RootOverride = null;
            ClientApprovalGate.RequireApprovalOverride = null;
            ClientApprovalGate.ResolverOverride = null;
            ClientApprovalGate.ClearSessionAllowances();
            MCPBrokerClientTransport.AuthorizeClient = _defaultBrokerAuthorize;
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }

        [Test]
        public void Store_ApproveThenIsApproved_RoundTrips()
        {
            Assert.IsFalse(ClientApprovalStore.IsApproved(@"C:\clients\claude.exe"));

            ClientApprovalStore.Approve(@"C:\clients\claude.exe");

            Assert.IsTrue(ClientApprovalStore.IsApproved(@"C:\clients\claude.exe"));
            Assert.IsTrue(ClientApprovalStore.IsApproved(@"c:\CLIENTS\CLAUDE.EXE"), "paths compare case-insensitively");
        }

        [Test]
        public void Store_NullOrEmptyIdentity_NeverApproved()
        {
            ClientApprovalStore.Approve(null);
            ClientApprovalStore.Approve("");

            Assert.IsFalse(ClientApprovalStore.IsApproved(null));
            Assert.IsFalse(ClientApprovalStore.IsApproved(""));
        }

        [Test]
        public void Resolver_DecodePort_SwapsNetworkByteOrder()
        {
            // 8765 = 0x223D -> stored as 0x3D22 in the low word.
            Assert.AreEqual(8765, TcpClientProcessResolver.DecodePort(0x3D22));
            Assert.AreEqual(80, TcpClientProcessResolver.DecodePort(0x5000));
        }

        // The gate ships off, so this log is the only thing that names a client. Nothing else
        // asserts it, and batch mode returns before reaching it, hence BatchModeOverride.
        [Test]
        public void Gate_ApprovalDisabled_NamesEachClientOnceNotEachRequest()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var first = @"C:\probe\" + suffix + @"-one.exe";
            var second = @"C:\probe\" + suffix + @"-two.exe";

            var seen = new List<string>();
            UnityEngine.Application.LogCallback capture = (condition, stack, type) =>
            {
                if (condition != null && condition.Contains(suffix))
                    seen.Add(condition);
            };

            ClientApprovalGate.ClearNotedClients();
            ClientApprovalGate.BatchModeOverride = false;
            ClientApprovalGate.RequireApprovalOverride = () => false;
            ClientApprovalGate.ResolverOverride = (clientPort, serverPort) =>
                new TcpClientProcessResolver.ClientProcessInfo
                {
                    Pid = 4242,
                    ProcessName = "probe",
                    ExecutablePath = clientPort < 50000 ? first : second
                };
            UnityEngine.Application.logMessageReceived += capture;
            try
            {
                Assert.IsTrue(ClientApprovalGate.AuthorizeAsync(40001, 8765).GetAwaiter().GetResult());
                Assert.IsTrue(ClientApprovalGate.AuthorizeAsync(40002, 8765).GetAwaiter().GetResult());
                Assert.AreEqual(1, seen.Count,
                    "a second connection from the same executable must not log again");
                StringAssert.Contains(first, seen[0]);

                Assert.IsTrue(ClientApprovalGate.AuthorizeAsync(50001, 8765).GetAwaiter().GetResult());
                Assert.AreEqual(2, seen.Count, "a different executable is a different client");
                StringAssert.Contains(second, seen[1]);
            }
            finally
            {
                UnityEngine.Application.logMessageReceived -= capture;
                ClientApprovalGate.BatchModeOverride = null;
                ClientApprovalGate.RequireApprovalOverride = null;
                ClientApprovalGate.ResolverOverride = null;
                ClientApprovalGate.ClearNotedClients();
            }
        }

        // Every other gate test injects a resolver, so the real one had no coverage - and the
        // connected-client log is the first thing that depends on it in the shipped configuration.
        [Test]
        public void Resolver_NamesTheProcessOwningARealConnection()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, serverPort);
                using (var accepted = listener.AcceptTcpClient())
                {
                    var clientPort = ((IPEndPoint)accepted.Client.RemoteEndPoint).Port;
                    var info = TcpClientProcessResolver.Resolve(clientPort, serverPort);

#if UNITY_EDITOR_WIN
                    Assert.IsNotNull(info, "the resolver found no owner for a connection it can see");
                    Assert.AreEqual(Process.GetCurrentProcess().Id, info.Pid);
                    Assert.IsFalse(string.IsNullOrEmpty(info.ProcessName));
#else
                    // Only the Windows branch reads the TCP table, so every client elsewhere is
                    // unidentified - which is why session allowances have to stay.
                    Assert.IsNull(info);
#endif
                }
            }

            listener.Stop();
        }

        [Test]
        public async Task Gate_ApprovalDisabled_Allows()
        {
            ClientApprovalGate.RequireApprovalOverride = () => false;

            using (var pair = await LoopbackPair.CreateAsync())
                Assert.IsTrue(await ClientApprovalGate.AuthorizeAsync(pair.ServerSide, pair.ServerPort));
        }

        [Test]
        public async Task Gate_OwnEditorProcess_AllowsWithoutPrompt()
        {
            ClientApprovalGate.RequireApprovalOverride = () => true;
            ClientApprovalGate.ResolverOverride = (clientPort, serverPort) =>
                new TcpClientProcessResolver.ClientProcessInfo { Pid = Process.GetCurrentProcess().Id };

            using (var pair = await LoopbackPair.CreateAsync())
                Assert.IsTrue(await ClientApprovalGate.AuthorizeAsync(pair.ServerSide, pair.ServerPort));
        }

        [Test]
        public async Task Gate_PreviouslyApprovedExecutable_AllowsWithoutPrompt()
        {
            ClientApprovalGate.RequireApprovalOverride = () => true;
            ClientApprovalGate.ResolverOverride = (clientPort, serverPort) =>
                new TcpClientProcessResolver.ClientProcessInfo
                {
                    Pid = 99999,
                    ExecutablePath = @"C:\clients\approved.exe",
                    ProcessName = "approved"
                };
            ClientApprovalStore.Approve(@"C:\clients\approved.exe");

            using (var pair = await LoopbackPair.CreateAsync())
                Assert.IsTrue(await ClientApprovalGate.AuthorizeAsync(pair.ServerSide, pair.ServerPort));
        }

        [Test]
        public void Gate_UnapprovedClientProcess_IsRefusedUntilApproved()
        {
            var info = new TcpClientProcessResolver.ClientProcessInfo
            {
                Pid = 99999,
                ExecutablePath = @"C:\clients\stranger.exe",
                ProcessName = "stranger"
            };

            Assert.IsFalse(ClientApprovalGate.IsPreApproved(info, out var identity));
            Assert.AreEqual(@"C:\clients\stranger.exe", identity);

            ClientApprovalStore.Approve(identity);

            Assert.IsTrue(ClientApprovalGate.IsPreApproved(info, out _));
        }

        [Test]
        public void Gate_UnidentifiedClient_AllowedForTheSessionOnly()
        {
            Assert.IsFalse(ClientApprovalGate.IsPreApproved(null, out var identity));

            // What the "Allow this session" branch of the prompt does.
            ClientApprovalGate.AllowThisSession(identity);

            Assert.IsTrue(ClientApprovalGate.IsPreApproved(null, out _),
                "the session allowance stops the same unidentified client re-prompting");
            Assert.IsFalse(ClientApprovalStore.IsApproved(identity),
                "a session allowance must not persist as a permanent wildcard");

            ClientApprovalGate.ClearSessionAllowances();
            Assert.IsFalse(ClientApprovalGate.IsPreApproved(null, out _),
                "and it is gone once the session ends");
        }

        [Test]
        public void Gate_SettingsNotResolvableYet_DoesNotAskAndMatchesShippedDefault()
        {
            Assert.IsFalse(ClientApprovalGate.RequireApprovalFrom(null),
                "autostart delivers requests mid-reload, before the settings service resolves; " +
                "falling back to on there prompts users who shipped with the gate off");
            Assert.IsFalse(SettingsController.DefaultRequireClientApprovalEnabled,
                "the fallback tracks the shipped default, so flipping the default flips both");
            Assert.IsFalse(ClientApprovalGate.RequireApprovalFrom(new SettingsController(_tempRoot)),
                "a freshly created settings file carries the same default");
        }

        [Test]
        public void Gate_ElevatedClientWithNameButNoPath_IsIdentifiedByName()
        {
            var elevated = new TcpClientProcessResolver.ClientProcessInfo { Pid = 99999, ProcessName = "elevated" };

            Assert.IsTrue(ClientApprovalGate.IsIdentified(elevated),
                "MainModule throws for elevated processes, but the name still names the client");
            Assert.IsFalse(ClientApprovalGate.IsPreApproved(elevated, out var identity));
            Assert.AreEqual("elevated", identity);
        }

        private static TcpClientProcessResolver.ClientProcessInfo Client(string path)
            => new TcpClientProcessResolver.ClientProcessInfo { Pid = 99999, ExecutablePath = path, ProcessName = "client" };

        [Test]
        public void Gate_DeniedClient_IsRefusedWithoutPrompting()
        {
            try
            {
                ClientApprovalGate.DenyThisSession(@"C:\clients\denied.exe");
                ClientApprovalStore.Approve(@"C:\clients\allowed.exe");

                Assert.AreEqual(false, ClientApprovalGate.Decide(Client(@"C:\clients\denied.exe"), out var deniedIdentity),
                    "a denied client must be refused outright for the rest of the session, never re-prompted");
                Assert.AreEqual(@"C:\clients\denied.exe", deniedIdentity);
                Assert.AreEqual(true, ClientApprovalGate.Decide(Client(@"C:\clients\allowed.exe"), out _));
                Assert.IsNull(ClientApprovalGate.Decide(Client(@"C:\clients\unknown.exe"), out _),
                    "an unknown client still goes to the prompt path");

                Assert.AreEqual(false, ClientApprovalGate.Decide(Client(@"c:\CLIENTS\DENIED.EXE"), out _),
                    "paths compare case-insensitively");
                Assert.IsFalse(ClientApprovalStore.IsApproved(@"C:\clients\denied.exe"),
                    "a refusal must not leak into the persisted approval list");
            }
            finally
            {
                // A refusal must not survive into another fixture; it does not survive a reload either.
                ClientApprovalGate.ClearSessionDenials();
            }
        }

        [Test]
        public async Task BrokerDispatch_ConsultsApprovalGateAndRefusesUnapprovedClient()
        {
            var gatedPorts = new List<int>();
            MCPBrokerClientTransport.AuthorizeClient = (clientPort, brokerPort) =>
            {
                gatedPorts.Add(clientPort);
                return Task.FromResult(clientPort == 4242);
            };

            var refusal = await MCPBrokerClientTransport.RefuseUnapprovedClientAsync(5353, 8765, "1");

            Assert.IsNotNull(refusal, "an unapproved broker client must not reach tool dispatch");
            StringAssert.Contains("-32001", refusal);
            Assert.IsNull(await MCPBrokerClientTransport.RefuseUnapprovedClientAsync(4242, 8765, "1"));
            CollectionAssert.AreEqual(new[] { 5353, 4242 }, gatedPorts);
        }

        [Test]
        public void NegotiateProtocolVersion_EchoesSupportedRequestElseServerLatest()
        {
            Assert.AreEqual("2025-06-18", MCPRequestHandler.ProtocolVersion);
            Assert.AreEqual("2024-11-05", MCPRequestHandler.NegotiateProtocolVersion("2024-11-05"));
            Assert.AreEqual("2025-03-26", MCPRequestHandler.NegotiateProtocolVersion("2025-03-26"));
            Assert.AreEqual("2025-06-18", MCPRequestHandler.NegotiateProtocolVersion(null));
            Assert.AreEqual("2025-06-18", MCPRequestHandler.NegotiateProtocolVersion("1999-01-01"));
        }

        [Test]
        public async Task Ping_IsAnsweredWithAnEmptyResult()
        {
            var settings = new SettingsController(_tempRoot);
            using (var threadHelper = new EditorThreadHelper())
            using (var resourceProvider = new MCPResourceProvider(null, null))
            {
                var handler = new MCPRequestHandler(
                    new MCPToolExporter(settings),
                    new MCPExecutionBridge(threadHelper, settings, new StateController(), new FunctionInvoker(), null),
                    resourceProvider,
                    new MCPPromptProvider("Test", _tempRoot),
                    "KitWright MCP Server",
                    "0.0.0",
                    "pin");

                var response = await handler.HandleRequestAsync(
                    new MCPRequest { JsonRpc = "2.0", Id = 7, Method = "ping" }, CancellationToken.None);

                Assert.IsNotNull(response, "ping must be answered");
                Assert.IsNull(response.Error, "ping must not come back as an error");
                Assert.IsNotNull(response.Result);
                Assert.AreEqual(7, response.Id);
            }
        }

        [Test]
        public void TryParseEnvelope_SuccessEnvelope()
        {
            var found = MCPRequestHandler.TryParseEnvelope(
                "{\"success\":true,\"message\":\"ok\",\"data\":{\"n\":1}}", out var envelope, out var isError);

            Assert.IsTrue(found);
            Assert.IsFalse(isError);
            Assert.IsNotNull(envelope);
        }

        [Test]
        public void TryParseEnvelope_ErrorEnvelope_SetsIsError()
        {
            var found = MCPRequestHandler.TryParseEnvelope(
                "{\"success\":false,\"code\":\"BOOM\",\"error\":\"BOOM\"}", out _, out var isError);

            Assert.IsTrue(found);
            Assert.IsTrue(isError);
        }

        [Test]
        public void TryParseEnvelope_RejectsNonEnvelopeInputs()
        {
            Assert.IsFalse(MCPRequestHandler.TryParseEnvelope("plain text result", out _, out _));
            Assert.IsFalse(MCPRequestHandler.TryParseEnvelope("{\"foo\":1}", out _, out _), "json without success field");
            Assert.IsFalse(MCPRequestHandler.TryParseEnvelope("{\"success\":\"yes\"}", out _, out _), "success must be boolean");
            Assert.IsFalse(MCPRequestHandler.TryParseEnvelope(null, out _, out _));
            Assert.IsFalse(MCPRequestHandler.TryParseEnvelope("{not json", out _, out _));
        }

        [Test]
        public async Task WaitForHotReloadOutcome_CompilationAlreadyStarted_ReturnsTrueImmediately()
        {
            var result = await CompilationFunctions.WaitForHotReloadOutcomeAsync(
                () => true, TimeSpan.FromSeconds(10));

            Assert.IsTrue(result);
        }

        [Test]
        public async Task WaitForHotReloadOutcome_NeverCompiles_ReturnsFalse()
        {
            var result = await CompilationFunctions.WaitForHotReloadOutcomeAsync(
                () => false, TimeSpan.Zero);

            Assert.IsFalse(result);
        }

        [Test]
        public async Task WaitForHotReloadOutcome_CompilationStartsMidWait_ReturnsTrue()
        {
            int calls = 0;
            var result = await CompilationFunctions.WaitForHotReloadOutcomeAsync(
                () => ++calls >= 2, TimeSpan.FromSeconds(5));

            Assert.IsTrue(result);
        }

        private sealed class LoopbackPair : IDisposable
        {
            public TcpClient ServerSide;
            public TcpClient ClientSide;
            public int ServerPort;
            private TcpListener _listener;

            public static async Task<LoopbackPair> CreateAsync()
            {
                var pair = new LoopbackPair();
                pair._listener = new TcpListener(IPAddress.Loopback, 0);
                pair._listener.Start();
                pair.ServerPort = ((IPEndPoint)pair._listener.LocalEndpoint).Port;

                var acceptTask = pair._listener.AcceptTcpClientAsync();
                pair.ClientSide = new TcpClient();
                await pair.ClientSide.ConnectAsync(IPAddress.Loopback, pair.ServerPort);
                pair.ServerSide = await acceptTask;
                return pair;
            }

            public void Dispose()
            {
                ServerSide?.Dispose();
                ClientSide?.Dispose();
                _listener?.Stop();
            }
        }
    }
}
