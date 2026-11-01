// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using KitWright.Editor.MCP.Server;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class ProjectSkillsManagerTests
    {
        [Test]
        public void PackageSkillCatalog_ParsesFrontMatterAndKeepsBodyVerbatim()
        {
            const string markdown = "---\nname: match\ndescription: Align Unity output: to a target image\n---\n\n# Match\n\nBody line.\n";

            var skill = PackageSkillCatalog.Parse("match", markdown);

            Assert.AreEqual("match", skill.Id);
            Assert.AreEqual("Match", skill.Title);
            // Split on the FIRST colon only, or a description containing one loses its tail.
            Assert.AreEqual("Align Unity output: to a target image", skill.Description);
            Assert.AreEqual("# Match\n\nBody line.", skill.Body);
            Assert.IsFalse(skill.IsBuiltIn);

            // Version tracks content, so an edited skill file reports an update.
            Assert.AreNotEqual(skill.Version, PackageSkillCatalog.Parse("match", markdown + "more\n").Version);
        }

        [Test]
        public void ApplyConfiguration_WritesSkillVersionMarkers()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex", "claude" });

                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                var status = ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, "codex");
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                var skillPath = GetCodexWorkflowSkillPath(projectRoot);
                var claudePath = ProjectSkillsManager.GetClaudeInstructionsPath(projectRoot);

                Assert.IsFalse(status.HasUpdates);
                Assert.IsTrue(File.Exists(agentsPath));
                Assert.IsTrue(File.Exists(skillPath));
                StringAssert.Contains("unity-mcp-workflow@1.0.1", File.ReadAllText(agentsPath));
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(agentsPath));
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(claudePath));
                StringAssert.Contains("version: 1.0.1", File.ReadAllText(skillPath));
                StringAssert.Contains("<!-- KitWright Unity skill version: unity-mcp-workflow@1.0.1 -->", File.ReadAllText(skillPath));

                var manifestJson = File.ReadAllText(ProjectSkillsManager.GetManifestPath(projectRoot));
                StringAssert.Contains("\"skillVersions\"", manifestJson);
                StringAssert.Contains("\"id\": \"unity-mcp-workflow\"", manifestJson);
                StringAssert.Contains("\"version\": \"1.0.1\"", manifestJson);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        // request_recompile and wait_for_compilation already return the compile errors, and
        // tools/list is the whole catalog: guidance that chains an error read or probes with it
        // costs every project a round-trip per edit and tens of KB per probe.
        [Test]
        public void ApplyConfiguration_GuidanceDropsTheErrorReadAndProbesWithPing()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex", "claude", "cursor" });

                foreach (var path in new[]
                {
                    ProjectSkillsManager.GetCodexAgentsPath(projectRoot),
                    ProjectSkillsManager.GetClaudeInstructionsPath(projectRoot),
                    GetCodexWorkflowSkillPath(projectRoot),
                    Path.Combine(ProjectSkillsManager.GetCursorRulesPath(projectRoot), "kitwright-unity-mcp-workflow.mdc")
                })
                {
                    var content = File.ReadAllText(path);
                    StringAssert.Contains("force_refresh=false", content, path);
                    StringAssert.Contains("with `get_compilation_errors`", content, path);
                    StringAssert.Contains("`ping`", content, path);
                    StringAssert.DoesNotContain("\"method\":\"tools/list\"", content, path);
                }
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        // Skill and instruction files are meant to be committed, and the access token is what keeps
        // other processes out of the editor.
        [Test]
        public void ApplyConfiguration_WritesTheAccessTokenIntoNoGeneratedFile()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex", "claude", "cursor", "agents" });

                var token = ServerToken.Get();
                var files = Directory.GetFiles(projectRoot, "*", SearchOption.AllDirectories);
                Assert.IsNotEmpty(files);
                foreach (var path in files)
                    StringAssert.DoesNotContain(token, File.ReadAllText(path), path);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_AppendsAndUpdatesOnlyManagedBlock()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                File.WriteAllText(agentsPath, "# Team instructions\n\nKeep this before KitWright.\n");

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" });
                File.AppendAllText(agentsPath, "\nKeep this after KitWright.\n");
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" });

                var content = File.ReadAllText(agentsPath);
                StringAssert.Contains("# Team instructions", content);
                StringAssert.Contains("Keep this before KitWright.", content);
                StringAssert.Contains("Keep this after KitWright.", content);
                Assert.AreEqual(1, CountOccurrences(content, ProjectSkillsManager.ManagedMarker));
                Assert.AreEqual(1, CountOccurrences(content, ProjectSkillsManager.ManagedEndMarker));
                CollectionAssert.DoesNotContain(
                    ProjectSkillsManager.GetPlatformConflictPaths(projectRoot, new[] { "codex" }),
                    agentsPath);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_MigratesExactLegacyGeneratedFile()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var manifest = CreateManifest("codex");
                ProjectSkillsManager.SaveManifest(projectRoot, manifest);
                manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                var legacy = BuildLegacyCodexContent(projectRoot, manifest).Replace("\r\n", "\n").Replace("\n", "\r\n");
                File.WriteAllText(agentsPath, legacy);

                var before = ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, "codex");
                var projectStatus = before.Files.First(file => file.Path == agentsPath);
                Assert.IsTrue(before.HasUpdates);
                Assert.AreEqual("legacy marker", projectStatus.InstalledVersion);

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" });

                var migrated = File.ReadAllText(agentsPath);
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, migrated);
                Assert.AreEqual(1, CountOccurrences(migrated, ProjectSkillsManager.ManagedMarker));
                Assert.IsFalse(ProjectSkillsManager.GetUpgradeStatus(
                    projectRoot,
                    ProjectSkillsManager.LoadManifest(projectRoot),
                    "codex").HasUpdates);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_RejectsEditedLegacyFileWithoutChangingIt()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var manifest = CreateManifest("codex");
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                var editedLegacy = BuildLegacyCodexContent(projectRoot, manifest) + "\n# Hand-authored notes\nKeep me.\n";
                File.WriteAllText(agentsPath, editedLegacy);

                var exception = Assert.Throws<InvalidOperationException>(() =>
                    ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" }));

                StringAssert.Contains("No content was changed", exception.Message);
                Assert.AreEqual(editedLegacy, File.ReadAllText(agentsPath));
                StringAssert.DoesNotContain(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(agentsPath));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void DisablePlatform_RemovesOnlyManagedBlockAndDeletesKitWrightOnlyFile()
        {
            var sharedRoot = CreateTempProjectPath();
            var kitwrightOnlyRoot = CreateTempProjectPath();

            try
            {
                var sharedPath = ProjectSkillsManager.GetCodexAgentsPath(sharedRoot);
                File.WriteAllText(sharedPath, "# Team instructions\nKeep me.\n");
                ProjectSkillsManager.ApplyConfiguration(sharedRoot, new[] { "codex" });
                ProjectSkillsManager.ApplyConfiguration(sharedRoot, Array.Empty<string>());

                var sharedContent = File.ReadAllText(sharedPath);
                StringAssert.Contains("# Team instructions", sharedContent);
                StringAssert.Contains("Keep me.", sharedContent);
                StringAssert.DoesNotContain(ProjectSkillsManager.ManagedMarker, sharedContent);
                StringAssert.DoesNotContain(ProjectSkillsManager.ManagedEndMarker, sharedContent);

                var kitwrightOnlyPath = ProjectSkillsManager.GetCodexAgentsPath(kitwrightOnlyRoot);
                ProjectSkillsManager.ApplyConfiguration(kitwrightOnlyRoot, new[] { "codex" });
                Assert.IsTrue(File.Exists(kitwrightOnlyPath));
                ProjectSkillsManager.ApplyConfiguration(kitwrightOnlyRoot, Array.Empty<string>());
                Assert.IsFalse(File.Exists(kitwrightOnlyPath));
            }
            finally
            {
                DeleteTempProjectPath(sharedRoot);
                DeleteTempProjectPath(kitwrightOnlyRoot);
            }
        }

        [Test]
        public void GetUpgradeStatus_DetectsUnversionedManagedSkillFile()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" });
                var skillPath = GetCodexWorkflowSkillPath(projectRoot);
                RemoveLinesContaining(skillPath, "KitWright Unity skill version:");
                RemoveLinesContaining(skillPath, "version: 1.0.1");

                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                var status = ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, "codex");
                var skillStatus = status.Files.First(file => file.Path == skillPath);

                Assert.IsTrue(status.HasUpdates);
                Assert.IsTrue(skillStatus.RequiresUpgrade);
                Assert.AreEqual("unknown", skillStatus.InstalledVersion);
                Assert.AreEqual("1.0.1", skillStatus.ExpectedVersion);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void GetUpgradeStatus_DetectsMissingGeneratedSkillFile()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" });
                var skillPath = GetCodexWorkflowSkillPath(projectRoot);
                File.Delete(skillPath);

                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                var status = ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, "codex");
                var skillStatus = status.Files.First(file => file.Path == skillPath);

                Assert.IsTrue(status.HasUpdates);
                Assert.IsTrue(skillStatus.Missing);
                Assert.AreEqual("missing", skillStatus.InstalledVersion);
                Assert.AreEqual("1.0.1", skillStatus.ExpectedVersion);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void GetPlatformConflictPaths_DetectsUnmanagedCursorRule()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var rulesRoot = ProjectSkillsManager.GetCursorRulesPath(projectRoot);
                Directory.CreateDirectory(rulesRoot);
                var path = Path.Combine(rulesRoot, "kitwright-unity-mcp-workflow.mdc");
                File.WriteAllText(path, "# User-owned Cursor rule");

                var conflicts = ProjectSkillsManager.GetPlatformConflictPaths(projectRoot, new[] { "cursor" });

                CollectionAssert.Contains(conflicts, path);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        private static string GetCodexWorkflowSkillPath(string projectRoot)
        {
            return Path.Combine(
                ProjectSkillsManager.GetCodexSkillsRoot(projectRoot),
                "unity-mcp-workflow",
                "SKILL.md");
        }

        private static ProjectSkillsManager.ProjectSkillsManifest CreateManifest(params string[] platforms)
        {
            return new ProjectSkillsManager.ProjectSkillsManifest
            {
                platforms = platforms.ToList()
            };
        }

        private static string BuildLegacyCodexContent(
            string projectRoot,
            ProjectSkillsManager.ProjectSkillsManifest manifest)
        {
            var method = typeof(ProjectSkillsManager).GetMethod(
                "BuildLegacyCodexAgentsContent",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (string)method.Invoke(null, new object[] { projectRoot, manifest });
        }

        private static int CountOccurrences(string content, string marker)
        {
            var count = 0;
            var index = 0;
            while ((index = content.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += marker.Length;
            }
            return count;
        }

        private static void RemoveLinesContaining(string path, string text)
        {
            var lines = File.ReadAllLines(path)
                .Where(line => !line.Contains(text))
                .ToArray();
            File.WriteAllLines(path, lines);
        }

        [Test]
        public void GetInstalledSkills_CoversEverySkillWithoutAnOptIn()
        {
            var installed = ProjectSkillsManager.GetInstalledSkills().Select(skill => skill.Id).ToArray();

            // No manifest, no toggles: a package that ships a skill is a project that has it.
            CollectionAssert.IsSupersetOf(installed, ProjectSkillsManager.GetBuiltInSkills().Select(skill => skill.Id));
            CollectionAssert.IsSupersetOf(installed, ProjectSkillsManager.GetPackageSkills().Select(skill => skill.Id));
            CollectionAssert.AllItemsAreUnique(installed);
        }

        [Test]
        public void ShortDescription_KeepsLeadSentenceAndDropsTriggerList()
        {
            Assert.AreEqual(
                "Match Unity output to a target image",
                ProjectSkillsManager.ShortDescription(
                    "Match Unity output to a target image. Two modes are available. Triggers - match, /match."));

            Assert.AreEqual(
                "Play a game in Play Mode",
                ProjectSkillsManager.ShortDescription("Play a game in Play Mode. Triggers - playtest"));

            Assert.AreEqual(string.Empty, ProjectSkillsManager.ShortDescription(null));
        }

        // Antigravity and Claude Code list `.agents/skills/<folder>` as `/<folder>`, so the folder
        // is the skill id. A legacy `kitwright-<id>` folder is ours by its marker and is cleaned up.
        [Test]
        public void ApplyConfiguration_NamesSkillFolderAfterTheSkillAndRemovesLegacyPrefixedFolder()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var skillsRoot = ProjectSkillsManager.GetAgentsSkillsRoot(projectRoot);
                var legacy = Path.Combine(skillsRoot, "kitwright-unity-mcp-workflow");
                var userOwned = Path.Combine(skillsRoot, "my-skill");
                Directory.CreateDirectory(legacy);
                Directory.CreateDirectory(userOwned);
                File.WriteAllText(Path.Combine(legacy, "SKILL.md"), ProjectSkillsManager.ManagedMarker + "\n");
                File.WriteAllText(Path.Combine(userOwned, "SKILL.md"), "# Mine\n");

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "agents" });

                Assert.IsTrue(File.Exists(Path.Combine(skillsRoot, "unity-mcp-workflow", "SKILL.md")));
                Assert.IsFalse(Directory.Exists(legacy));
                Assert.IsTrue(File.Exists(Path.Combine(userOwned, "SKILL.md")));

                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                Assert.IsFalse(ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, "agents").HasUpdates);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void GetPlatformConflictPaths_ReportsUserOwnedSkillFolderWithASkillsName()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var path = Path.Combine(ProjectSkillsManager.GetAgentsSkillsRoot(projectRoot), "unity-mcp-workflow", "SKILL.md");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "# Hand-written\n");

                CollectionAssert.Contains(ProjectSkillsManager.GetPlatformConflictPaths(projectRoot, new[] { "agents" }), path);
                CollectionAssert.IsEmpty(ProjectSkillsManager.GetPlatformConflictPaths(projectRoot, new[] { "claude" }));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        private const string BeginMarker = ProjectSkillsManager.ManagedMarker;
        private const string EndMarker = ProjectSkillsManager.ManagedEndMarker;

        private static ManagedBlockText.Markers ScanKitWright(string content)
        {
            return ManagedBlockText.Scan(content, BeginMarker, EndMarker);
        }

        private static string ClaudeSkillPath(string projectRoot, string id)
        {
            return Path.Combine(ProjectSkillsManager.GetClaudeSkillsRoot(projectRoot), id, "SKILL.md");
        }

        [Test]
        public void Scan_MatchesAMarkerOnlyWhenItIsALineOfItsOwn()
        {
            var prose = "Never edit `" + BeginMarker + "` or " + EndMarker + " by hand.\n";
            var scan = ScanKitWright(prose);
            Assert.AreEqual(0, scan.BeginCount);
            Assert.AreEqual(0, scan.EndCount);

            var content = "intro\r\n  " + BeginMarker + "  \r\nbody\r\n" + EndMarker + "\r\noutro\r\n";
            scan = ScanKitWright(content);
            Assert.IsTrue(scan.IsWellFormed);
            Assert.AreEqual(
                "  " + BeginMarker + "  \r\nbody\r\n" + EndMarker,
                content.Substring(scan.Start, scan.End - scan.Start));
        }

        [Test]
        public void Upsert_AndStrip_RefuseMarkersThatAreDuplicatedUnmatchedOrOutOfOrder()
        {
            var cases = new[]
            {
                "user\n" + BeginMarker + "\nbody\n",
                "user\n" + EndMarker + "\nbody\n",
                EndMarker + "\n" + BeginMarker + "\n",
                BeginMarker + "\n" + BeginMarker + "\n" + EndMarker + "\n",
                BeginMarker + "\n" + EndMarker + "\n" + BeginMarker + "\n" + EndMarker + "\n"
            };

            foreach (var content in cases)
            {
                var updated = ManagedBlockText.Upsert(content, "block", "# T", "not the legacy file", "\n", out var refusal);
                Assert.IsNull(updated, content);
                StringAssert.Contains("No content was changed", refusal, content);

                ManagedBlockText.Strip(content, "# T", "not the legacy file", "\n", out refusal);
                Assert.IsNotNull(refusal, content);
            }
        }

        [Test]
        public void Upsert_ReplacesAGameWrightBlockInPlaceAndDropsOneBesideAKitWrightBlock()
        {
            var old = ManagedBlockText.LegacyBeginMarker + "\nold body\n" + ManagedBlockText.LegacyEndMarker + "\n";
            var kit = BeginMarker + "\nkit body\n" + EndMarker + "\n";
            var block = BeginMarker + "\nnew body\n" + EndMarker;

            var replaced = ManagedBlockText.Upsert("Before.\n\n" + old + "\nAfter.\n", block, "# T", string.Empty, "\n", out var refusal);
            Assert.IsNull(refusal);
            Assert.AreEqual("Before.\n\n" + block + "\n\nAfter.\n", replaced);

            var both = ManagedBlockText.Upsert("Before.\n" + kit + "\n" + old + "After.\n", block, "# T", string.Empty, "\n", out refusal);
            Assert.IsNull(refusal);
            Assert.AreEqual("Before.\n" + block + "\n\nAfter.\n", both);
        }

        [Test]
        public void Upsert_WritesTheBlockWithTheFilesLineEndingAndLeavesTheUsersTextAlone()
        {
            var content = "# Team\r\n\r\nKeep.\r\n\r\n" + BeginMarker + "\r\nold\r\n" + EndMarker + "\r\n\r\nTail.\r\n";
            var block = BeginMarker + "\nnew\nmore\n" + EndMarker;

            var updated = ManagedBlockText.Upsert(content, block, "# T", string.Empty, "\r\n", out var refusal);

            Assert.IsNull(refusal);
            Assert.AreEqual(
                "# Team\r\n\r\nKeep.\r\n\r\n" + BeginMarker + "\r\nnew\r\nmore\r\n" + EndMarker + "\r\n\r\nTail.\r\n",
                updated);
        }

        [Test]
        public void Strip_RemovesOnlyTheBlockAndDeletesAFileThatHeldNothingElse()
        {
            var block = BeginMarker + "\nbody\n" + EndMarker;

            Assert.AreEqual(
                "# T\nKeep.\n",
                ManagedBlockText.Strip("# T\nKeep.\n\n" + block + "\n", "# T", string.Empty, "\n", out var refusal));
            Assert.IsNull(refusal);

            Assert.IsNull(ManagedBlockText.Strip("# T\n\n" + block + "\n", "# T", string.Empty, "\n", out refusal));
            Assert.IsNull(refusal);

            var prose = "Mentions " + BeginMarker + " only.\n";
            Assert.AreEqual(prose, ManagedBlockText.Strip(prose, "# T", string.Empty, "\n", out refusal));
            Assert.IsNull(refusal);
        }

        [Test]
        public void HasManagedHeader_NeedsTheMarkerAsTheFirstLineAfterTheFrontMatter()
        {
            Assert.IsTrue(ManagedBlockText.HasManagedHeader("---\nname: x\n---\n" + BeginMarker + "\n"));
            Assert.IsTrue(ManagedBlockText.HasManagedHeader(BeginMarker + "\n"));
            Assert.IsTrue(ManagedBlockText.HasManagedHeader("﻿---\r\nname: x\r\n---\r\n" + ManagedBlockText.LegacyBeginMarker + "\r\n"));
            Assert.IsFalse(ManagedBlockText.HasManagedHeader("# Mine\n" + BeginMarker + "\n"));
            Assert.IsFalse(ManagedBlockText.HasManagedHeader("# Mine mentions " + BeginMarker + " inline\n"));
            Assert.IsFalse(ManagedBlockText.HasManagedHeader("---\nname: x\n" + BeginMarker + "\n"));
            Assert.IsFalse(ManagedBlockText.HasManagedHeader(null));
        }

        [Test]
        public void ManagedTextFile_ReadsBomAndDominantNewlineAndRefusesInvalidUtf8()
        {
            var text = ManagedTextFile.Decode(
                new byte[] { 0xEF, 0xBB, 0xBF, 0x61, 0x0D, 0x0A, 0x62, 0x0D, 0x0A, 0x63, 0x0A },
                out var bom,
                out var newline);

            Assert.IsTrue(bom);
            Assert.AreEqual("a\r\nb\r\nc\n", text);
            Assert.AreEqual("\r\n", newline);
            CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, ManagedTextFile.Encode("a", true));
            Assert.Throws<DecoderFallbackException>(() => ManagedTextFile.Decode(new byte[] { 0x61, 0xFF, 0x62 }, out _, out _));
        }

        [Test]
        public void ApplyConfiguration_IgnoresProseThatMentionsTheMarkers()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                var prose = "Never edit the `" + BeginMarker + "` or `" + EndMarker + "` lines by hand.";
                File.WriteAllText(agentsPath, "# Team\n\n" + prose + "\n");

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" });
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" });
                StringAssert.Contains(prose, File.ReadAllText(agentsPath));
                Assert.IsTrue(ScanKitWright(File.ReadAllText(agentsPath)).IsWellFormed);

                ProjectSkillsManager.ApplyConfiguration(projectRoot, Array.Empty<string>());
                Assert.AreEqual("# Team\n\n" + prose + "\n", File.ReadAllText(agentsPath));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_RefusesBrokenMarkersBeforeWritingAnything()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                var broken = "# Team\n" + EndMarker + "\nhand-written\n" + BeginMarker + "\n";
                File.WriteAllText(agentsPath, broken);

                var exception = Assert.Throws<InvalidOperationException>(() =>
                    ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "claude", "codex" }));

                StringAssert.Contains("No content was changed", exception.Message);
                Assert.AreEqual(broken, File.ReadAllText(agentsPath));
                Assert.IsFalse(File.Exists(ProjectSkillsManager.GetManifestPath(projectRoot)));
                Assert.IsFalse(File.Exists(ProjectSkillsManager.GetClaudeInstructionsPath(projectRoot)));
                Assert.IsFalse(Directory.Exists(ProjectSkillsManager.GetClaudeSkillsRoot(projectRoot)));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_RefusesAnInstructionsFileThatIsNotUtf8WithoutTouchingIt()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var claudePath = ProjectSkillsManager.GetClaudeInstructionsPath(projectRoot);
                var bytes = new byte[] { 0x23, 0x20, 0xFF, 0xFE, 0x41, 0x0A };
                File.WriteAllBytes(claudePath, bytes);

                var exception = Assert.Throws<InvalidOperationException>(() =>
                    ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "claude" }));

                StringAssert.Contains("not valid UTF-8", exception.Message);
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(claudePath));
                Assert.IsFalse(File.Exists(ProjectSkillsManager.GetManifestPath(projectRoot)));

                Assert.DoesNotThrow(() => ProjectSkillsManager.ApplyConfiguration(projectRoot, Array.Empty<string>()));
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(claudePath));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_KeepsTheBomAndLineEndingsOfTheFilesItRewrites()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                File.WriteAllBytes(agentsPath, ManagedTextFile.Encode("# Team\r\n\r\nKeep.\r\n", true));

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" });
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" });

                AssertBomAndOnlyCrLf(File.ReadAllBytes(agentsPath));
                StringAssert.Contains("# Team\r\n\r\nKeep.\r\n", File.ReadAllText(agentsPath));

                var skillPath = GetCodexWorkflowSkillPath(projectRoot);
                File.WriteAllBytes(skillPath, ManagedTextFile.Encode(File.ReadAllText(skillPath).Replace("\n", "\r\n"), true));

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" });

                AssertBomAndOnlyCrLf(File.ReadAllBytes(skillPath));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        private static void AssertBomAndOnlyCrLf(byte[] bytes)
        {
            CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
            var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.IsFalse(Regex.IsMatch(text, "(?<!\r)\n"), "a bare LF was written into a CRLF file");
        }

        [Test]
        public void ApplyConfiguration_WritesNewFilesWithLfAndNoBom()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "claude", "cursor" });

                foreach (var path in new[]
                {
                    ProjectSkillsManager.GetClaudeInstructionsPath(projectRoot),
                    ClaudeSkillPath(projectRoot, "unity-mcp-workflow"),
                    Path.Combine(ProjectSkillsManager.GetCursorRulesPath(projectRoot), "kitwright-unity-mcp-workflow.mdc")
                })
                {
                    var bytes = File.ReadAllBytes(path);
                    Assert.AreNotEqual(0xEF, bytes[0], path);
                    CollectionAssert.DoesNotContain(bytes, (byte)0x0D, path);
                }
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_WritesNoLocalPathIntoTheCommittedBlocks()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex", "claude" });

                foreach (var path in new[]
                {
                    ProjectSkillsManager.GetCodexAgentsPath(projectRoot),
                    ProjectSkillsManager.GetClaudeInstructionsPath(projectRoot)
                })
                {
                    var content = File.ReadAllText(path);
                    StringAssert.DoesNotContain(Path.GetFileName(projectRoot), content, path);
                    StringAssert.Contains("- Project root:", content, path);
                }

                StringAssert.Contains(projectRoot, BuildLegacyCodexContent(projectRoot, CreateManifest("codex")));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void DisablePlatform_KeepsSkillFoldersAndFilesItDidNotWrite()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var skillsRoot = ProjectSkillsManager.GetClaudeSkillsRoot(projectRoot);
                var mentions = ClaudeSkillPath(projectRoot, "mentions");
                var unlisted = ClaudeSkillPath(projectRoot, "hand-copied");
                Directory.CreateDirectory(Path.GetDirectoryName(mentions));
                Directory.CreateDirectory(Path.GetDirectoryName(unlisted));
                File.WriteAllText(mentions, "# Mine\n\nThe line " + BeginMarker + " marks generated files.\n");
                File.WriteAllText(unlisted, "---\nname: hand-copied\n---\n" + BeginMarker + "\n\n# Hand copied\n");

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "claude" });
                var ownFolder = Path.Combine(skillsRoot, "unity-mcp-workflow");
                var note = Path.Combine(ownFolder, "notes.txt");
                File.WriteAllText(note, "mine");
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "claude" });
                ProjectSkillsManager.ApplyConfiguration(projectRoot, Array.Empty<string>());

                Assert.IsTrue(File.Exists(mentions));
                Assert.IsTrue(File.Exists(unlisted));
                Assert.IsTrue(File.Exists(note));
                Assert.IsFalse(File.Exists(Path.Combine(ownFolder, "SKILL.md")));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_ReplacesAGameWrightBlockInsteadOfAppendingASecondOne()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var claudePath = ProjectSkillsManager.GetClaudeInstructionsPath(projectRoot);
                File.WriteAllText(
                    claudePath,
                    "# Team\n\nBefore.\n\n" + ManagedBlockText.LegacyBeginMarker + "\nold body\n" + ManagedBlockText.LegacyEndMarker + "\n\nAfter.\n");

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "claude" });

                var content = File.ReadAllText(claudePath);
                Assert.AreEqual(0, CountOccurrences(content, ManagedBlockText.LegacyBeginMarker));
                Assert.AreEqual(0, CountOccurrences(content, ManagedBlockText.LegacyEndMarker));
                Assert.AreEqual(1, CountOccurrences(content, BeginMarker));
                Assert.AreEqual(1, CountOccurrences(content, EndMarker));
                StringAssert.DoesNotContain("old body", content);
                Assert.Less(content.IndexOf("Before.", StringComparison.Ordinal), content.IndexOf(BeginMarker, StringComparison.Ordinal));
                Assert.Less(content.IndexOf(EndMarker, StringComparison.Ordinal), content.IndexOf("After.", StringComparison.Ordinal));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        private static string WriteDemoSkillSource(string source)
        {
            Directory.CreateDirectory(Path.Combine(source, "scripts"));
            Directory.CreateDirectory(Path.Combine(source, "references"));
            File.WriteAllText(Path.Combine(source, "SKILL.md"), "---\nname: demo\ndescription: Demo skill\n---\n\n# Demo\n\nBody.\n");
            File.WriteAllText(Path.Combine(source, "scripts", "run.py"), "print('hi')\n");
            File.WriteAllText(Path.Combine(source, "scripts", "run.py.meta"), "meta");
            File.WriteAllText(Path.Combine(source, "references", "api.md"), "# Api\n");
            return File.ReadAllText(Path.Combine(source, "SKILL.md"));
        }

        private static void InstallDemoSkill(
            string projectRoot,
            string source,
            ProjectSkillsManager.ProjectSkillsManifest manifest,
            bool installed)
        {
            var skills = installed
                ? new[] { PackageSkillCatalog.Parse("demo", File.ReadAllText(Path.Combine(source, "SKILL.md")), source) }
                : Array.Empty<ProjectSkillsManager.SkillDefinition>();

            ProjectSkillsManager.WriteManagedSkillDirectories(
                projectRoot,
                ProjectSkillsManager.GetClaudeSkillsRoot(projectRoot),
                manifest,
                ProjectSkillsManager.SkillPlatform.Claude,
                skills);
        }

        [Test]
        public void WriteManagedSkillDirectories_CopiesTheFilesBesideSkillMdRecordsThemAndRemovesOnlyThose()
        {
            var projectRoot = CreateTempProjectPath();
            var source = CreateTempProjectPath();

            try
            {
                WriteDemoSkillSource(source);
                var manifest = new ProjectSkillsManager.ProjectSkillsManifest();

                InstallDemoSkill(projectRoot, source, manifest, true);

                var installed = Path.GetDirectoryName(ClaudeSkillPath(projectRoot, "demo"));
                Assert.IsTrue(File.Exists(Path.Combine(installed, "SKILL.md")));
                Assert.AreEqual("print('hi')\n", File.ReadAllText(Path.Combine(installed, "scripts", "run.py")));
                Assert.AreEqual("# Api\n", File.ReadAllText(Path.Combine(installed, "references", "api.md")));
                Assert.IsFalse(File.Exists(Path.Combine(installed, "scripts", "run.py.meta")));
                CollectionAssert.AreEqual(
                    new[]
                    {
                        ".claude/skills/demo/SKILL.md",
                        ".claude/skills/demo/references/api.md",
                        ".claude/skills/demo/scripts/run.py"
                    },
                    manifest.managedFiles);

                var mine = Path.Combine(installed, "mine.txt");
                File.WriteAllText(mine, "user");

                InstallDemoSkill(projectRoot, source, manifest, false);

                Assert.IsFalse(File.Exists(Path.Combine(installed, "SKILL.md")));
                Assert.IsFalse(Directory.Exists(Path.Combine(installed, "scripts")));
                Assert.IsFalse(Directory.Exists(Path.Combine(installed, "references")));
                Assert.IsTrue(File.Exists(mine));
                CollectionAssert.IsEmpty(manifest.managedFiles);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
                DeleteTempProjectPath(source);
            }
        }

        [Test]
        public void WriteManagedSkillDirectories_RemovesASiblingThePackageDroppedOnTheNextSync()
        {
            var projectRoot = CreateTempProjectPath();
            var source = CreateTempProjectPath();

            try
            {
                WriteDemoSkillSource(source);
                var manifest = new ProjectSkillsManager.ProjectSkillsManifest();
                InstallDemoSkill(projectRoot, source, manifest, true);

                File.Delete(Path.Combine(source, "scripts", "run.py"));
                File.WriteAllText(Path.Combine(source, "scripts", "new.py"), "new\n");
                InstallDemoSkill(projectRoot, source, manifest, true);

                var installed = Path.GetDirectoryName(ClaudeSkillPath(projectRoot, "demo"));
                Assert.IsFalse(File.Exists(Path.Combine(installed, "scripts", "run.py")));
                Assert.IsTrue(File.Exists(Path.Combine(installed, "scripts", "new.py")));
                CollectionAssert.Contains(manifest.managedFiles, ".claude/skills/demo/scripts/new.py");
                CollectionAssert.DoesNotContain(manifest.managedFiles, ".claude/skills/demo/scripts/run.py");
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
                DeleteTempProjectPath(source);
            }
        }

        [Test]
        public void WriteManagedSkillDirectories_DoesNotOverwriteAFileTheUserOwnsBesideSkillMd()
        {
            var projectRoot = CreateTempProjectPath();
            var source = CreateTempProjectPath();

            try
            {
                WriteDemoSkillSource(source);
                var installed = Path.GetDirectoryName(ClaudeSkillPath(projectRoot, "demo"));
                Directory.CreateDirectory(Path.Combine(installed, "references"));
                File.WriteAllText(Path.Combine(installed, "references", "api.md"), "mine\n");
                var manifest = new ProjectSkillsManager.ProjectSkillsManifest();

                InstallDemoSkill(projectRoot, source, manifest, true);

                Assert.AreEqual("mine\n", File.ReadAllText(Path.Combine(installed, "references", "api.md")));
                CollectionAssert.DoesNotContain(manifest.managedFiles, ".claude/skills/demo/references/api.md");
                CollectionAssert.Contains(manifest.managedFiles, ".claude/skills/demo/scripts/run.py");
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
                DeleteTempProjectPath(source);
            }
        }

        [Test]
        public void PackageSkillVersion_ChangesWithAFileBesideSkillMdButNotWithItsMetaFile()
        {
            var source = CreateTempProjectPath();
            var bare = CreateTempProjectPath();

            try
            {
                var markdown = WriteDemoSkillSource(source);
                File.WriteAllText(Path.Combine(bare, "SKILL.md"), markdown);

                var before = PackageSkillCatalog.Parse("demo", markdown, source).Version;
                File.WriteAllText(Path.Combine(source, "scripts", "run.py.meta"), "other meta");
                Assert.AreEqual(before, PackageSkillCatalog.Parse("demo", markdown, source).Version);

                File.WriteAllText(Path.Combine(source, "scripts", "run.py"), "print('changed')\n");
                Assert.AreNotEqual(before, PackageSkillCatalog.Parse("demo", markdown, source).Version);

                Assert.AreEqual(
                    PackageSkillCatalog.Parse("demo", markdown).Version,
                    PackageSkillCatalog.Parse("demo", markdown, bare).Version);
            }
            finally
            {
                DeleteTempProjectPath(source);
                DeleteTempProjectPath(bare);
            }
        }

        [Test]
        public void PlanSiblingFiles_SkipsSkillMdAndMetaFilesAndRefusesPathsThatLeaveTheFolder()
        {
            var destination = CreateTempProjectPath();

            try
            {
                var plan = ProjectSkillsManager.PlanSiblingFiles(
                    new[]
                    {
                        "SKILL.md", "scripts\\run.py", "references/a.md", "scripts/run.py.meta",
                        "../evil.txt", "a/../../b.txt", "/abs.txt", "nested/SKILL.md"
                    },
                    destination,
                    out var refused);

                CollectionAssert.AreEqual(new[] { "nested/SKILL.md", "references/a.md", "scripts/run.py" }, plan);
                CollectionAssert.AreEqual(new[] { "../evil.txt", "a/../../b.txt", "/abs.txt" }, refused);
            }
            finally
            {
                DeleteTempProjectPath(destination);
            }
        }

        [Test]
        public void ResolveInside_RefusesWhatLeavesTheRoot()
        {
            var root = CreateTempProjectPath();

            try
            {
                Assert.IsNotNull(ProjectSkillsManager.ResolveInside(root, "a/b.txt"));
                Assert.IsNotNull(ProjectSkillsManager.ResolveInside(root, "a/../b.txt"));
                Assert.IsNull(ProjectSkillsManager.ResolveInside(root, "../x.txt"));
                Assert.IsNull(ProjectSkillsManager.ResolveInside(root, "a/../../x.txt"));
                Assert.IsNull(ProjectSkillsManager.ResolveInside(root, "/abs.txt"));
                Assert.IsNull(ProjectSkillsManager.ResolveInside(root, string.Empty));
                Assert.IsNull(ProjectSkillsManager.ResolveInside(root, "."));
            }
            finally
            {
                DeleteTempProjectPath(root);
            }
        }

        [Test]
        public void Manifest_DropsManagedFileEntriesThatWouldLeaveTheProject()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.SaveManifest(projectRoot, new ProjectSkillsManager.ProjectSkillsManifest
                {
                    managedFiles = new System.Collections.Generic.List<string>
                    {
                        "../outside.txt",
                        "/abs.txt",
                        ".claude/skills/a/../../../b",
                        ".claude\\skills\\a\\SKILL.md",
                        ".claude/skills/a/SKILL.md"
                    }
                });

                CollectionAssert.AreEqual(
                    new[] { ".claude/skills/a/SKILL.md" },
                    ProjectSkillsManager.LoadManifest(projectRoot).managedFiles);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        private static string Visible(string text)
        {
            return text.Replace("\r", "\\r").Replace("\n", "\\n");
        }

        [Test]
        public void Scan_FindsTheMarkerLinesWhateverEndsTheLines()
        {
            foreach (var newline in new[] { "\n", "\r\n", "\r" })
            {
                var content = "intro" + newline + "  " + BeginMarker + "  " + newline + "body" + newline + EndMarker + newline + "outro" + newline;
                var scan = ScanKitWright(content);

                Assert.IsTrue(scan.IsWellFormed, Visible(newline));
                Assert.AreEqual(
                    "  " + BeginMarker + "  " + newline + "body" + newline + EndMarker,
                    content.Substring(scan.Start, scan.End - scan.Start),
                    Visible(newline));

                var prose = "intro" + newline + "see " + BeginMarker + " here" + newline + BeginMarker + " not alone" + newline;
                Assert.AreEqual(0, ScanKitWright(prose).BeginCount, Visible(newline));
            }

            var mixed = "a\r\n" + BeginMarker + "\rbody\n" + EndMarker + "\r\nz";
            var mixedScan = ScanKitWright(mixed);
            Assert.IsTrue(mixedScan.IsWellFormed);
            Assert.AreEqual(BeginMarker + "\rbody\n" + EndMarker, mixed.Substring(mixedScan.Start, mixedScan.End - mixedScan.Start));

            Assert.IsTrue(ScanKitWright("x\r" + BeginMarker + "\ry\r" + EndMarker).IsWellFormed);
        }

        [Test]
        public void DominantNewline_NamesTheEndingMostLinesUseIncludingALoneCr()
        {
            Assert.AreEqual("\r", ManagedTextFile.DominantNewline("a\rb\rc\r"));
            Assert.AreEqual("\r", ManagedTextFile.DominantNewline("a\r\nb\rc\rd\r"));
            Assert.AreEqual("\r\n", ManagedTextFile.DominantNewline("a\r\nb\r\nc\n"));
            Assert.AreEqual("\n", ManagedTextFile.DominantNewline("a\nb\nc\r\n"));
            Assert.AreEqual("\n", ManagedTextFile.DominantNewline("one line"));
            Assert.AreEqual("\n", ManagedTextFile.DominantNewline(string.Empty));

            var text = ManagedTextFile.Decode(Encoding.UTF8.GetBytes("a\rb\rc\r"), out _, out var newline);
            Assert.AreEqual("a\rb\rc\r", text);
            Assert.AreEqual("\r", newline);
        }

        [Test]
        public void UpsertAndStrip_KeepACrOnlyFileCrOnlyAndNeverAppendASecondBlock()
        {
            var content = "# Team\rKeep.\r\r" + BeginMarker + "\rold\r" + EndMarker + "\rTail.\r";
            var block = BeginMarker + "\nnew\nmore\n" + EndMarker;
            var newline = ManagedTextFile.DominantNewline(content);

            var updated = ManagedBlockText.Upsert(content, block, "# T", string.Empty, newline, out var refusal);
            Assert.IsNull(refusal);
            Assert.AreEqual("# Team\rKeep.\r\r" + BeginMarker + "\rnew\rmore\r" + EndMarker + "\rTail.\r", updated);
            Assert.AreEqual(updated, ManagedBlockText.Upsert(updated, block, "# T", string.Empty, newline, out refusal));

            var appended = ManagedBlockText.Upsert("# Team\rKeep.\r", block, "# T", string.Empty, "\r", out refusal);
            Assert.AreEqual("# Team\rKeep.\r\r" + BeginMarker + "\rnew\rmore\r" + EndMarker + "\r", appended);
            Assert.AreEqual(appended, ManagedBlockText.Upsert(appended, block, "# T", string.Empty, "\r", out refusal));

            Assert.AreEqual("# Team\rKeep.\r", ManagedBlockText.Strip(appended, "# T", string.Empty, "\r", out refusal));
            Assert.IsNull(refusal);
            Assert.IsNull(ManagedBlockText.Strip("# T\r\r" + BeginMarker + "\rbody\r" + EndMarker + "\r", "# T", string.Empty, "\r", out refusal));
            Assert.IsNull(refusal);
        }

        [Test]
        public void ApplyConfiguration_KeepsACrOnlyInstructionsFileCrOnlyWithOneBlock()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                File.WriteAllText(agentsPath, "# Team\rKeep.\r");

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" });
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" });

                var content = File.ReadAllText(agentsPath);
                StringAssert.StartsWith("# Team\rKeep.\r", content);
                Assert.IsFalse(content.Contains("\n"), "an LF was written into a CR-only file");
                Assert.AreEqual(1, CountOccurrences(content, BeginMarker));
                Assert.AreEqual(1, CountOccurrences(content, EndMarker));

                ProjectSkillsManager.ApplyConfiguration(projectRoot, Array.Empty<string>());
                Assert.AreEqual("# Team\rKeep.\r", File.ReadAllText(agentsPath));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        private static void WriteSkillFolder(string skillsRoot, string name, string header, params string[] extraFiles)
        {
            var directory = Path.Combine(skillsRoot, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), "---\nname: " + name + "\n---\n" + header + "\n\n# Old\n");

            foreach (var extra in extraFiles)
            {
                var path = Path.Combine(directory, extra);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "mine");
            }
        }

        [Test]
        public void WriteManagedSkillDirectories_RemovesAnUnrecordedFolderOfAnUninstalledSkillOnlyWhenNothingElseIsInIt()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var skillsRoot = ProjectSkillsManager.GetClaudeSkillsRoot(projectRoot);
                WriteSkillFolder(skillsRoot, "gone", BeginMarker);
                WriteSkillFolder(skillsRoot, "kitwright-old", BeginMarker);
                WriteSkillFolder(skillsRoot, "busy", BeginMarker, "scripts/run.py");
                WriteSkillFolder(skillsRoot, "mine", "# Mine");
                WriteSkillFolder(skillsRoot, "unlisted", BeginMarker);
                var manifest = new ProjectSkillsManager.ProjectSkillsManifest
                {
                    retiredSkillIds = new List<string> { "gone", "old", "busy", "mine" }
                };

                ProjectSkillsManager.WriteManagedSkillDirectories(
                    projectRoot,
                    skillsRoot,
                    manifest,
                    ProjectSkillsManager.SkillPlatform.Claude,
                    Array.Empty<ProjectSkillsManager.SkillDefinition>());

                Assert.IsFalse(Directory.Exists(Path.Combine(skillsRoot, "gone")));
                Assert.IsFalse(Directory.Exists(Path.Combine(skillsRoot, "kitwright-old")));
                Assert.IsTrue(File.Exists(Path.Combine(skillsRoot, "busy", "SKILL.md")));
                Assert.IsTrue(File.Exists(Path.Combine(skillsRoot, "busy", "scripts", "run.py")));
                Assert.IsTrue(File.Exists(Path.Combine(skillsRoot, "mine", "SKILL.md")));
                Assert.IsTrue(File.Exists(Path.Combine(skillsRoot, "unlisted", "SKILL.md")));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_RemovesTheFolderOfASkillAnOldManifestListedThatIsNoLongerInstalled()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var manifestPath = ProjectSkillsManager.GetManifestPath(projectRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
                File.WriteAllText(
                    manifestPath,
                    "{\"platforms\":[\"claude\"],\"skillVersions\":[{\"id\":\"gone-skill\",\"version\":\"1\"},{\"id\":\"busy-skill\",\"version\":\"1\"}]}");
                var skillsRoot = ProjectSkillsManager.GetClaudeSkillsRoot(projectRoot);
                WriteSkillFolder(skillsRoot, "gone-skill", BeginMarker);
                WriteSkillFolder(skillsRoot, "busy-skill", BeginMarker, "notes.txt");

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "claude" });

                Assert.IsFalse(Directory.Exists(Path.Combine(skillsRoot, "gone-skill")));
                Assert.IsTrue(File.Exists(Path.Combine(skillsRoot, "busy-skill", "SKILL.md")));
                Assert.IsTrue(File.Exists(Path.Combine(skillsRoot, "busy-skill", "notes.txt")));
                Assert.IsTrue(File.Exists(ClaudeSkillPath(projectRoot, "unity-mcp-workflow")));

                var json = File.ReadAllText(manifestPath);
                StringAssert.DoesNotContain("gone-skill", json);
                StringAssert.DoesNotContain("retiredSkillIds", json);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void RemoveSupersededGameWrightFolders_DeletesAnOldFolderOnlyWhenItsSkillIsInstalledNowAndNothingElseIsInIt()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var skillsRoot = ProjectSkillsManager.GetClaudeSkillsRoot(projectRoot);
                var old = ManagedBlockText.LegacyBeginMarker;
                WriteSkillFolder(skillsRoot, "gamewright-demo", old);
                WriteSkillFolder(skillsRoot, "gamewright-prefixed", old);
                WriteSkillFolder(skillsRoot, "gamewright-recorded", old, "scripts/x.py");
                WriteSkillFolder(skillsRoot, "gamewright-busy", old, "mine.txt");
                WriteSkillFolder(skillsRoot, "gamewright-gone", old);
                WriteSkillFolder(skillsRoot, "gamewright-current", BeginMarker);
                WriteSkillFolder(skillsRoot, "gamewright-mine", "# Mine");
                var manifest = new ProjectSkillsManager.ProjectSkillsManifest
                {
                    managedFiles = new List<string> { ".claude/skills/gamewright-recorded/scripts/x.py" }
                };
                var current = new HashSet<string>
                {
                    ".claude/skills/demo/SKILL.md",
                    ".claude/skills/kitwright-prefixed/SKILL.md",
                    ".claude/skills/recorded/SKILL.md",
                    ".claude/skills/busy/SKILL.md",
                    ".claude/skills/current/SKILL.md"
                };

                var kept = ProjectSkillsManager.RemoveSupersededGameWrightFolders(projectRoot, skillsRoot, manifest, current);

                foreach (var removed in new[] { "gamewright-demo", "gamewright-prefixed", "gamewright-recorded" })
                    Assert.IsFalse(Directory.Exists(Path.Combine(skillsRoot, removed)), removed);

                foreach (var left in new[] { "gamewright-busy", "gamewright-gone", "gamewright-current", "gamewright-mine" })
                    Assert.IsTrue(File.Exists(Path.Combine(skillsRoot, left, "SKILL.md")), left);

                Assert.IsTrue(File.Exists(Path.Combine(skillsRoot, "gamewright-busy", "mine.txt")));
                CollectionAssert.IsEmpty(manifest.managedFiles);
                Assert.AreEqual(2, kept.Count);
                StringAssert.StartsWith("gamewright-busy", kept[0]);
                StringAssert.StartsWith("gamewright-gone", kept[1]);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void WriteManagedSkillDirectories_RemovesTheGameWrightFolderOfTheSkillItInstalls()
        {
            var projectRoot = CreateTempProjectPath();
            var source = CreateTempProjectPath();

            try
            {
                WriteDemoSkillSource(source);
                WriteSkillFolder(ProjectSkillsManager.GetClaudeSkillsRoot(projectRoot), "gamewright-demo", ManagedBlockText.LegacyBeginMarker);

                InstallDemoSkill(projectRoot, source, new ProjectSkillsManager.ProjectSkillsManifest(), true);

                Assert.IsFalse(Directory.Exists(Path.Combine(ProjectSkillsManager.GetClaudeSkillsRoot(projectRoot), "gamewright-demo")));
                Assert.IsTrue(File.Exists(ClaudeSkillPath(projectRoot, "demo")));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
                DeleteTempProjectPath(source);
            }
        }

        [Test]
        public void ApplyConfiguration_RemovesAnOldGameWrightCursorRuleOnlyWhenItsKitWrightRuleIsWritten()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var rulesRoot = ProjectSkillsManager.GetCursorRulesPath(projectRoot);
                Directory.CreateDirectory(rulesRoot);
                var header = "---\ndescription: x\n---\n" + ManagedBlockText.LegacyBeginMarker + "\n";
                var replaced = Path.Combine(rulesRoot, "gamewright-unity-mcp-workflow.mdc");
                var orphan = Path.Combine(rulesRoot, "gamewright-not-installed.mdc");
                var mine = Path.Combine(rulesRoot, "gamewright-mine.mdc");
                File.WriteAllText(replaced, header);
                File.WriteAllText(orphan, header);
                File.WriteAllText(mine, "# Mine\n");

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "cursor" });

                Assert.IsFalse(File.Exists(replaced));
                Assert.IsTrue(File.Exists(orphan));
                Assert.IsTrue(File.Exists(mine));
                Assert.IsTrue(File.Exists(Path.Combine(rulesRoot, "kitwright-unity-mcp-workflow.mdc")));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        private static string CreateTempProjectPath()
        {
            var path = Path.Combine(Path.GetTempPath(), "KitWrightProjectSkillsTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTempProjectPath(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }
}
