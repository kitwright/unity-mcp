// Copyright (C) KitWright. Licensed under MIT.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KitWright.Editor.MCP.Server;
using Newtonsoft.Json.Linq;
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

        // The codec reads no comments, so writing back the tree it parsed dropped every key after one.
        [Test]
        public void RewriteJson_ChangesOnlyTheUrlAndKeepsCommentsAndLaterKeys()
        {
            var dir = Path.Combine(Path.GetTempPath(), "KitWrightConfigRewrite_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path,
                    "{\n  \"mcpServers\": { \"kitwright\": { \"type\": \"http\", \"url\": \"http://127.0.0.1:8766/\" } },\n" +
                    "  // keep this note\n  \"theme\": \"dark\",\n  \"other\": { \"url\": \"http://example.test/\" }\n}\n");

                Assert.IsTrue(MCPClientConfigAutoRewrite.RewriteJson(path, "mcpServers", "kitwright", "http://127.0.0.1:8765/"));

                var rewritten = File.ReadAllText(path);
                StringAssert.Contains("\"url\": \"http://127.0.0.1:8765/\"", rewritten);
                StringAssert.DoesNotContain("8766", rewritten);
                StringAssert.Contains("// keep this note", rewritten);
                StringAssert.Contains("\"theme\": \"dark\"", rewritten);
                StringAssert.Contains("http://example.test/", rewritten);
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

        private static void InTempDir(System.Action<string> body)
        {
            var dir = Path.Combine(Path.GetTempPath(), "KitWrightClientConfig_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                body(dir);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static Dictionary<string, object> HttpEntry(string url)
            => new Dictionary<string, object> { ["url"] = url, ["type"] = "http" };

        [Test]
        public void ReadExistingConfig_MissingFileIsNullNotAnError()
        {
            InTempDir(dir =>
            {
                Assert.IsNull(ClientConfigPanel.ReadExistingConfig(Path.Combine(dir, "nope.json")));
                Assert.IsNull(ClientConfigPanel.ReadExistingConfig(Path.Combine(dir, "no-dir", "nope.json")));
                Assert.IsNull(ClientConfigPanel.ReadExistingConfig(string.Empty));
            });
        }

        [Test]
        public void ConfigureJsonFile_AbortsWithoutWritingWhenTheFileCannotBeRead()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("Sharing violations are only enforced on Windows.");

            InTempDir(dir =>
            {
                var path = Path.Combine(dir, ".claude.json");
                const string original = "{\"history\":[1,2,3],\"mcpServers\":{\"claude-mem\":{\"command\":\"node\"}}}";
                File.WriteAllText(path, original);

                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Assert.Throws<IOException>(() => ClientConfigPanel.ConfigureJsonFile(
                        path, "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null),
                        "a locked file is not an empty one: writing now would drop everything in it");
                }

                Assert.AreEqual(original, File.ReadAllText(path));
                Assert.IsFalse(File.Exists(path + ".bak"));
            });
        }

        [Test]
        public void ConfigureJsonFile_KeepsTheOverwrittenFileAsBak()
        {
            InTempDir(dir =>
            {
                var path = Path.Combine(dir, ".claude.json");
                const string original = "{\"history\":[1,2,3],\"mcpServers\":{\"claude-mem\":{\"command\":\"node\"}}}";
                File.WriteAllText(path, original);

                ClientConfigPanel.ConfigureJsonFile(
                    path, "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null);

                Assert.AreEqual(original, File.ReadAllText(path + ".bak"));
                var written = File.ReadAllText(path);
                StringAssert.Contains("8975", written);
                StringAssert.Contains("claude-mem", written);
                StringAssert.Contains("history", written);

                var fresh = Path.Combine(dir, "fresh.json");
                ClientConfigPanel.ConfigureJsonFile(
                    fresh, "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null);
                Assert.IsFalse(File.Exists(fresh + ".bak"), "there is nothing to back up for a new file");
            });
        }

        [Test]
        public void MergeJsonConfig_KeepsUserKeysInsideOurOwnEntry()
        {
            var existing =
                "{\"mcpServers\":{\"kitwright\":{\"type\":\"http\",\"url\":\"http://127.0.0.1:8766/\"," +
                "\"alwaysAllow\":[\"get_scene_info\"],\"timeout\":60,\"disabled\":true}}}";
            var entry = HttpEntry("http://127.0.0.1:8975/");
            entry["disabled"] = false;

            var merged = ClientConfigPanel.MergeJsonConfig(existing, "mcpServers", "kitwright", entry, null, "cfg.json");

            var server = (JObject)JObject.Parse(merged)["mcpServers"]["kitwright"];
            Assert.AreEqual("http://127.0.0.1:8975/", (string)server["url"]);
            Assert.AreEqual("get_scene_info", (string)server["alwaysAllow"][0]);
            Assert.AreEqual(60, (int)server["timeout"]);
            Assert.IsTrue((bool)server["disabled"], "a default field must not undo what the user set");

            var stdio = ClientConfigPanel.MergeJsonConfig(
                "{\"mcpServers\":{\"kitwright\":{\"command\":\"node\",\"args\":[\"x\"]}}}",
                "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null, "cfg.json");
            var replaced = (JObject)JObject.Parse(stdio)["mcpServers"]["kitwright"];
            Assert.IsNull(replaced["command"], "an entry with no URL is not ours to merge into");
            Assert.AreEqual("http://127.0.0.1:8975/", (string)replaced["url"]);
        }

        [Test]
        public void MergeJsonConfig_RefusesAServersKeyThatIsNotAnObject()
        {
            foreach (var bad in new[] { "[1,2]", "\"x\"", "5" })
            {
                Assert.Throws<IOException>(() => ClientConfigPanel.MergeJsonConfig(
                    "{\"mcpServers\":" + bad + "}", "mcpServers", "kitwright",
                    HttpEntry("http://127.0.0.1:8975/"), null, "cfg.json"),
                    $"a non-object mcpServers must not be replaced with {{}}: {bad}");
            }
        }

        [Test]
        public void MergeJsonConfig_DoesNotRewriteDateLikeStringsOfOtherServers()
        {
            var merged = ClientConfigPanel.MergeJsonConfig(
                "{\"mcpServers\":{\"other\":{\"since\":\"2025-10-31T10:00:00.000Z\"}}}",
                "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null, "cfg.json");

            StringAssert.Contains("\"2025-10-31T10:00:00.000Z\"", merged);
        }

        [Test]
        public void MergeJsonConfig_AcceptsCommentsInTheFile()
        {
            var merged = ClientConfigPanel.MergeJsonConfig(
                "{\n  // servers\n  \"mcpServers\": {\n    \"claude-mem\": { \"command\": \"node\" } // keep\n  }\n}\n// end",
                "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null, "cfg.json");

            StringAssert.Contains("claude-mem", merged);
            StringAssert.Contains("kitwright", merged);

            Assert.Throws<IOException>(() => ClientConfigPanel.MergeJsonConfig(
                "{\"mcpServers\":{}} trailing", "mcpServers", "kitwright",
                HttpEntry("http://127.0.0.1:8975/"), null, "cfg.json"));
        }

        [Test]
        public void RemoveTomlSection_OnlyMatchesAHeaderThatStartsALine()
        {
            const string commented = "model = \"x\"\n# [mcp_servers.kitwright]\n[other]\na = 1\n";
            Assert.AreEqual(commented, ClientConfigPanel.RemoveTomlSection(commented, "kitwright"),
                "a header quoted in a comment is not a table");

            Assert.AreEqual("[other]\na = 1\n", ClientConfigPanel.RemoveTomlSection(
                "[mcp_servers.\"kitwright\"]\nurl = \"x\"\n[other]\na = 1\n", "kitwright"),
                "the quoted-key form is the same table");

            Assert.AreEqual("[other]\r\na = 1\r\n", ClientConfigPanel.RemoveTomlSection(
                "  [mcp_servers.kitwright] # ours\r\nurl = \"x\"\r\n[other]\r\na = 1\r\n", "kitwright"));

            const string sibling = "[mcp_servers.kitwright-extra]\nurl = \"y\"\n";
            Assert.AreEqual(sibling, ClientConfigPanel.RemoveTomlSection(sibling, "kitwright"));
        }

        [Test]
        public void EnsureCodexRmcpFeature_IgnoresAFeaturesHeaderInAComment()
        {
            var appended = ClientConfigPanel.EnsureCodexRmcpFeature(
                "# [features]\n[mcp_servers.kitwright]\nurl = \"x\"\n");
            StringAssert.EndsWith("[features]\nrmcp_client = true\n", appended);
            Assert.Less(appended.IndexOf("[mcp_servers.kitwright]"), appended.IndexOf("rmcp_client"),
                "the key belongs under a real [features] table, not at the root");

            Assert.AreEqual("[features]\nrmcp_client = true\nfoo = 1\n",
                ClientConfigPanel.EnsureCodexRmcpFeature("[features]\nfoo = 1\n"));
        }

        [Test]
        public void RewriteToml_UsesTheRealTableNotACommentedOrQuotedOne()
        {
            InTempDir(dir =>
            {
                var path = Path.Combine(dir, "config.toml");
                File.WriteAllText(path,
                    "# [mcp_servers.kitwright]\n# url = \"http://127.0.0.1:1/\"\n" +
                    "[other]\nurl = \"http://elsewhere/\"\n" +
                    "[mcp_servers.\"kitwright\"]\nurl = \"http://127.0.0.1:8766/\"\n");

                Assert.IsTrue(MCPClientConfigAutoRewrite.RewriteToml(path, "kitwright", "http://127.0.0.1:8765/"));

                var rewritten = File.ReadAllText(path);
                StringAssert.Contains("# url = \"http://127.0.0.1:1/\"", rewritten);
                StringAssert.Contains("url = \"http://elsewhere/\"", rewritten);
                StringAssert.Contains("url = \"http://127.0.0.1:8765/\"", rewritten);
                StringAssert.DoesNotContain("8766", rewritten);
            });
        }

        [Test]
        public void RewriteJson_RepairsTheSameStaleUrlHeldUnderTwoKeys()
        {
            InTempDir(dir =>
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path,
                    "{\"mcpServers\":{\"kitwright\":{\"url\":\"http://127.0.0.1:8766/\",\"httpUrl\":\"http://127.0.0.1:8766/\"}}}");

                Assert.IsTrue(MCPClientConfigAutoRewrite.RewriteJson(path, "mcpServers", "kitwright", "http://127.0.0.1:8765/"));

                var rewritten = File.ReadAllText(path);
                StringAssert.DoesNotContain("8766", rewritten);
                StringAssert.Contains("\"httpUrl\":\"http://127.0.0.1:8765/\"", rewritten);
            });
        }

        [Test]
        public void RewriteJson_FindsTheEntryBehindCommentsBeforeAndInsideMcpServers()
        {
            InTempDir(dir =>
            {
                var cases = new[]
                {
                    "{\n  // note\n  \"mcpServers\": { \"kitwright\": { \"url\": \"http://127.0.0.1:8766/\" } }\n}",
                    "{\n  \"mcpServers\": {\n    /* \"} */\n    // mine\n    \"kitwright\": { \"url\": \"http://127.0.0.1:8766/\" }\n  }\n}",
                };

                for (var i = 0; i < cases.Length; i++)
                {
                    var path = Path.Combine(dir, $"config{i}.json");
                    File.WriteAllText(path, cases[i]);

                    Assert.IsTrue(MCPClientConfigAutoRewrite.RewriteJson(path, "mcpServers", "kitwright", "http://127.0.0.1:8765/"),
                        $"case {i}: a comment must not hide the entry");

                    var rewritten = File.ReadAllText(path);
                    StringAssert.Contains("8765", rewritten);
                    StringAssert.DoesNotContain("8766", rewritten);
                    StringAssert.Contains("//", rewritten.Replace("http://", string.Empty), $"case {i}: the comment stays");
                }
            });
        }

        [Test]
        public void StripComments_LeavesStringsAloneAndRemovesBothCommentKinds()
        {
            var stripped = JsonCodec.StripComments(
                "{\"u\":\"http://x/*y*/\", /* a \"} */ \"k\":1 // tail\n}");

            var parsed = (Dictionary<string, object>)JsonCodec.Deserialize(stripped);
            Assert.AreEqual("http://x/*y*/", parsed["u"]);
            Assert.AreEqual(1d, parsed["k"]);
        }

        private static int Occurrences(string text, string value)
            => text.Split(new[] { value }, System.StringSplitOptions.None).Length - 1;

        // The second Configure used to copy the already-merged file over the only copy of the original.
        [Test]
        public void ConfigureJsonFile_KeepsTheFirstBakAndWritesNothingWhenNothingChanges()
        {
            InTempDir(dir =>
            {
                var path = Path.Combine(dir, ".claude.json");
                const string original = "{\"mcpServers\":{\"claude-mem\":{\"command\":\"node\"}}}";
                File.WriteAllText(path, original);

                ClientConfigPanel.ConfigureJsonFile(
                    path, "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null);
                ClientConfigPanel.ConfigureJsonFile(
                    path, "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8976/"), null);

                StringAssert.Contains("8976", File.ReadAllText(path));
                Assert.AreEqual(original, File.ReadAllText(path + ".bak"),
                    "the backup must stay what the user had before the first Configure");

                File.Delete(path + ".bak");
                var sentinel = new System.DateTime(2001, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(path, sentinel);

                ClientConfigPanel.ConfigureJsonFile(
                    path, "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8976/"), null);

                Assert.AreEqual(sentinel, File.GetLastWriteTimeUtc(path), "an unchanged config must not be rewritten");
                Assert.IsFalse(File.Exists(path + ".bak"), "an unchanged config needs no backup");
            });
        }

        [Test]
        public void ConfigureJsonFile_LeavesAnExistingBakAlone_EvenWhenReadOnly()
        {
            InTempDir(dir =>
            {
                var path = Path.Combine(dir, ".claude.json");
                var backup = path + ".bak";
                File.WriteAllText(path, "{\"mcpServers\":{}}");
                File.WriteAllText(backup, "first");
                File.SetAttributes(backup, FileAttributes.ReadOnly);

                try
                {
                    ClientConfigPanel.ConfigureJsonFile(
                        path, "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null);

                    Assert.AreEqual("first", File.ReadAllText(backup));
                    StringAssert.Contains("8975", File.ReadAllText(path));
                }
                finally
                {
                    File.SetAttributes(backup, FileAttributes.Normal);
                }
            });
        }

        [Test]
        public void ConfigureJsonFile_ReportsABackupThatCannotBeMadeBeforeWritingAnything()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("Copying onto a directory fails with different exceptions off Windows.");

            InTempDir(dir =>
            {
                var path = Path.Combine(dir, ".claude.json");
                const string original = "{\"history\":[1],\"mcpServers\":{}}";
                File.WriteAllText(path, original);
                Directory.CreateDirectory(path + ".bak");

                var ex = Assert.Throws<IOException>(() => ClientConfigPanel.ConfigureJsonFile(
                    path, "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null));

                StringAssert.Contains("Nothing was written", ex.Message);
                Assert.AreEqual(original, File.ReadAllText(path));
            });
        }

        [Test]
        public void ReadExistingConfig_RefusesWhatIsNotUtf8AndKeepsTheFileUntouched()
        {
            InTempDir(dir =>
            {
                var latin1 = Path.Combine(dir, "latin1.json");
                var bytes = new byte[] { (byte)'{', (byte)'"', 0xE9, (byte)'"', (byte)':', (byte)'1', (byte)'}' };
                File.WriteAllBytes(latin1, bytes);

                Assert.Throws<IOException>(() => ClientConfigPanel.ReadExistingConfig(latin1));
                Assert.Throws<IOException>(() => ClientConfigPanel.ConfigureJsonFile(
                    latin1, "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null));
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(latin1));
                Assert.IsFalse(File.Exists(latin1 + ".bak"));

                var utf16 = Path.Combine(dir, "utf16.json");
                File.WriteAllText(utf16, "{}", Encoding.Unicode);
                Assert.Throws<IOException>(() => ClientConfigPanel.ReadExistingConfig(utf16));

                var bom = Path.Combine(dir, "bom.json");
                File.WriteAllBytes(bom, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' });
                Assert.AreEqual("{}", ClientConfigPanel.ReadExistingConfig(bom));
            });
        }

        [Test]
        public void ConfigureTomlFile_KeepsTheUserKeysOfOurTableAndWritesNothingTheSecondTime()
        {
            InTempDir(dir =>
            {
                var path = Path.Combine(dir, "config.toml");
                File.WriteAllText(path,
                    "model = \"x\"\n\n[mcp_servers.kitwright]\nurl = \"http://127.0.0.1:8766/\"\n" +
                    "startup_timeout_sec = 30\nenabled_tools = [\"a\"]\n\n[mcp_servers.kitwright.env]\nK = \"v\"\n\n" +
                    "[other]\na = 1\n");

                ClientConfigPanel.ConfigureTomlFile(path, "http://127.0.0.1:8975/");

                var text = File.ReadAllText(path);
                StringAssert.Contains("url = \"http://127.0.0.1:8975/\"", text);
                StringAssert.DoesNotContain("8766", text);
                StringAssert.Contains("startup_timeout_sec = 30", text);
                StringAssert.Contains("enabled_tools = [\"a\"]", text);
                StringAssert.Contains("[mcp_servers.kitwright.env]\nK = \"v\"", text);
                StringAssert.Contains("[other]\na = 1", text);
                Assert.AreEqual(1, Occurrences(text, "[mcp_servers.kitwright]"));
                Assert.IsTrue(File.Exists(path + ".bak"));

                var sentinel = new System.DateTime(2001, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(path, sentinel);
                ClientConfigPanel.ConfigureTomlFile(path, "http://127.0.0.1:8975/");
                Assert.AreEqual(sentinel, File.GetLastWriteTimeUtc(path));

                var fresh = Path.Combine(dir, "fresh.toml");
                ClientConfigPanel.ConfigureTomlFile(fresh, "http://127.0.0.1:8975/");
                StringAssert.Contains("[mcp_servers.kitwright]\nurl = \"http://127.0.0.1:8975/\"", File.ReadAllText(fresh));
                StringAssert.Contains("rmcp_client = true", File.ReadAllText(fresh));
            });
        }

        [Test]
        public void MergeTomlConfig_ReplacesATableThatHasNoUrl()
        {
            var merged = ClientConfigPanel.MergeTomlConfig(
                "[mcp_servers.kitwright]\ncommand = \"node\"\n[other]\na = 1\n",
                "http://127.0.0.1:8975/", "config.toml");

            StringAssert.DoesNotContain("command", merged);
            StringAssert.Contains("url = \"http://127.0.0.1:8975/\"", merged);
            Assert.AreEqual(1, Occurrences(merged, "[mcp_servers.kitwright]"));
        }

        [Test]
        public void TomlFinder_IgnoresAHeaderInsideAMultiLineString()
        {
            foreach (var quote in new[] { "\"\"\"", "'''" })
            {
                var text = "instr = " + quote + "\n[mcp_servers.kitwright]\nnot a table\n" + quote + "\n[other]\na = 1\n";

                Assert.IsFalse(ClientConfigPanel.TryFindMcpServerTable(text, "kitwright", out _, out _));
                Assert.AreEqual(text, ClientConfigPanel.RemoveTomlSection(text, "kitwright"));
            }
        }

        [Test]
        public void TomlFinder_ReadsEverySpellingOfTheHeaderAsOneTable()
        {
            foreach (var header in new[]
                     {
                         "[ mcp_servers.kitwright ]", "[mcp_servers . kitwright]", "[mcp_servers.\"kitwright\"]",
                         "[mcp_servers.'kitwright']", "[\"mcp_servers\".kitwright]"
                     })
            {
                var merged = ClientConfigPanel.MergeTomlConfig(
                    header + "\nurl = \"http://127.0.0.1:8766/\"\n", "http://127.0.0.1:8975/", "config.toml");

                Assert.AreEqual(1, Occurrences(merged, "kitwright"), $"{header} must not gain a second table");
                StringAssert.Contains("8975", merged);
                StringAssert.DoesNotContain("8766", merged);
            }
        }

        [Test]
        public void ConfigureTomlFile_RefusesAnEntryItCannotEditInPlace_AndWritesNothing()
        {
            InTempDir(dir =>
            {
                foreach (var unhandled in new[]
                         {
                             "[mcp_servers]\nkitwright = { url = \"http://127.0.0.1:8766/\" }\n",
                             "mcp_servers.kitwright.url = \"http://127.0.0.1:8766/\"\n",
                             "mcp_servers = { kitwright = { url = \"http://127.0.0.1:8766/\" } }\n",
                         })
                {
                    var path = Path.Combine(dir, System.Guid.NewGuid().ToString("N") + ".toml");
                    File.WriteAllText(path, unhandled);

                    Assert.Throws<IOException>(() => ClientConfigPanel.ConfigureTomlFile(path, "http://127.0.0.1:8975/"),
                        $"a second table would make this file invalid: {unhandled}");
                    Assert.AreEqual(unhandled, File.ReadAllText(path));
                    Assert.IsFalse(File.Exists(path + ".bak"));
                }
            });
        }

        [Test]
        public void TomlFinder_DoesNotEndTheTableAtAnArrayRow()
        {
            Assert.AreEqual("[other]\na = 1\n", ClientConfigPanel.RemoveTomlSection(
                "[mcp_servers.kitwright]\nurl = \"x\"\nm = [\n  [1,2],\n  [3,4],\n]\n[other]\na = 1\n", "kitwright"));

            Assert.AreEqual("[other]\n", ClientConfigPanel.RemoveTomlSection(
                "[mcp_servers.kitwright]\nm = [\n  [1],\n  [2]\n]\n[other]\n", "kitwright"));
        }

        [Test]
        public void TomlFinder_KeepsTheCommentAboveTheNextTable()
        {
            Assert.AreEqual("# about other\n[other]\na = 1\n", ClientConfigPanel.RemoveTomlSection(
                "[mcp_servers.kitwright]\nurl = \"x\"\n# about other\n[other]\na = 1\n", "kitwright"));
        }

        [Test]
        public void MergeTomlConfig_DropsEveryStaleEntryName()
        {
            foreach (var stale in ClientConfigPanel.StaleServerEntryNames())
            {
                var merged = ClientConfigPanel.MergeTomlConfig(
                    "[mcp_servers." + stale + "]\nurl = \"http://127.0.0.1:1/\"\n",
                    "http://127.0.0.1:8975/", "config.toml");

                StringAssert.DoesNotContain("[mcp_servers." + stale + "]", merged);
                StringAssert.Contains("[mcp_servers.kitwright]", merged);
            }
        }

        [Test]
        public void ConfigStatus_RecognisesEverySpellingOfTheTomlTable()
        {
            Assert.IsTrue(ClientConfigPanel.ConfigHasOurEntry("[mcp_servers.'kitwright']\nurl = \"x\"\n"));
            Assert.IsTrue(ClientConfigPanel.ConfigHasOurEntry("[ mcp_servers . kitwright ]\nurl = \"x\"\n"));
            Assert.IsFalse(ClientConfigPanel.ConfigHasOurEntry("# [mcp_servers.kitwright]\n[other]\n"));
        }

        [Test]
        public void MergeJsonConfig_DoesNotLeaveAStaleUrlOfTheOtherSpellingNextToTheFreshOne()
        {
            var merged = ClientConfigPanel.MergeJsonConfig(
                "{\"mcpServers\":{\"kitwright\":{\"serverUrl\":\"http://old/\",\"disabled\":true}}}",
                "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null, "cfg.json");

            var server = (JObject)JObject.Parse(merged)["mcpServers"]["kitwright"];
            Assert.IsNull(server["serverUrl"]);
            Assert.AreEqual("http://127.0.0.1:8975/", (string)server["url"]);
            Assert.IsTrue((bool)server["disabled"], "the user's own keys stay");
        }

        [Test]
        public void MergeJsonConfig_RefusesToWriteWhatCommentsTurnIntoInvalidJson()
        {
            foreach (var broken in new[]
                     {
                         "{\"mcpServers\":{\"a\":{\"x\":[,1,,2]}}}",
                         "{\"mcpServers\":{\"a\":{\"x\":[1,\n// see */ here\n2]}}}",
                     })
            {
                var ex = Assert.Throws<IOException>(() => ClientConfigPanel.MergeJsonConfig(
                    broken, "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null, "cfg.json"),
                    $"the merged text would not be JSON: {broken}");
                StringAssert.Contains("Nothing was written", ex.Message);
            }
        }

        [Test]
        public void MergeJsonConfig_TreatsANullServersKeyAsAbsent_AndNamesANonObjectRoot()
        {
            var merged = ClientConfigPanel.MergeJsonConfig(
                "{\"mcpServers\":null}", "mcpServers", "kitwright",
                HttpEntry("http://127.0.0.1:8975/"), null, "cfg.json");
            Assert.AreEqual("http://127.0.0.1:8975/",
                (string)JObject.Parse(merged)["mcpServers"]["kitwright"]["url"]);

            foreach (var root in new[] { "[]", "null", "5", "\"x\"" })
            {
                var ex = Assert.Throws<IOException>(() => ClientConfigPanel.MergeJsonConfig(
                    root, "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null, "cfg.json"));
                StringAssert.Contains("not a JSON object", ex.Message, $"valid JSON is not 'invalid': {root}");
            }

            var garbage = Assert.Throws<IOException>(() => ClientConfigPanel.MergeJsonConfig(
                "not json", "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null, "cfg.json"));
            StringAssert.Contains("not valid JSON", garbage.Message);
        }

        [Test]
        public void MergeJsonConfig_CarriesTheUserKeysOfAPinnedOrLegacyEntryOver()
        {
            var stale = ClientConfigPanel.StaleServerEntryNames().First();

            var merged = ClientConfigPanel.MergeJsonConfig(
                "{\"mcpServers\":{\"" + stale + "\":{\"url\":\"http://mine/\",\"autoApprove\":[\"a\"]}}}",
                "mcpServers", "kitwright", HttpEntry("http://127.0.0.1:8975/"), null, "cfg.json");

            var servers = (JObject)JObject.Parse(merged)["mcpServers"];
            Assert.IsNull(servers[stale], "the stale entry is replaced, not duplicated");
            Assert.AreEqual("a", (string)servers["kitwright"]["autoApprove"][0]);
            Assert.AreEqual("http://127.0.0.1:8975/", (string)servers["kitwright"]["url"]);
        }
    }
}
