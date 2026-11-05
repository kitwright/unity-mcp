// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    public sealed class ExecuteCodeLeftoversTests
    {
        private static ParameterInfo Parameter(string method, string name) =>
            typeof(ScriptExecutionFunctions).GetMethod(method).GetParameters().FirstOrDefault(parameter => parameter.Name == name);

        // A replay of a read-only snippet otherwise always refreshes the AssetDatabase, which can
        // reload the domain on a shared editor, and its loops keep the 20 s budget.
        [TestCase("skip_refresh")]
        [TestCase("timeout_seconds")]
        public void ReplayTakesTheRefreshAndTimeoutParametersExecuteCodeHas(string name)
        {
            var execute = Parameter("ExecuteCode", name);
            var replay = Parameter("ReplayExecuteCode", name);

            Assert.IsNotNull(replay, name);
            Assert.AreEqual(execute.ParameterType, replay.ParameterType, name);
            Assert.AreEqual(execute.DefaultValue, replay.DefaultValue, name);
            Assert.IsFalse(replay.GetCustomAttribute<ToolParamAttribute>().Required, name);
        }

        // The wrapper's own "return null;" after a body ending in return is unreachable (CS0162); that line is
        // not the caller's, so only the snippet's own warning may come back.
        [UnityTest]
        public IEnumerator Warnings_PointAtTheSnippetLine_AndOnlyTheSnippets()
        {
            var task = ScriptExecutionFunctions.ExecuteCode("int unused = 1;\nreturn 2;", skip_refresh: true);
            yield return WaitForTask(task);

            var data = JObject.FromObject(task.Result)["data"];
            var warning = data["warnings"].Single();
            Assert.AreEqual("CS0219", (string)warning["code"]);
            Assert.AreEqual(1, (int)warning["line"]);
        }

        [UnityTest]
        public IEnumerator Warnings_AreLeftOutOfACleanSnippetsAnswer()
        {
            var task = ScriptExecutionFunctions.ExecuteCode("return 2;", skip_refresh: true);
            yield return WaitForTask(task);

            Assert.IsNull(JObject.FromObject(task.Result)["data"]["warnings"]);
        }

        private static IEnumerator WaitForTask(Task task, float timeoutSeconds = 30f)
        {
            var start = Time.realtimeSinceStartup;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup - start > timeoutSeconds)
                    throw new TimeoutException("Timed out waiting for execute_code.");

                yield return null;
            }

            if (task.IsFaulted)
                throw task.Exception;
        }
    }
}
