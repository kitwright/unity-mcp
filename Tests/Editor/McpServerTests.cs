// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KitWright.Editor.MCP.Server;
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

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "kitwright-mcpserver-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
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

        // The profile lookup used to sit below the off-editor-thread branch, so a [OffEditorThread]
        // tool went straight to the invoker whatever the profile said. dismiss_editor_dialog is one
        // of them, and it clicks buttons on a modal - 'Don't Save' among them.
        [Test]
        public async Task AToolThatAnswersOffTheEditorThreadStillObeysToolExposure()
        {
            Assert.IsTrue(ToolRegistry.RunsOffEditorThread("get_editor_dialog"),
                "Setup: this test only means anything while get_editor_dialog takes the off-thread path.");

            var settings = new SettingsController(_tempRoot);
            using (var threadHelper = new EditorThreadHelper())
            {
                var bridge = new MCPExecutionBridge(
                    threadHelper, settings, new StateController(), new FunctionInvoker(), null);

                settings.MCPToolExportProfile = "minimal";
                var refused = await bridge.ExecuteToolAsync(
                    "get_editor_dialog", new Dictionary<string, object>(), CancellationToken.None);
                StringAssert.Contains("TOOL_NOT_EXPOSED", refused);

                settings.MCPToolExportProfile = "full";
                var allowed = await bridge.ExecuteToolAsync(
                    "get_editor_dialog", new Dictionary<string, object>(), CancellationToken.None);
                StringAssert.DoesNotContain("TOOL_NOT_EXPOSED", allowed,
                    "A profile that exposes the tool must still reach it.");
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

        private static List<Dictionary<string, object>> TextContent(string text) =>
            new List<Dictionary<string, object>> { new Dictionary<string, object> { ["type"] = "text", ["text"] = text } };

        [Test]
        public void AnOversizedTextResultSpillsToAFileThatHoldsAllOfIt()
        {
            var text = "{\"success\":true,\"data\":\"" + new string('x', MCPRequestHandler.MaxInlineTextChars) + "\"}";
            var content = TextContent(text);

            Assert.IsTrue(MCPRequestHandler.SpillOversizedText(content, "get_hierarchy", _tempRoot));

            var files = Directory.GetFiles(_tempRoot, "*.txt");
            Assert.AreEqual(1, files.Length);
            StringAssert.EndsWith("_get_hierarchy.txt", files[0]);
            Assert.AreEqual(text, File.ReadAllText(files[0]), "The file must hold the whole result, not the preview.");

            var inline = (string)content[0]["text"];
            StringAssert.Contains(files[0], inline, "The answer must say where the rest is.");
            StringAssert.EndsWith(text.Substring(0, MCPRequestHandler.SpillPreviewChars), inline);
            Assert.Less(inline.Length, MCPRequestHandler.SpillPreviewChars + 1024);
        }

        [Test]
        public void ASmallResultAndAnImageStayInline()
        {
            var small = TextContent("small");
            var image = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["type"] = "image",
                    ["data"] = new string('A', MCPRequestHandler.MaxInlineTextChars * 2),
                    ["mimeType"] = "image/png"
                }
            };

            Assert.IsFalse(MCPRequestHandler.SpillOversizedText(small, "get_selection", _tempRoot));
            Assert.IsFalse(MCPRequestHandler.SpillOversizedText(image, "capture_game_view", _tempRoot));
            Assert.AreEqual("small", small[0]["text"]);
            Assert.AreEqual(MCPRequestHandler.MaxInlineTextChars * 2, ((string)image[0]["data"]).Length);
            Assert.IsFalse(Directory.Exists(_tempRoot), "Nothing to spill must write nothing.");
        }

        [Test]
        public void SpillingKeepsOnlyTheNewestOutputs()
        {
            Directory.CreateDirectory(_tempRoot);
            for (var i = 0; i < 25; i++)
                File.WriteAllText(Path.Combine(_tempRoot, $"20000101-000000-{i:000}_old.txt"), "old");

            MCPRequestHandler.SpillOversizedText(
                TextContent(new string('x', MCPRequestHandler.MaxInlineTextChars + 1)), "find_assets", _tempRoot);

            var names = Directory.GetFiles(_tempRoot, "*.txt").Select(Path.GetFileName).ToArray();
            Assert.AreEqual(MCPRequestHandler.SpilledOutputsKept, names.Length);
            Assert.IsTrue(names.Any(name => name.EndsWith("_find_assets.txt")), "The spill just written must survive.");
            Assert.IsFalse(names.Contains("20000101-000000-005_old.txt"), "The oldest go first.");
            Assert.IsTrue(names.Contains("20000101-000000-006_old.txt"));
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

    }
}
