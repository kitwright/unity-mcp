// Copyright (C) KitWright. All rights reserved.

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Tools;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    public sealed class ToolTimeoutBudgetTests
    {
        // The advertised numbers only mean something if the per-call ceiling can honour them:
        // CompilationFunctions.cs:32 caps timeout_seconds at 120, and the call has to come back
        // before HttpMCPTransport.cs gives up at 180s.
        [Test]
        public void ToolCallCeiling_HonoursTheLongestReachableToolTimeoutAndBeatsTheTransportCap()
        {
            Assert.GreaterOrEqual(MCPServerService.ToolCallTimeoutMs, 120_000,
                "wait_for_compilation advertises up to 120s, so a smaller ceiling makes that a lie.");
            Assert.Less(MCPServerService.ToolCallTimeoutMs, 180_000,
                "Our timeout must fire before the transport's, or the client gets the bare one.");
        }

        [Test]
        public void RequestBudget_OnlyWidensForToolsCallAndNeverBelowTheFallback()
        {
            var call = new Dictionary<string, object> { { "name", "build_player" } };

            Assert.Greater(ToolRegistry.TimeoutSecondsForRequest("tools/call", call), 180);
            Assert.AreEqual(180, ToolRegistry.TimeoutSecondsForRequest("tools/list", call));
            Assert.AreEqual(180, ToolRegistry.TimeoutSecondsForRequest("tools/call", null));

            // The broker path passes its own, larger fallback; a short tool must not shrink it.
            Assert.AreEqual(300, ToolRegistry.TimeoutSecondsForRequest(
                "tools/call", new Dictionary<string, object> { { "name", "get_hierarchy" } }, 300));
        }

        private static Dictionary<string, object> ExecuteCodeCall(object timeoutSeconds)
        {
            var arguments = new Dictionary<string, object> { { "code", "return 1;" } };
            if (timeoutSeconds != null)
                arguments["timeout_seconds"] = timeoutSeconds;

            return new Dictionary<string, object> { { "name", "execute_code" }, { "arguments", arguments } };
        }

        [Test]
        public void ExecuteCodeBudget_FollowsTimeoutSecondsAndOnlyEverWidensTheCeiling()
        {
            int Budget(object timeout, int fallback = 170) =>
                ToolRegistry.TimeoutSecondsForRequest("tools/call", ExecuteCodeCall(timeout), fallback);

            Assert.AreEqual(170, Budget(null), "no timeout_seconds keeps the ordinary ceiling");
            Assert.AreEqual(170, Budget(0));
            Assert.AreEqual(170, Budget(-5));
            Assert.AreEqual(170, Budget(30), "a small value must not narrow the ceiling");
            Assert.AreEqual(170, Budget("soon"), "an unreadable value is no request");
            Assert.AreEqual(300, Budget(10, fallback: 300), "the broker's larger fallback is kept");

            foreach (var spelling in new object[] { 300, 300L, 300.0, "300" })
                Assert.AreEqual(300 + 120, Budget(spelling), "a client may send the number as " + spelling.GetType().Name);

            Assert.AreEqual(1800, Budget(99999), "capped at a batch's ceiling, under the broker's hold deadline");
        }

        [Test]
        public void ExecuteCodeBudget_ReadsTheShapeTheTransportsActuallyParse()
        {
            const string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"execute_code\"," +
                                "\"arguments\":{\"code\":\"return 1;\",\"timeout_seconds\":300}}}";
            var root = (Dictionary<string, object>)JsonCodec.Deserialize(body);
            var parameters = (Dictionary<string, object>)root["params"];

            Assert.AreEqual(300 + 120, ToolRegistry.TimeoutSecondsForRequest("tools/call", parameters));
        }

        // 22 s of real wall clock, because 20 s is the budget being widened.
        [UnityTest]
        public IEnumerator ExecuteCode_TimeoutSecondsKeepsASnippetLoopAliveBeyondTheDefaultBudget()
        {
            const string snippet = "var sw = System.Diagnostics.Stopwatch.StartNew(); " +
                                   "while (sw.Elapsed.TotalSeconds < 22) { } return \"finished\";";
            var wrapped = Tools.Builtins.ScriptExecutionFunctions.BuildCodeForCompilation(snippet, "T", false, out _);
            StringAssert.Contains("LoopBudgetGuard.Check(", Tools.Scripting.LoopGuardInjector.Inject(wrapped),
                "without the injected guard this test would pass whatever the budget is");

            var task = Tools.Builtins.ScriptExecutionFunctions.ExecuteCode(snippet, false, true, 40);
            while (!task.IsCompleted)
                yield return null;

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(task.Result);
            StringAssert.Contains("finished", json);
            StringAssert.DoesNotContain("budget", json);
        }

        [Test]
        public void ExecuteCodeLoopBudget_StartsAtTheDefaultAndOnlyEverWidens()
        {
            Assert.AreEqual(20, Tools.Builtins.ScriptExecutionFunctions.LoopBudgetFor(0).TotalSeconds);
            Assert.AreEqual(20, Tools.Builtins.ScriptExecutionFunctions.LoopBudgetFor(5).TotalSeconds,
                "timeout_seconds below the default must not shorten a snippet's loops");
            Assert.AreEqual(300, Tools.Builtins.ScriptExecutionFunctions.LoopBudgetFor(300).TotalSeconds);
            Assert.AreEqual(ToolRegistry.MaxExecuteCodeTimeoutSeconds,
                Tools.Builtins.ScriptExecutionFunctions.LoopBudgetFor(99999).TotalSeconds);
        }

        [Test]
        public void ToolTimeoutMessage_SaysTheCallMayStillFinishAndPointsExecuteCodeAtItsOptions()
        {
            var plain = MCPServerService.ToolTimeoutMessage("get_hierarchy", 170_000);
            var executeCode = MCPServerService.ToolTimeoutMessage("execute_code", 170_000);

            StringAssert.Contains("timed out after 170000ms", plain);
            StringAssert.Contains("may still finish", plain);
            StringAssert.DoesNotContain("timeout_seconds", plain);
            StringAssert.Contains("timeout_seconds", executeCode);
            StringAssert.Contains("get_execute_code_history", executeCode);
        }

        [Test]
        public void ToolTimeoutMessage_OnlyNamesTheHistoryToolWhenTheProfileExposesIt()
        {
            var hidden = MCPServerService.ToolTimeoutMessage("execute_code", 170_000, historyExposed: false);

            StringAssert.Contains("tool_profile", hidden);
            StringAssert.Contains("timeout_seconds", hidden);
        }

        [Test]
        public void ExecuteCode_TakesTimeoutSecondsAsAnOptInParameter()
        {
            var parameter = typeof(Tools.Builtins.ScriptExecutionFunctions).GetMethod("ExecuteCode")
                .GetParameters().SingleOrDefault(p => p.Name == "timeout_seconds");

            Assert.NotNull(parameter);
            Assert.AreEqual(0, parameter.DefaultValue, "a plain execute_code must keep its ordinary limits");
        }

        // CoplayDev/unity-mcp #1130.
        // ponytail: hand-kept list; alternative is finding the long blocking calls in source.
        [Test]
        public void EveryToolThatBlocksTheMainThreadForMinutes_DeclaresABudget()
        {
            foreach (var tool in new[] { "build_player", "switch_build_target", "bake_nav_mesh" })
            {
                var budget = ToolRegistry.TimeoutSecondsForRequest(
                    "tools/call", new Dictionary<string, object> { { "name", tool } });

                // Against the fallback, not the transport cap, or a tool with no attribute passes.
                Assert.Greater(budget, ToolRegistry.DefaultToolTimeoutSeconds,
                    tool + " blocks the editor past the default ceiling, so it needs [LongRunningTool].");
            }
        }
    }
}
