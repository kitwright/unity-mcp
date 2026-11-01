// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Settings;
using KitWright.Editor.State;
using KitWright.Editor.Threading;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace KitWright.Editor.Tests
{
    public sealed class RecoveryAndTimeoutTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "kitwright-recovery-" + Guid.NewGuid().ToString("N"));
            Undo.IncrementCurrentGroup();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }

        private static MCPRequest Redelivery(string tool) => new MCPRequest
        {
            Id = "1",
            Method = "tools/call",
            IsBrokerRedelivery = true,
            Params = new Dictionary<string, object> { ["name"] = tool }
        };

        [Test]
        public void RecoveredCompile_WithScriptsStillNewerThanTheLastCompile_IsNotASuccess()
        {
            var outcome = CompilationFunctions.DescribeRecoveredCompile("No compilation errors or warnings detected.", 3);

            Assert.AreEqual(MCPToolCallStatus.Error, outcome.Status);
            StringAssert.Contains("SCRIPT_CHANGES_NOT_COMPILED", outcome.Summary);
            StringAssert.Contains("3 script file(s)", outcome.Summary);
            StringAssert.Contains("request_recompile", outcome.Summary);
        }

        [Test]
        public void RecoveredCompile_WithNothingPendingAndNoIssues_IsASuccess()
        {
            foreach (var clean in new[] { "No compilation errors or warnings detected.", "No compilation errors detected." })
                Assert.AreEqual(MCPToolCallStatus.Success, CompilationFunctions.DescribeRecoveredCompile(clean, 0).Status);
        }

        [Test]
        public void RecoveredCompile_ReportsCompilerIssuesBeforeStaleness()
        {
            var outcome = CompilationFunctions.DescribeRecoveredCompile("Assets/A.cs(1,1): error CS1002: ; expected", 2);

            Assert.AreEqual(MCPToolCallStatus.Error, outcome.Status);
            StringAssert.Contains("CS1002", outcome.Summary);
            StringAssert.DoesNotContain("SCRIPT_CHANGES_NOT_COMPILED", outcome.Summary);
        }

        [Test]
        public void GenericInterruptedRecord_DoesNotOverwriteTheOneTheNewDomainAlreadyWrote()
        {
            var reloadedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
            var written = new DomainReloadHandler.RecoveryInfo
            {
                ToolName = "request_recompile",
                Timestamp = reloadedAt.AddSeconds(2)
            };

            Assert.IsTrue(DomainReloadHandler.IsRecordedSince(written, "Request_Recompile", reloadedAt),
                "the record written after the reload carries the Play Mode errors; the generic one must not replace it");

            written.Timestamp = reloadedAt.AddMinutes(-3);
            Assert.IsFalse(DomainReloadHandler.IsRecordedSince(written, "request_recompile", reloadedAt),
                "a record from before the reload belongs to an earlier call");

            written.Timestamp = reloadedAt.AddSeconds(2);
            Assert.IsFalse(DomainReloadHandler.IsRecordedSince(written, "execute_code", reloadedAt));
            Assert.IsFalse(DomainReloadHandler.IsRecordedSince(null, "request_recompile", reloadedAt));
            Assert.IsFalse(DomainReloadHandler.IsRecordedSince(written, "request_recompile", DateTime.MinValue),
                "without a reload time there is nothing to compare against");
        }

        [Test]
        public void StoredRecoveryRecord_ReadsAsWrittenAfterAnEarlierReloadAndNotAfterALaterOne()
        {
            try
            {
                DomainReloadHandler.StoreRecoveryInfo("request_recompile", "Error", "kept");
                var stored = DomainReloadHandler.GetLastRecoveryInfo(false);

                Assert.IsTrue(DomainReloadHandler.IsRecordedSince(stored, "request_recompile", DateTime.Now.AddSeconds(-5)));
                Assert.IsFalse(DomainReloadHandler.IsRecordedSince(stored, "request_recompile", DateTime.Now.AddSeconds(5)));
            }
            finally
            {
                DomainReloadHandler.GetLastRecoveryInfo(consume: true);
            }
        }

        [Test]
        public void ConsumeInterruptedState_CarriesTheReloadTime()
        {
            try
            {
                DomainReloadHandler.SaveState(KitWrightState.ExecutingFunction);
                DomainReloadHandler.SavePendingFunction(new FunctionCall { FunctionName = "build_player" });
                var interrupted = DomainReloadHandler.ConsumeInterruptedState();

                Assert.NotNull(interrupted);
                Assert.Greater(interrupted.ReloadedAt, DateTime.Now.AddSeconds(-30));
            }
            finally
            {
                DomainReloadHandler.ClearPendingFunction();
            }
        }

        [Test]
        public void OnlyAMutatingCallMarksItselfPending()
        {
            Assert.IsFalse(MCPExecutionBridge.MarksPendingFunction(ToolRegistry.GetMethod("get_editor_state")));
            Assert.IsTrue(MCPExecutionBridge.MarksPendingFunction(ToolRegistry.GetMethod("set_transform")));
            Assert.IsTrue(MCPExecutionBridge.MarksPendingFunction(null), "a manual tool has no attributes and is treated as mutating");
        }

        [Test]
        public async Task AFinishedReadOnlyCall_LeavesTheMarkerOfAMutatingCallStillRunning()
        {
            var settings = new SettingsController(_tempRoot);
            settings.MCPToolExportProfile = "full";
            using (var threadHelper = new EditorThreadHelper())
            {
                var bridge = new MCPExecutionBridge(
                    threadHelper, settings, new StateController(), new FunctionInvoker(), null);

                try
                {
                    DomainReloadHandler.SavePendingFunction(new FunctionCall { FunctionName = "build_player" });

                    await bridge.ExecuteToolAsync("get_editor_state", new Dictionary<string, object>(), CancellationToken.None);

                    var interrupted = DomainReloadHandler.ConsumeInterruptedState();
                    Assert.NotNull(interrupted, "the read finished and wiped the marker of the call still running");
                    Assert.AreEqual("build_player", interrupted.PendingFunction.FunctionName);
                }
                finally
                {
                    DomainReloadHandler.ClearPendingFunction();
                }
            }
        }

        [Test]
        public void SyncContextLostInAnInitializeOnLoadConstructor_IsTakenAgainOnTheEditorThread()
        {
            var original = SynchronizationContext.Current;
            var current = new SynchronizationContext();
            try
            {
                SynchronizationContext.SetSynchronizationContext(current);

                Assert.AreSame(current, EditorThreadHelper.CaptureIfMissing(null));

                var captured = new SynchronizationContext();
                Assert.AreSame(captured, EditorThreadHelper.CaptureIfMissing(captured),
                    "a context already captured stays");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(original);
            }
        }

        [Test]
        public void CeilingForBatchAndExecuteCode_DoesNotDependOnTheCaseOfTheName()
        {
            var batch = new Dictionary<string, object>
            {
                ["name"] = "Batch_Execute",
                ["arguments"] = new Dictionary<string, object>
                {
                    ["commands"] = "[{\"name\":\"bake_nav_mesh\",\"params\":{}}]"
                }
            };
            Assert.AreEqual(180 + 900, ToolRegistry.TimeoutSecondsForRequest("tools/call", batch),
                "the batch budgets itself from the same list, so a shorter ceiling cuts it off");

            var executeCode = new Dictionary<string, object>
            {
                ["name"] = "Execute_Code",
                ["arguments"] = new Dictionary<string, object> { ["timeout_seconds"] = 300 }
            };
            Assert.AreEqual(300 + 120, ToolRegistry.TimeoutSecondsForRequest("tools/call", executeCode));

            var replay = new Dictionary<string, object>
            {
                ["name"] = "replay_execute_code",
                ["arguments"] = new Dictionary<string, object> { ["timeout_seconds"] = 300 }
            };
            Assert.AreEqual(300 + 120, ToolRegistry.TimeoutSecondsForRequest("tools/call", replay),
                "a replay with a long timeout_seconds must not be cut off at the ordinary ceiling");
        }

        [Test]
        public void ToolTimeoutMessage_PointsExecuteCodeAtItsOptionsWhateverTheCase()
        {
            StringAssert.Contains("timeout_seconds", MCPServerService.ToolTimeoutMessage("Execute_Code", 170_000));
            StringAssert.Contains("timeout_seconds", MCPServerService.ToolTimeoutMessage("replay_execute_code", 170_000));
        }

        [Test]
        public async Task BrokerRedelivery_OfRecompileWaitsWhateverTheCaseOfTheName()
        {
            CompilationFunctions.MarkExternalSyncPending();
            try
            {
                DomainReloadHandler.StoreRecoveryInfo("request_recompile", MCPToolCallStatus.Success.ToString(), "compiled after reload");

                var answer = MCPServerService.TryCreateBrokerRedeliveryResponseAsync(
                    Redelivery("Request_Recompile"), TimeSpan.FromSeconds(30));
                await Task.Delay(600);
                Assert.IsFalse(answer.IsCompleted, "answered while the recompile was still pending");

                CompilationFunctions.ClearExternalSyncPending();
                Assert.NotNull(await answer);
            }
            finally
            {
                CompilationFunctions.ClearExternalSyncPending();
                DomainReloadHandler.GetLastRecoveryInfo(consume: true);
            }
        }

        [Test]
        public void InterruptedStub_OfAReadOnlyToolSaysToCallItAgainNotToLookForARecord()
        {
            var readOnly = MCPServerService.DescribeInterruptedCall("get_lighting_settings", true);
            StringAssert.Contains("read-only", readOnly);
            StringAssert.Contains("just call it again", readOnly);
            StringAssert.Contains("'get_lighting_settings'", readOnly);

            var mutating = MCPServerService.DescribeInterruptedCall("set_transform", false);
            StringAssert.Contains("was not re-run automatically", mutating);
            StringAssert.Contains("Call get_reload_recovery_status", mutating);
            StringAssert.DoesNotContain("read-only", mutating);
        }

        [Test]
        public void BrokerRedelivery_OfAReadOnlyToolThatIsNotReplaySafeAnswersTheReadOnlyStub()
        {
            Assume.That(ToolRegistry.IsReadOnly(ToolRegistry.GetMethod("get_lighting_settings")), "tool is no longer read-only");
            Assume.That(ToolRegistry.IsReplaySafe("get_lighting_settings"), Is.False, "tool became replay-safe");
            DomainReloadHandler.GetLastRecoveryInfo(consume: true);

            var response = MCPServerService.TryCreateBrokerRedeliveryResponse(Redelivery("get_lighting_settings"));

            Assert.NotNull(response);
            var text = JsonConvert.SerializeObject(response.Result);
            StringAssert.Contains("just call it again", text);
            StringAssert.DoesNotContain("Call get_reload_recovery_status", text);
        }

        [Test]
        public void BatchExecute_RefusesABatchInsideABatch()
        {
            var batch = BatchFunctions.BatchExecute(
                "[{\"name\":\"Batch_Execute\",\"params\":{\"commands\":\"[{\\\"name\\\":\\\"get_editor_state\\\"}]\"}}]");

            Assert.IsTrue(batch.IsCompleted, "a refused command must not be run or awaited");
            var envelope = JObject.Parse(JsonConvert.SerializeObject(batch.Result));
            Assert.AreEqual("BATCH_ABORTED", (string)envelope["code"]);
            Assert.AreEqual("BATCH_NESTED", (string)envelope["data"]["results"][0]["result"]["code"]);
        }
    }
}
