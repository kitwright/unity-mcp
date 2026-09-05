// Copyright (C) KitWright. Licensed under MIT.

using System.Collections.Generic;
using System.Linq;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Tools;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    /// A provider that keeps compiling without its package registers tools that can only answer
    /// "<package> required". Neither the exported surface nor the Tool Exposure switches may offer
    /// those, or a profile turns on tools that can only fail.
    public sealed class ToolExposurePackageGateTests
    {
        private static readonly string[] AllToolNames =
            ToolSchemaBuilder.BuildAll().Select(tool => tool.name).Where(name => !string.IsNullOrEmpty(name)).ToArray();

        private static readonly string[] NothingInstalled = new string[0];

        [Test]
        public void EveryAddressableToolIsLockedWhenThePackageIsAbsent()
        {
            var missing = ToolPackageGate.Compute(AllToolNames, NothingInstalled);

            var addressableTools = AllToolNames.Where(name => name.Contains("addressable")).ToArray();
            Assert.IsNotEmpty(addressableTools, "the Addressable provider should register tools either way");

            foreach (var tool in addressableTools)
                Assert.AreEqual("com.unity.addressables", Required(missing, tool), tool);
        }

        [Test]
        public void NothingIsLockedOnceThePackageIsInstalled()
        {
            var installed = new[] { "com.unity.addressables", "com.unity.render-pipelines.universal" };
            var missing = ToolPackageGate.Compute(AllToolNames, installed);

            Assert.IsEmpty(missing.Keys.Where(tool => tool.Contains("addressable") || tool.Contains("volume")));
        }

        // The Memory Profiler provider is mostly built-in: only the calls that go through the
        // package's crawler or window carry the requirement, so the gate has to read it per method.
        [Test]
        public void OnlyThePackageBoundMemoryToolsAreLocked()
        {
            var missing = ToolPackageGate.Compute(AllToolNames, NothingInstalled);

            Assert.AreEqual("com.unity.memoryprofiler", Required(missing, "memory_query_top_objects"));
            Assert.AreEqual("com.unity.memoryprofiler", Required(missing, "memory_query_references"));
            Assert.AreEqual("com.unity.memoryprofiler", Required(missing, "memory_open_snapshot_in_profiler"));

            Assert.IsFalse(missing.ContainsKey("memory_take_full_snapshot"));
            Assert.IsFalse(missing.ContainsKey("memory_list_full_snapshots"));
        }

        [Test]
        public void AToolWithoutAPackageRequirementIsNeverLocked()
        {
            var missing = ToolPackageGate.Compute(AllToolNames, NothingInstalled);

            Assert.IsFalse(missing.ContainsKey("get_hierarchy"));
            Assert.IsFalse(missing.ContainsKey("get_scene_info"));
        }

        // Full and Extended are "everything except a niche list", so they are where an unavailable
        // tool slips back in after the per-tool switches have already been fixed.
        [TestCase("minimal")]
        [TestCase("core")]
        [TestCase("extended")]
        [TestCase("full")]
        public void NoProfileDefaultsToAToolThatCannotRun(string profile)
        {
            var defaults = MCPToolExportPolicy
                .DefaultToolsFor(MCPToolExportPolicy.Parse(profile), AllToolNames).ToArray();

            CollectionAssert.IsEmpty(defaults.Where(ToolPackageGate.IsUnavailable).ToArray(),
                profile + " offers tools whose package is not installed");
        }

        [Test]
        public void AProfileSavedBeforeThePackageWentMissingStillCannotExportIt()
        {
            var unavailable = AllToolNames.FirstOrDefault(ToolPackageGate.IsUnavailable);
            if (unavailable == null)
                Assert.Ignore("every optional package is installed here");

            Assert.IsFalse(MCPToolExportPolicy.IsToolAllowed(
                unavailable, MCPToolExportPolicy.Parse("full"), profileConfigured: true,
                profileTools: new[] { unavailable }));
        }

        private static string Required(IDictionary<string, string> missing, string tool)
        {
            Assert.Contains(tool, AllToolNames, tool + " should be registered");
            return missing.TryGetValue(tool, out var package) ? package : null;
        }
    }
}
