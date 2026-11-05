// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    public sealed class BatchFunctionsTests
    {
        [Test]
        public void IsSuccess_ImageDataUri_CountsAsSuccess()
        {
            const string raw = "data:image/png;base64,iVBORw0KGgo=";

            Assert.IsTrue(BatchFunctions.IsSuccess(raw, raw),
                "a screenshot never carries a success field, and stop_on_error must not read that as a failure");
        }

        [Test]
        public void IsSuccess_ReadsTheEnvelopeForEverythingElse()
        {
            Assert.IsTrue(BatchFunctions.IsSuccess("{\"success\":true}", JToken.Parse("{\"success\":true}")));
            Assert.IsFalse(BatchFunctions.IsSuccess("{\"success\":false}", JToken.Parse("{\"success\":false}")));
            Assert.IsFalse(BatchFunctions.IsSuccess("not json", "not json"));
        }

        [Test]
        public void ExtractParams_LeavesAJsonNullOutInsteadOfPassingTheWordNull()
        {
            var extracted = BatchFunctions.ExtractParams(JToken.Parse("{\"a\":null,\"b\":\"text\",\"c\":2,\"d\":true}"));

            CollectionAssert.AreEquivalent(new[] { "b", "c", "d" }, extracted.Keys);
            Assert.AreEqual("text", extracted["b"]);
        }

        // BatchExecute collapses every undo step it sees down to the group that was current when it
        // started. Run without this, it swallows the test runner's own group, and the runner's
        // Undo.RevertAllDownToGroup after the run then reverts into a group that no longer exists -
        // which takes the editor down natively, not as a test failure.
        [SetUp]
        public void IsolateUndoGroup() => Undo.IncrementCurrentGroup();

        [UnityTest]
        public IEnumerator BatchExecute_StoppedByAFailingCommand_ReportsFailure()
        {
            var batch = BatchFunctions.BatchExecute(
                "[{\"name\":\"no_such_tool_for_tests\",\"params\":{}},{\"name\":\"get_editor_state\",\"params\":{}}]");

            yield return WaitForTask(batch);

            var envelope = JObject.Parse(JsonConvert.SerializeObject(batch.Result));

            Assert.IsFalse(envelope["success"].Value<bool>(),
                "an aborted batch reported as success hides the failure from isError, and from a parent batch");
            Assert.AreEqual("BATCH_ABORTED", envelope["code"].Value<string>());
            Assert.AreEqual(1, envelope["data"]["count"].Value<int>(), "the batch must stop at the failing command");
            Assert.AreEqual(2, envelope["data"]["total"].Value<int>());
            Assert.IsTrue(envelope["data"]["aborted"].Value<bool>());
            Assert.AreEqual(1, envelope["data"]["results"].Count(), "the step results survive the error envelope");
        }

        [UnityTest]
        public IEnumerator BatchExecute_AnUnknownToolAnswersUnknownToolLikeADirectCall()
        {
            var batch = BatchFunctions.BatchExecute("[{\"name\":\"no_such_tool_for_tests\",\"params\":{}}]");

            yield return WaitForTask(batch);

            var result = JObject.Parse(JsonConvert.SerializeObject(batch.Result))["data"]["results"][0]["result"];
            Assert.AreEqual("UNKNOWN_TOOL", result["code"].Value<string>());
            Assert.AreEqual("no_such_tool_for_tests", result["data"]["tool"].Value<string>());
        }

        [UnityTest]
        public IEnumerator BatchExecute_AllCommandsSucceed_ReportsSuccess()
        {
            var batch = BatchFunctions.BatchExecute("[{\"name\":\"get_editor_state\",\"params\":{}}]");

            yield return WaitForTask(batch);

            var envelope = JObject.Parse(JsonConvert.SerializeObject(batch.Result));

            Assert.IsTrue(envelope["success"].Value<bool>());
            Assert.IsFalse(envelope["data"]["aborted"].Value<bool>());
        }

        // Collapsing onto the group that was current when the batch started took in whatever the
        // user had registered in it just before: one Ctrl+Z then reverted their edit with the batch.
        [UnityTest]
        public IEnumerator BatchExecute_OneUndoRevertsTheBatchAndNothingBeforeIt()
        {
            var before = new GameObject("kw-batch-undo-before");
            var target = new GameObject("kw-batch-undo-target");
            try
            {
                Undo.RecordObject(before.transform, "move before the batch");
                before.transform.position = new Vector3(1f, 0f, 0f);
                Undo.FlushUndoRecordObjects();

                var batch = BatchFunctions.BatchExecute(
                    "[{\"name\":\"set_transform\",\"params\":{\"target\":\"kw-batch-undo-target\",\"position\":\"5,0,0\"}}]");
                yield return WaitForTask(batch);
                Assert.IsTrue(JObject.Parse(JsonConvert.SerializeObject(batch.Result))["success"].Value<bool>());
                Assert.AreEqual(new Vector3(5f, 0f, 0f), target.transform.position);

                Undo.PerformUndo();

                Assert.AreEqual(Vector3.zero, target.transform.position, "one undo reverts the batch");
                Assert.AreEqual(new Vector3(1f, 0f, 0f), before.transform.position, "and only the batch");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(before);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [UnityTest]
        public IEnumerator BatchExecute_PastItsBudget_StopsAndSaysSo()
        {
            var margin = BatchFunctions.DeadlineMarginSeconds;
            BatchFunctions.DeadlineMarginSeconds = 100_000;
            try
            {
                var batch = BatchFunctions.BatchExecute("[{\"name\":\"get_editor_state\",\"params\":{}}]");
                yield return WaitForTask(batch);

                var envelope = JObject.Parse(JsonConvert.SerializeObject(batch.Result));
                Assert.IsFalse(envelope["success"].Value<bool>());
                Assert.AreEqual("BATCH_TIMED_OUT", envelope["code"].Value<string>());
                Assert.AreEqual(0, envelope["data"]["count"].Value<int>(), "nothing may start past the deadline");
            }
            finally
            {
                BatchFunctions.DeadlineMarginSeconds = margin;
            }
        }

        private const string MoveTargetThenFail =
            "[{\"name\":\"set_transform\",\"params\":{\"target\":\"kw-batch-rollback-target\",\"position\":\"5,0,0\"}}," +
            "{\"name\":\"no_such_tool_for_tests\",\"params\":{}}]";

        [UnityTest]
        public IEnumerator BatchExecute_RollbackOnError_RevertsTheBatchAndKeepsTheUndoStack()
        {
            var before = new GameObject("kw-batch-rollback-before");
            var target = new GameObject("kw-batch-rollback-target");
            try
            {
                Undo.RecordObject(before.transform, "move before the batch");
                before.transform.position = new Vector3(1f, 0f, 0f);
                Undo.FlushUndoRecordObjects();

                var batch = BatchFunctions.BatchExecute(MoveTargetThenFail, rollback_on_error: true);
                yield return WaitForTask(batch);

                var envelope = JObject.Parse(JsonConvert.SerializeObject(batch.Result));
                Assert.AreEqual("BATCH_ABORTED", envelope["code"].Value<string>());
                Assert.IsTrue(envelope["data"]["rolled_back"].Value<bool>());
                StringAssert.Contains("resend the whole batch", envelope["hint"].Value<string>());
                Assert.AreEqual(Vector3.zero, target.transform.position, "the batch's move is reverted");
                Assert.AreEqual(new Vector3(1f, 0f, 0f), before.transform.position, "the user's change before it is not");

                Undo.PerformUndo();
                Assert.AreEqual(Vector3.zero, before.transform.position, "the user's own step is still on the stack");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(before);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [UnityTest]
        public IEnumerator BatchExecute_WithoutRollback_KeepsWhatRanBeforeTheFailure()
        {
            var target = new GameObject("kw-batch-rollback-target");
            try
            {
                var batch = BatchFunctions.BatchExecute(MoveTargetThenFail);
                yield return WaitForTask(batch);

                var envelope = JObject.Parse(JsonConvert.SerializeObject(batch.Result));
                Assert.AreEqual("BATCH_ABORTED", envelope["code"].Value<string>());
                Assert.IsNull(envelope["data"]["rolled_back"], "batch_execute's payload is unchanged without the option");
                Assert.AreEqual(new Vector3(5f, 0f, 0f), target.transform.position);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [UnityTest]
        public IEnumerator BatchExecute_RollbackOfABatchThatChangedNothing_LeavesTheUserStepAlone()
        {
            var before = new GameObject("kw-batch-rollback-before");
            try
            {
                Undo.RecordObject(before.transform, "move before the batch");
                before.transform.position = new Vector3(1f, 0f, 0f);
                Undo.FlushUndoRecordObjects();

                var batch = BatchFunctions.BatchExecute(
                    "[{\"name\":\"get_editor_state\",\"params\":{}},{\"name\":\"no_such_tool_for_tests\",\"params\":{}}]",
                    rollback_on_error: true);
                yield return WaitForTask(batch);

                Assert.AreEqual(new Vector3(1f, 0f, 0f), before.transform.position);
                Undo.PerformUndo();
                Assert.AreEqual(Vector3.zero, before.transform.position, "the empty group's revert popped nothing of the user's");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(before);
            }
        }

        [UnityTest]
        public IEnumerator BatchExecute_RollbackRefusesUndoBeforeAnythingRuns()
        {
            var target = new GameObject("kw-batch-rollback-target");
            try
            {
                var batch = BatchFunctions.BatchExecute(
                    "[{\"name\":\"set_transform\",\"params\":{\"target\":\"kw-batch-rollback-target\",\"position\":\"5,0,0\"}}," +
                    "{\"name\":\"undo\",\"params\":{}}]",
                    rollback_on_error: true);
                yield return WaitForTask(batch);

                var envelope = JObject.Parse(JsonConvert.SerializeObject(batch.Result));
                Assert.AreEqual("BATCH_UNDO_IN_ROLLBACK", envelope["code"].Value<string>());
                Assert.AreEqual(Vector3.zero, target.transform.position, "refused before the first command ran");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [UnityTest]
        public IEnumerator BatchExecute_RollbackSkippedWhenPlayModeChanged()
        {
            var target = new GameObject("kw-batch-rollback-target");
            var isPlaying = BatchFunctions.IsPlaying;
            var calls = 0;
            BatchFunctions.IsPlaying = () => calls++ > 0;
            try
            {
                var batch = BatchFunctions.BatchExecute(MoveTargetThenFail, rollback_on_error: true);
                yield return WaitForTask(batch);

                var data = JObject.Parse(JsonConvert.SerializeObject(batch.Result))["data"];
                Assert.IsFalse(data["rolled_back"].Value<bool>());
                Assert.AreEqual("play_mode_changed", data["rollback_skipped"].Value<string>());
                Assert.AreEqual(new Vector3(5f, 0f, 0f), target.transform.position);
            }
            finally
            {
                BatchFunctions.IsPlaying = isPlaying;
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [UnityTest]
        public IEnumerator BatchExecute_RolledBackAtItsBudget_AsksToResendTheWholeBatch()
        {
            var margin = BatchFunctions.DeadlineMarginSeconds;
            BatchFunctions.DeadlineMarginSeconds = 100_000;
            try
            {
                var batch = BatchFunctions.BatchExecute("[{\"name\":\"get_editor_state\",\"params\":{}}]", rollback_on_error: true);
                yield return WaitForTask(batch);

                var envelope = JObject.Parse(JsonConvert.SerializeObject(batch.Result));
                Assert.AreEqual("BATCH_TIMED_OUT", envelope["code"].Value<string>());
                Assert.IsTrue(envelope["data"]["rolled_back"].Value<bool>());
                StringAssert.Contains("Resend the whole batch", envelope["hint"].Value<string>());
            }
            finally
            {
                BatchFunctions.DeadlineMarginSeconds = margin;
            }
        }

        [Test]
        public void ResolveReferences_ReplacesWholeValueReferencesAndLeavesTextAlone()
        {
            var results = new JToken[]
            {
                JObject.Parse("{\"success\":true,\"message\":\"ok\",\"data\":{\"instanceId\":\"12\",\"empty\":null,\"nested\":{\"a\":1},\"list\":[{\"b\":2},{\"b\":3}]}}"),
                JObject.Parse("{\"success\":false,\"code\":\"NOPE\"}"),
                new JValue("data:image/png;base64,iVBORw0KGgo=")
            };
            var ids = new[] { "go", "failed", "shot", null };
            var succeeded = new[] { true, false, true };

            JToken Resolve(string json, out object unresolved) =>
                BatchFunctions.ResolveReferences(JToken.Parse(json), ids, 3, results, succeeded, out unresolved);

            var resolved = Resolve(
                "{\"a\":\"$go.instanceId\",\"b\":\"$0.nested.a\",\"c\":\"$0.data.instanceId\",\"d\":[\"$go.list[1].b\"]," +
                "\"price\":\"$0.99\",\"dollars\":\"$$\",\"escaped\":\"$$go.instanceId\",\"raw\":\"$$\\\"\\\"\\\"x\\\"\\\"\\\"\"}",
                out var none);
            Assert.IsNull(none);
            Assert.AreEqual("12", resolved["a"].Value<string>());
            Assert.AreEqual(JTokenType.Integer, resolved["b"].Type, "a number stays a number");
            Assert.AreEqual("12", resolved["c"].Value<string>(), "the whole envelope is read when data has no such path");
            Assert.AreEqual(3, resolved["d"][0].Value<int>());
            Assert.AreEqual("$0.99", resolved["price"].Value<string>());
            Assert.AreEqual("$$", resolved["dollars"].Value<string>());
            Assert.AreEqual("$go.instanceId", resolved["escaped"].Value<string>());
            Assert.AreEqual("$$\"\"\"x\"\"\"", resolved["raw"].Value<string>());

            foreach (var bad in new[] { "$go.missing", "$go.empty","$failed.code", "$shot.x", "$3.instanceId", "$nobody.x", "$go.list[*].b", "$go.a[" })
            {
                Assert.IsNull(Resolve("{\"v\":\"" + bad + "\"}", out var unresolved), bad);
                Assert.IsNotNull(unresolved, bad + " must report an unresolved reference");
            }
        }

        [UnityTest]
        public IEnumerator Batch_ReferencesAnEarlierResultById()
        {
            var batch = UnityCliEditorFunctions.Batch(
                "[{\"id\":\"go\",\"command\":\"create_game_object\",\"params\":{\"name\":\"kw-batch-ref\"}}," +
                "{\"command\":\"add_component\",\"params\":{\"target\":\"$go.instanceId\",\"component_type\":\"BoxCollider\"}}," +
                "{\"command\":\"create_game_object\",\"params\":{\"name\":\"$0.99\"}}]");
            try
            {
                yield return WaitForTask(batch);

                Assert.IsTrue(JObject.Parse(JsonConvert.SerializeObject(batch.Result))["success"].Value<bool>(),
                    JsonConvert.SerializeObject(batch.Result));
                Assert.IsNotNull(GameObject.Find("kw-batch-ref").GetComponent<BoxCollider>());
                Assert.IsNotNull(GameObject.Find("$0.99"), "a price is text, not a reference");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(GameObject.Find("kw-batch-ref"));
                UnityEngine.Object.DestroyImmediate(GameObject.Find("$0.99"));
            }
        }

        [UnityTest]
        public IEnumerator Batch_AMissingPathFailsThatOperationAndStops()
        {
            var batch = UnityCliEditorFunctions.Batch(
                "[{\"id\":\"state\",\"command\":\"get_editor_state\",\"params\":{}}," +
                "{\"command\":\"create_game_object\",\"params\":{\"name\":\"$state.noSuchField\"}}," +
                "{\"command\":\"get_editor_state\",\"params\":{}}]");
            yield return WaitForTask(batch);

            var envelope = JObject.Parse(JsonConvert.SerializeObject(batch.Result));
            Assert.AreEqual("BATCH_ABORTED", envelope["code"].Value<string>());
            Assert.AreEqual(2, envelope["data"]["count"].Value<int>());
            Assert.AreEqual("UNRESOLVED_REFERENCE", envelope["data"]["results"][1]["result"]["code"].Value<string>());
        }

        [UnityTest]
        public IEnumerator Batch_RefusesADuplicateIdBeforeRunning()
        {
            var batch = UnityCliEditorFunctions.Batch(
                "[{\"id\":\"a\",\"command\":\"create_game_object\",\"params\":{\"name\":\"kw-batch-dup\"}}," +
                "{\"id\":\"a\",\"command\":\"get_editor_state\",\"params\":{}}]");
            yield return WaitForTask(batch);

            Assert.AreEqual("DUPLICATE_ID", JObject.Parse(JsonConvert.SerializeObject(batch.Result))["code"].Value<string>());
            Assert.IsNull(GameObject.Find("kw-batch-dup"), "nothing ran");
        }

        [UnityTest]
        public IEnumerator BatchExecute_PassesDollarTextThroughUntouched()
        {
            var batch = BatchFunctions.BatchExecute(
                "[{\"name\":\"get_editor_state\",\"params\":{}},{\"name\":\"create_game_object\",\"params\":{\"name\":\"$0.instanceId\"}}]");
            try
            {
                yield return WaitForTask(batch);
                Assert.IsNotNull(GameObject.Find("$0.instanceId"), "batch_execute has shipped without references");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(GameObject.Find("$0.instanceId"));
            }
        }

        [Test]
        public void BatchBudget_GrowsWithItsLongRunningCommands()
        {
            Assert.AreEqual(170, ToolRegistry.BatchBudgetSeconds(new[] { "get_hierarchy", "set_transform" }, 170));
            Assert.AreEqual(170 + 900, ToolRegistry.BatchBudgetSeconds(new[] { "bake_nav_mesh", "get_hierarchy" }, 170));
            Assert.AreEqual(1800, ToolRegistry.BatchBudgetSeconds(new[] { "build_player", "bake_nav_mesh" }, 170),
                "capped under the broker's hold deadline");

            var call = new System.Collections.Generic.Dictionary<string, object>
            {
                ["name"] = "batch_execute",
                ["arguments"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["commands"] = "[{\"name\":\"bake_nav_mesh\",\"params\":{}}]"
                }
            };
            Assert.AreEqual(180 + 900, ToolRegistry.TimeoutSecondsForRequest("tools/call", call),
                "the transport must wait as long as the batch may run");
        }

        [Test]
        public void EveryResultShapeComesBackAsAnEnvelope()
        {
            Assert.AreEqual(true, JObject.Parse(FunctionInvoker.SerializeResult(new { answer = 42 }))["success"].Value<bool>());
            Assert.AreEqual(42, JObject.Parse(FunctionInvoker.SerializeResult(new { answer = 42 }))["data"]["answer"].Value<int>());
            Assert.AreEqual(true, JObject.Parse(FunctionInvoker.SerializeResult(new JArray(1, 2)))["success"].Value<bool>());

            var error = JObject.Parse(FunctionInvoker.SerializeResult(KitWright.Editor.Tools.Helpers.Response.Error("NOPE")));
            Assert.IsFalse(error["success"].Value<bool>(), "an envelope is passed through, not wrapped");
            Assert.AreEqual("NOPE", error["code"].Value<string>());

            var voidTask = FunctionInvoker.NormalizeResultAsync(FinishesWithNothing(), typeof(Task));
            Assert.IsTrue(voidTask.IsCompleted);
            Assert.IsTrue(JObject.Parse(voidTask.Result)["success"].Value<bool>(),
                "an async Task tool that finished is a success, not an envelope-less {}");
        }

#pragma warning disable CS1998
        private static async Task FinishesWithNothing()
        {
        }
#pragma warning restore CS1998

        private static IEnumerator WaitForTask(Task task, float timeoutSeconds = 10f)
        {
            var start = Time.realtimeSinceStartup;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup - start > timeoutSeconds)
                    throw new TimeoutException("Timed out waiting for the batch to finish.");

                yield return null;
            }

            if (task.IsFaulted)
                throw task.Exception;
        }
    }
}
