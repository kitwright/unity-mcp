// Copyright (C) KitWright. All rights reserved.

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
using Step = KitWright.Editor.Tools.Builtins.ExternalSyncRecoveryTracker.RecoveryStep;

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
        public void InterruptedRecompile_WhileTheTrackerStillWaits_DoesNotKeepTheTransientNotCompiledRead()
        {
            var notCompiled = CompilationFunctions.DescribeRecoveredCompile("No compilation errors detected.", 3).Summary;
            var withIssues = CompilationFunctions.DescribeRecoveredCompile("Assets/A.cs(1,1): error CS1002: ; expected", 3).Summary;
            var clean = CompilationFunctions.DescribeRecoveredCompile("No compilation errors detected.", 0).Summary;

            Assert.IsTrue(CompilationFunctions.IsNotCompiledOutcome(notCompiled));
            Assert.IsFalse(CompilationFunctions.IsNotCompiledOutcome(withIssues));
            Assert.IsFalse(CompilationFunctions.IsNotCompiledOutcome(clean));
            Assert.IsFalse(CompilationFunctions.IsNotCompiledOutcome(null));

            Assert.IsTrue(MCPServerService.IsTransientNotCompiled(true, notCompiled));
            Assert.IsFalse(MCPServerService.IsTransientNotCompiled(false, notCompiled), "nothing else records the outcome then");
            Assert.IsFalse(MCPServerService.IsTransientNotCompiled(true, withIssues));
            Assert.IsFalse(MCPServerService.IsTransientNotCompiled(true, clean));
        }

        [Test]
        public void ExternalSyncPending_AgesInUtcAndStillReadsTheOldLocalFormat()
        {
            var now = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

            Assert.IsTrue(CompilationFunctions.IsExternalSyncStillPending(now.AddSeconds(-110).ToString("O"), now));
            Assert.IsFalse(CompilationFunctions.IsExternalSyncStillPending(now.AddSeconds(-130).ToString("O"), now));

            var recentLocal = new DateTimeOffset(now.AddSeconds(-60)).ToOffset(TimeSpan.FromHours(7)).ToString("O");
            var staleLocal = new DateTimeOffset(now.AddSeconds(-300)).ToOffset(TimeSpan.FromHours(-8)).ToString("O");
            Assert.IsTrue(CompilationFunctions.IsExternalSyncStillPending(recentLocal, now), "a stamp an older domain wrote in local time");
            Assert.IsFalse(CompilationFunctions.IsExternalSyncStillPending(staleLocal, now));

            foreach (var unreadable in new[] { null, "", "not a date" })
                Assert.IsFalse(CompilationFunctions.IsExternalSyncStillPending(unreadable, now), unreadable ?? "null");
        }

        [Test]
        public void MarkExternalSyncPending_StampsUtc()
        {
            try
            {
                CompilationFunctions.MarkExternalSyncPending();

                StringAssert.EndsWith("Z", SessionState.GetString(CompilationFunctions.ExternalSyncStartedAtKey, ""));
                Assert.IsTrue(CompilationFunctions.HasPendingExternalSync());
            }
            finally
            {
                CompilationFunctions.ClearExternalSyncPending();
            }
        }

        private static DomainReloadHandler.RecoveryInfo RecordedAt(string toolName, DateTime utc) =>
            new DomainReloadHandler.RecoveryInfo { ToolName = toolName, TimestampUtc = utc, Timestamp = utc.ToLocalTime() };

        [Test]
        public void GenericInterruptedRecord_DoesNotOverwriteTheOneTheNewDomainAlreadyWrote()
        {
            var reloadedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var written = RecordedAt("request_recompile", reloadedAt.AddSeconds(2));

            Assert.IsTrue(DomainReloadHandler.IsRecordedSince(written, "Request_Recompile", reloadedAt),
                "the record written after the reload carries the Play Mode errors; the generic one must not replace it");
            Assert.IsTrue(DomainReloadHandler.IsRecordedSince(written, "request_recompile", reloadedAt.ToLocalTime()),
                "the reload time compares as the same instant whatever its Kind");

            written.TimestampUtc = reloadedAt.AddMinutes(-3);
            Assert.IsFalse(DomainReloadHandler.IsRecordedSince(written, "request_recompile", reloadedAt),
                "a record from before the reload belongs to an earlier call");

            written.TimestampUtc = reloadedAt.AddSeconds(2);
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
                Assert.Less(Math.Abs((DateTime.Now - stored.Timestamp).TotalMinutes), 1.0,
                    "Timestamp stays local time: the broker redelivery subtracts it from DateTime.Now");
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
                Assert.AreEqual(DateTimeKind.Utc, interrupted.ReloadedAt.Kind);
                Assert.Greater(interrupted.ReloadedAt, DateTime.UtcNow.AddSeconds(-30));
            }
            finally
            {
                DomainReloadHandler.ClearPendingFunction();
            }
        }

        [Test]
        public void RecoveryTimestamps_ReadBackAsTheSameInstantInTheNewAndTheOldLocalFormat()
        {
            var utcNow = DateTime.UtcNow;
            Assert.IsTrue(DomainReloadHandler.TryParseUtc(utcNow.ToString("O"), out var fromUtc));
            Assert.AreEqual(utcNow, fromUtc);

            var localNow = DateTime.Now;
            Assert.IsTrue(DomainReloadHandler.TryParseUtc(localNow.ToString("O"), out var fromLocal));
            Assert.AreEqual(localNow.ToUniversalTime(), fromLocal, "a record a previous domain wrote in local time still reads");

            Assert.IsTrue(DomainReloadHandler.TryParseUtc("2026-01-01T12:00:00.0000000+07:00", out var offset));
            Assert.AreEqual(new DateTime(2026, 1, 1, 5, 0, 0, DateTimeKind.Utc), offset);

            foreach (var unreadable in new[] { null, "", "not a date" })
            {
                Assert.IsFalse(DomainReloadHandler.TryParseUtc(unreadable, out var none));
                Assert.AreEqual(DateTime.MinValue, none);
            }

            Assert.IsFalse(DomainReloadHandler.IsRecordedSince(
                new DomainReloadHandler.RecoveryInfo { ToolName = "request_recompile" }, "request_recompile", utcNow),
                "a record with no readable timestamp never counts as recorded, so the generic record is still stored");
        }

        [Test]
        public void ARecordWrittenAfterADstFallBackReloadStillReadsAsLater()
        {
            // 01:30 PDT then 01:10 PST the same night: the wall clock went backwards, the instants did not.
            Assert.IsTrue(DomainReloadHandler.TryParseUtc("2026-11-01T08:30:00.0000000Z", out var reloadedAt));
            Assert.IsTrue(DomainReloadHandler.TryParseUtc("2026-11-01T09:10:00.0000000Z", out var recordedAt));
            Assert.IsTrue(DomainReloadHandler.IsRecordedSince(RecordedAt("request_recompile", recordedAt), "request_recompile", reloadedAt));

            Assert.IsTrue(DomainReloadHandler.TryParseUtc("2026-11-01T01:30:00.0000000-07:00", out var oldReload));
            Assert.IsTrue(DomainReloadHandler.TryParseUtc("2026-11-01T01:10:00.0000000-08:00", out var oldRecord));
            Assert.IsTrue(DomainReloadHandler.IsRecordedSince(RecordedAt("request_recompile", oldRecord), "request_recompile", oldReload),
                "the same holds for stamps an earlier domain wrote in local time");
        }

        [Test]
        public void Recovery_WaitsForTheCompileWhileScriptsAreStaleAndNoneHasStarted()
        {
            var grace = ExternalSyncRecoveryTracker.NotCompiledGraceSeconds;

            Assert.AreEqual(Step.Wait, ExternalSyncRecoveryTracker.DecideRecovery(false, false, 3, 0),
                "leaving Play Mode reloads before the compile starts; recording now would be a final-looking error");
            Assert.AreEqual(Step.Wait, ExternalSyncRecoveryTracker.DecideRecovery(false, false, 3, grace - 0.1));
            Assert.AreEqual(Step.RecordNotCompiled, ExternalSyncRecoveryTracker.DecideRecovery(false, false, 3, grace),
                "nothing started within the grace period");
        }

        [Test]
        public void Recovery_RecordsTheRealOutcomeOnceACompileHasRun()
        {
            Assert.AreEqual(Step.Wait, ExternalSyncRecoveryTracker.DecideRecovery(true, true, 3, 1000),
                "a compile in flight is waited out whatever the clock says");
            Assert.AreEqual(Step.RecordOutcome, ExternalSyncRecoveryTracker.DecideRecovery(false, true, 3, 5),
                "the compile ended: its result is the record, not the staleness measured before it");
            Assert.AreEqual(Step.RecordOutcome, ExternalSyncRecoveryTracker.DecideRecovery(false, false, 0, 0),
                "nothing stale, nothing to wait for");
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
