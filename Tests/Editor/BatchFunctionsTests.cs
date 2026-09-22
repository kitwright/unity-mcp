// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
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
