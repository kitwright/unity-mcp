// Copyright (C) KitWright. All rights reserved.

using System.IO;
using System.Linq;
using KitWright.Editor.MCP.Server;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    // The JSON files are the interface — nothing in the package reads them back, they exist for a
    // person or an agent pointing an MCP client at the right port. So the assertions read the
    // directory the same way that reader would.
    public sealed class MCPInstanceRegistryTests
    {
        private const string ProjectA = "C:/Projects/Alpha";
        private const string ProjectB = "C:/Projects/Beta";
        private const int DeadPid = int.MaxValue;

        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "KitWrightInstanceRegistryTests", Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
            MCPInstanceRegistry.RootOverride = _root;
        }

        [TearDown]
        public void TearDown()
        {
            MCPInstanceRegistry.RootOverride = null;
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        private JObject[] Entries() =>
            Directory.GetFiles(_root, "*.json").Select(f => JObject.Parse(File.ReadAllText(f))).ToArray();

        [Test]
        public void PublishWritesThePortAndProjectForThisEditor()
        {
            MCPInstanceRegistry.Publish(8765, ProjectA, "Alpha", "identity-a");

            var entry = Entries().Single();

            Assert.AreEqual(8765, (int)entry["port"]);
            Assert.AreEqual(ProjectA, (string)entry["projectPath"]);
            Assert.AreEqual("Alpha", (string)entry["projectName"]);
            Assert.AreEqual("identity-a", (string)entry["projectIdentity"]);
            Assert.Greater((int)entry["pid"], 0);
        }

        [Test]
        public void TwoProjectsGetSeparateEntries()
        {
            MCPInstanceRegistry.Publish(8765, ProjectA, "Alpha", "identity-a");
            MCPInstanceRegistry.Publish(8766, ProjectB, "Beta", "identity-b");

            var ports = Entries().Select(e => (int)e["port"]).OrderBy(p => p).ToArray();

            Assert.AreEqual(new[] { 8765, 8766 }, ports);
        }

        [Test]
        public void RepublishingTheSameProjectOverwritesRatherThanAccumulates()
        {
            MCPInstanceRegistry.Publish(8765, ProjectA, "Alpha", "identity-a");
            MCPInstanceRegistry.Publish(8770, ProjectA, "Alpha", "identity-a");

            Assert.AreEqual(8770, (int)Entries().Single()["port"]);
        }

        [Test]
        public void RemoveDeletesOnlyThatProjectsEntry()
        {
            MCPInstanceRegistry.Publish(8765, ProjectA, "Alpha", "identity-a");
            MCPInstanceRegistry.Publish(8766, ProjectB, "Beta", "identity-b");

            MCPInstanceRegistry.Remove(ProjectA);

            Assert.AreEqual(8766, (int)Entries().Single()["port"]);
        }

        // A crashed editor never runs Remove, so the file it left behind must not survive the
        // next write — otherwise the registry keeps naming a port nothing is listening on.
        [Test]
        public void EntryFromADeadProcessIsPrunedOnNextPublish()
        {
            var stale = Path.Combine(_root, "deadbeef.json");
            File.WriteAllText(stale,
                $"{{\"port\":9999,\"projectPath\":\"C:/Projects/Ghost\",\"projectName\":\"Ghost\",\"pid\":{DeadPid}}}");

            MCPInstanceRegistry.Publish(8765, ProjectA, "Alpha", "identity-a");

            Assert.IsFalse(File.Exists(stale), "Stale entry should be pruned.");
            Assert.AreEqual(8765, (int)Entries().Single()["port"]);
        }

        [Test]
        public void UnparsableFilesAreLeftAlone()
        {
            var foreign = Path.Combine(_root, "not-ours.json");
            File.WriteAllText(foreign, "this is not json");

            MCPInstanceRegistry.Publish(8765, ProjectA, "Alpha", "identity-a");

            Assert.IsTrue(File.Exists(foreign), "Files the registry cannot parse are not its own to delete.");
        }

        [Test]
        public void PublishCreatesTheRegistryDirectory()
        {
            var missing = Path.Combine(_root, "does-not-exist");
            MCPInstanceRegistry.RootOverride = missing;

            MCPInstanceRegistry.Publish(8765, ProjectA, "Alpha", "identity-a");

            Assert.AreEqual(1, Directory.GetFiles(missing, "*.json").Length);
        }
    }
}
