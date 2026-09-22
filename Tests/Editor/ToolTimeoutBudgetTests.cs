// Copyright (C) KitWright. All rights reserved.

using System.Collections.Generic;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Tools;
using NUnit.Framework;

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

        // CoplayDev/unity-mcp #1130.
        // ponytail: hand-kept list; alternative is parsing NoThrottleLease durations out of source.
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
