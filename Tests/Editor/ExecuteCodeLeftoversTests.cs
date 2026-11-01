// Copyright (C) KitWright. All rights reserved.

using System.Linq;
using System.Reflection;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;

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
    }
}
