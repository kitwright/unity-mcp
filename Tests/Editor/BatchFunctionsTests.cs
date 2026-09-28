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
