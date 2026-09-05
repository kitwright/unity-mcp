// Copyright (C) KitWright. Licensed under MIT.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using KitWright.Editor.MCP.Server;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public class ClientConfigTargetTests
    {
        private static string ProjectRoot =>
            Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;

        [Test]
        public void ProjectScopedTargetsWriteInsideTheProject()
        {
            var scoped = ClientConfigPanel.GetAllTargets().Where(t => t.ProjectScoped).ToArray();

            Assert.IsNotEmpty(scoped, "Claude Code and Cursor should offer project-scoped targets.");

            foreach (var target in scoped)
            {
                Assert.IsTrue(
                    target.ConfigPath.Replace('\\', '/').StartsWith(ProjectRoot.Replace('\\', '/')),
                    $"{target.Name} must write inside the project, got {target.ConfigPath}.");
            }
        }

        // Scope selection is a display-time choice, so a target must resolve to a real path in
        // whichever scope it is asked for, and fall back when it only supports one.
        [Test]
        public void EveryTargetResolvesAPathInBothScopes()
        {
            foreach (var target in ClientConfigPanel.GetAllTargets())
            {
                Assert.IsTrue(target.Supports(true) || target.Supports(false),
                    $"{target.Name} has no config path at all.");
                Assert.IsNotEmpty(target.ConfigPath, $"{target.Name} resolved to an empty path.");
            }
        }

        [Test]
        public void EveryTargetNameIsUnique()
        {
            var names = ClientConfigPanel.GetAllTargets().Select(t => t.Name).ToArray();

            Assert.AreEqual(names.Length, names.Distinct().Count(),
                "The dropdown selects a target by name, so duplicates would be unreachable.");
        }

        // The sweep used to visit only the project-scoped file, so a client configured in the
        // global file kept pointing at a port the server had already left. Sweeping both is only
        // safe because a config without our entry must come back untouched.
        [Test]
        public void RewriteJson_RepairsTheGlobalFileAndLeavesAForeignOneAlone()
        {
            var dir = Path.Combine(Path.GetTempPath(), "KitWrightConfigRewrite_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var foreignJson = "{\"mcpServers\":{\"ai-game-developer\":{\"url\":\"http://localhost:23275/\"}}}";
                var projectPath = Path.Combine(dir, "project.json");
                var globalPath = Path.Combine(dir, "global.json");
                File.WriteAllText(projectPath, foreignJson);
                File.WriteAllText(globalPath,
                    "{\"mcpServers\":{\"kitwright\":{\"type\":\"http\",\"url\":\"http://127.0.0.1:8766/\"}}}");

                var url = "http://127.0.0.1:8765/";
                Assert.IsFalse(
                    MCPClientConfigAutoRewrite.RewriteJson(projectPath, "mcpServers", "kitwright", url),
                    "A config without our entry must not be reported as rewritten.");
                Assert.AreEqual(foreignJson, File.ReadAllText(projectPath),
                    "A config without our entry must not be modified.");

                Assert.IsTrue(
                    MCPClientConfigAutoRewrite.RewriteJson(globalPath, "mcpServers", "kitwright", url),
                    "The global file holds the stale entry and must be repaired.");
                StringAssert.Contains("8765", File.ReadAllText(globalPath));

                // One global file, one entry name, several projects: an entry already pinned to a
                // sibling project belongs to that project's editor.
                var foreignPinned =
                    "{\"mcpServers\":{\"kitwright\":{\"type\":\"http\",\"url\":\"http://127.0.0.1:8766/p/deadbeef/\"}}}";
                File.WriteAllText(globalPath, foreignPinned);

                Assert.IsFalse(
                    MCPClientConfigAutoRewrite.RewriteJson(globalPath, "mcpServers", "kitwright", url, true),
                    "An entry pinned to another project must be left alone in the global file.");
                Assert.AreEqual(foreignPinned, File.ReadAllText(globalPath));

                Assert.IsTrue(
                    MCPClientConfigAutoRewrite.RewriteJson(globalPath, "mcpServers", "kitwright", url),
                    "The project-scoped sweep still repairs a stale pin, since that file is ours.");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Test]
        public void ConfigProblem_NamesAUrlThatCannotReachThisServer()
        {
            const string live = "http://127.0.0.1:8766/p/e39cb4bc/";

            Assert.IsNull(ClientConfigPanel.DescribeConfigProblem(
                "{\"mcpServers\":{\"kitwright\":{\"url\":\"" + live + "\"}}}", live));

            Assert.IsNotNull(ClientConfigPanel.DescribeConfigProblem(
                "{\"mcpServers\":{\"kitwright\":{\"url\":\"http://127.0.0.1:8765/p/e39cb4bc/\"}}}", live),
                "a stale port still reads as configured, but another editor answers there");

            Assert.IsNotNull(ClientConfigPanel.DescribeConfigProblem(
                "{\"mcpServers\":{\"kitwright\":{\"url\":\"http://127.0.0.1:8766/\"}}}", live),
                "a pinless URL is served by whichever project owns the port");
        }

        [Test]
        public void ConfigStatus_CountsTheEntryNotTheFile()
        {
            Assert.IsFalse(ClientConfigPanel.ConfigHasOurEntry(
                "{\"mcpServers\":{\"ai-game-developer\":{\"url\":\"http://localhost:23275/p/e39cb4bc\"}}}"),
                "a file full of other MCP servers is not this plugin being configured");

            Assert.IsFalse(ClientConfigPanel.ConfigHasOurEntry(
                "{\"mcpServers\":{\"other\":{\"args\":[\"Library/PackageCache/com.kitwright.unity.mcp/x.js\"]}}}"),
                "the name appearing inside some other value is not an entry");

            Assert.IsFalse(ClientConfigPanel.ConfigHasOurEntry(
                "{\"mcpServers\":{\"kitwright-e39cb4bc\":{\"url\":\"http://127.0.0.1:8765/\"}}}"),
                "the 0.6.x pinned name is stale, and Configure is what replaces it");

            Assert.IsTrue(ClientConfigPanel.ConfigHasOurEntry(
                "{\"mcpServers\":{\"kitwright\":{\"url\":\"http://127.0.0.1:8766/p/e39cb4bc/\"}}}"));
            Assert.IsTrue(ClientConfigPanel.ConfigHasOurEntry(
                "[mcp_servers.kitwright]\nurl = \"http://127.0.0.1:8766/p/e39cb4bc/\""));

            Assert.IsFalse(ClientConfigPanel.ConfigHasOurEntry(null));
            Assert.IsFalse(ClientConfigPanel.ConfigHasOurEntry(string.Empty));
        }

        [Test]
        public void MergeJsonConfig_KeepsForeignServers_AndRefusesCorruptFiles()
        {
            var entry = new Dictionary<string, object> { ["url"] = "http://127.0.0.1:8975/" };

            var merged = ClientConfigPanel.MergeJsonConfig(
                "{\"mcpServers\":{\"claude-mem\":{\"command\":\"node\"}}}",
                "mcpServers", "kitwright", entry, null, "cfg.json");
            StringAssert.Contains("claude-mem", merged, "merging must not drop somebody else's server");
            StringAssert.Contains("8975", merged);

            // The 0-byte file Antigravity chokes on: nothing to lose, so rebuild it.
            var rebuilt = ClientConfigPanel.MergeJsonConfig(
                string.Empty, "mcpServers", "kitwright", entry, null, "cfg.json");
            StringAssert.Contains("kitwright", rebuilt);
            Assert.IsFalse(string.IsNullOrWhiteSpace(rebuilt), "a write must never produce an empty file");

            // Truncated but not empty: servers we cannot see are still in there. The lenient reader
            // returns a partial dictionary for these rather than null, so a null check alone misses
            // them and the servers past the cut get dropped on write.
            foreach (var corrupt in new[]
                     {
                         "{\"mcpServers\":{\"claude-mem\"",
                         "{\"mcpServers\":{\"a\":{\"url\":\"x\"},",
                         "not json at all",
                     })
            {
                Assert.Throws<IOException>(() => ClientConfigPanel.MergeJsonConfig(
                    corrupt, "mcpServers", "kitwright", entry, null, "cfg.json"),
                    $"a config we cannot parse must not be overwritten: {corrupt}");
            }
        }

        [Test]
        public void ConfigProblem_TellsAStalePortApartFromASiblingProject()
        {
            const string live = "http://127.0.0.1:8975/p/d156b7e9/";

            StringAssert.Contains("re-run Configure", ClientConfigPanel.DescribeConfigProblem(
                "{\"mcpServers\":{\"kitwright\":{\"url\":\"http://127.0.0.1:8766/p/d156b7e9/\"}}}", live),
                "our own entry on a dead port is exactly what Configure repairs");

            StringAssert.Contains("another project", ClientConfigPanel.DescribeConfigProblem(
                "{\"mcpServers\":{\"kitwright\":{\"url\":\"http://127.0.0.1:8766/p/e39cb4bc/\"}}}", live),
                "re-running Configure here would steal the sibling project's entry");
        }
    }
}
