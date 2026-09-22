// Copyright (C) KitWright. All rights reserved.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
                StringAssert.Contains("unity-mcp-workflow@1.0.0", File.ReadAllText(agentsPath));
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(agentsPath));
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(claudePath));
                StringAssert.Contains("version: 1.0.0", File.ReadAllText(skillPath));
                StringAssert.Contains("<!-- KitWright Unity skill version: unity-mcp-workflow@1.0.0 -->", File.ReadAllText(skillPath));

                var manifestJson = File.ReadAllText(ProjectSkillsManager.GetManifestPath(projectRoot));
                StringAssert.Contains("\"skillVersions\"", manifestJson);
                StringAssert.Contains("\"id\": \"unity-mcp-workflow\"", manifestJson);
                StringAssert.Contains("\"version\": \"1.0.0\"", manifestJson);
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
                RemoveLinesContaining(skillPath, "version: 1.0.0");

                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                var status = ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, "codex");
                var skillStatus = status.Files.First(file => file.Path == skillPath);

                Assert.IsTrue(status.HasUpdates);
                Assert.IsTrue(skillStatus.RequiresUpgrade);
                Assert.AreEqual("unknown", skillStatus.InstalledVersion);
                Assert.AreEqual("1.0.0", skillStatus.ExpectedVersion);
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
                Assert.AreEqual("1.0.0", skillStatus.ExpectedVersion);
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
