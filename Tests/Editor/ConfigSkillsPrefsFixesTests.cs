// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Settings;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    public sealed class ConfigSkillsPrefsFixesTests
    {
        private static void InTempDir(Action<string> body)
        {
            var dir = Path.Combine(Path.GetTempPath(), "KitWrightConfigSkillsPrefs_" + Guid.NewGuid().ToString("N"));
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

        [Test]
        public void WriteConfig_RefusesAFileThatChangedSinceItWasRead()
        {
            InTempDir(dir =>
            {
                var path = Path.Combine(dir, ".claude.json");
                const string savedByTheClient = "{\"history\":[1,2,3]}";
                File.WriteAllText(path, savedByTheClient);

                var ex = Assert.Throws<IOException>(() =>
                    ClientConfigPanel.WriteConfig(path, "{\"whatWeRead\":true}", "{\"ours\":true}"));

                StringAssert.Contains("changed while it was being edited", ex.Message);
                Assert.AreEqual(savedByTheClient, File.ReadAllText(path), "the client's change was overwritten");
                Assert.IsFalse(File.Exists(path + ".bak"));

                ClientConfigPanel.WriteConfig(path, savedByTheClient, "{\"ours\":true}");
                Assert.AreEqual("{\"ours\":true}", File.ReadAllText(path));
                Assert.AreEqual(savedByTheClient, File.ReadAllText(path + ".bak"));
            });
        }

        [Test]
        public void WriteConfig_RefusesAFileThatAppearedAfterItWasReadAsMissing()
        {
            InTempDir(dir =>
            {
                var path = Path.Combine(dir, "mcp.json");
                ClientConfigPanel.WriteConfig(path, null, "{\"first\":true}");
                Assert.AreEqual("{\"first\":true}", File.ReadAllText(path));

                Assert.Throws<IOException>(() => ClientConfigPanel.WriteConfig(path, null, "{\"second\":true}"));
                Assert.AreEqual("{\"first\":true}", File.ReadAllText(path));
            });
        }

        [Test]
        public void RewriteJson_ChangesOnlyTheEntryAndNotTheSameUrlElsewhere()
        {
            InTempDir(dir =>
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path,
                    "{\n  \"mcpServers\": {\n" +
                    "    \"kitwright\": { \"url\": \"http://127.0.0.1:8766/\" },\n" +
                    "    \"mirror\": { \"url\": \"http://127.0.0.1:8766/\" }\n  },\n" +
                    "  // was \"http://127.0.0.1:8766/\"\n" +
                    "  \"note\": \"http://127.0.0.1:8766/\"\n}\n");

                Assert.IsTrue(MCPClientConfigAutoRewrite.RewriteJson(path, "mcpServers", "kitwright", "http://127.0.0.1:8765/"));

                var rewritten = File.ReadAllText(path);
                StringAssert.Contains("\"kitwright\": { \"url\": \"http://127.0.0.1:8765/\" }", rewritten);
                StringAssert.Contains("\"mirror\": { \"url\": \"http://127.0.0.1:8766/\" }", rewritten);
                StringAssert.Contains("// was \"http://127.0.0.1:8766/\"", rewritten);
                StringAssert.Contains("\"note\": \"http://127.0.0.1:8766/\"", rewritten);
            });
        }

        [Test]
        public void FindStringValues_ReadsOnlyTheStringsOfTheNamedObjectAndSkipsComments()
        {
            const string json =
                "{\n // \"mcpServers\": { \"kitwright\": { \"url\": \"decoy\" } }\n" +
                " \"mcpServers\": { /* \"} */ \"kitwright\": { \"type\": \"http\", \"url\": \"http://a/\", \"args\": [\"x\"], \"n\": 1 },\n" +
                "   \"other\": { \"url\": \"http://b/\" } }\n}";

            var values = JsonCodec.FindStringValues(json, "mcpServers", "kitwright");

            CollectionAssert.AreEqual(new[] { "type", "url" }, values.Select(value => value.Key).ToArray());
            var url = values.Single(value => value.Key == "url");
            Assert.AreEqual("\"http://a/\"", json.Substring(url.Start, url.Length));
            Assert.IsEmpty(JsonCodec.FindStringValues(json, "mcpServers", "missing"));
            Assert.IsEmpty(JsonCodec.FindStringValues(null, "mcpServers"));
        }

        [Test]
        public void RewriteToml_ReadsTheTablesOwnUrlKeyNotAnotherKeyEndingInUrl()
        {
            InTempDir(dir =>
            {
                var path = Path.Combine(dir, "config.toml");
                File.WriteAllText(path,
                    "[mcp_servers.kitwright]\nbase_url = \"http://127.0.0.1:8766/p/deadbeef/\"\nurl = \"http://127.0.0.1:8766/\"\n");

                Assert.IsTrue(MCPClientConfigAutoRewrite.RewriteToml(path, "kitwright", "http://127.0.0.1:8765/", true));

                var rewritten = File.ReadAllText(path);
                StringAssert.Contains("base_url = \"http://127.0.0.1:8766/p/deadbeef/\"", rewritten);
                StringAssert.Contains("\nurl = \"http://127.0.0.1:8765/\"", rewritten);
            });
        }

        [Test]
        public void TomlUrlValue_ReadsBothQuoteStylesAndIgnoresComments()
        {
            Assert.AreEqual("http://a/", ClientConfigPanel.TomlUrlValue("[t]\nurl = \"http://a/\"\n"));
            Assert.AreEqual("http://a/", ClientConfigPanel.TomlUrlValue("[t]\n  url='http://a/'\n"));
            Assert.IsNull(ClientConfigPanel.TomlUrlValue("[t]\n# url = \"http://a/\"\nbase_url = \"http://b/\"\n"));
        }

        [Test]
        public void Deserialize_ReadsAnEscapedSlashAsASlash()
        {
            var parsed = (System.Collections.Generic.Dictionary<string, object>)JsonCodec.Deserialize(
                "{\"path\":\"Assets\\/Scenes\\/Main.unity\"}");

            Assert.AreEqual("Assets/Scenes/Main.unity", parsed["path"]);
        }

        [Test]
        public void PackageSkill_ReadsFrontMatterFromAFileWithCrOnlyLineEndings()
        {
            const string markdown = "---\rname: demo\rdescription: A demo skill\r---\rBody line";

            Assert.AreEqual("demo", PackageSkillCatalog.FrontMatterValue(markdown, "name"));
            Assert.AreEqual("A demo skill", PackageSkillCatalog.FrontMatterValue(markdown, "description"));
            Assert.AreEqual("Body line", PackageSkillCatalog.StripFrontMatter(markdown));
        }

        [Test]
        public void PackageSkill_ReadsABlockScalarDescriptionAndKeepsTheOtherFrontMatter()
        {
            const string markdown =
                "---\nname: wrapped\ndescription: >-\n  First line of the description\n  continues here.\n" +
                "disable-model-invocation: true\nallowed-tools: Read, Grep\nmetadata:\n  owner: team\n---\n\n# Wrapped\n\nBody line.\n";

            var skill = PackageSkillCatalog.Parse("wrapped", markdown);

            Assert.AreEqual("First line of the description continues here.", skill.Description);
            Assert.AreEqual(
                "disable-model-invocation: true\nallowed-tools: Read, Grep\nmetadata:\n  owner: team",
                skill.ExtraFrontMatter);

            var document = ProjectSkillsManager.BuildSkillDocument(skill, ProjectSkillsManager.SkillPlatform.Claude);
            StringAssert.Contains("description: First line of the description continues here.", document);
            Assert.Less(document.IndexOf("platform: claude", StringComparison.Ordinal),
                document.IndexOf("disable-model-invocation: true", StringComparison.Ordinal));
            Assert.Less(document.IndexOf("owner: team", StringComparison.Ordinal),
                document.IndexOf(ProjectSkillsManager.ManagedMarker, StringComparison.Ordinal));
        }

        [Test]
        public void PackageSkill_ReadsQuotedAndWrappedPlainDescriptions()
        {
            Assert.AreEqual("A \"quoted\" one",
                PackageSkillCatalog.FrontMatterValue("---\ndescription: \"A \\\"quoted\\\" one\"\n---\nx", "description"));
            Assert.AreEqual("it's here",
                PackageSkillCatalog.FrontMatterValue("---\ndescription: 'it''s here'\n---\nx", "description"));
            Assert.AreEqual("wraps onto the next line",
                PackageSkillCatalog.FrontMatterValue("---\ndescription: wraps onto\n  the next line\n---\nx", "description"));
            Assert.AreEqual("line one\nline two",
                PackageSkillCatalog.FrontMatterValue("---\ndescription: |\n  line one\n  line two\n---\nx", "description"));
            Assert.IsNull(PackageSkillCatalog.FrontMatterValue("---\nmetadata:\n  description: nested\n---\nx", "description"));
        }

        [Test]
        public void SkillDocument_QuotesADescriptionThatBareYamlCannotHold()
        {
            var skill = PackageSkillCatalog.Parse("match", "---\nname: match\ndescription: Align Unity output: to a target image\n---\n\n# Match\n\nBody line.\n");

            var document = ProjectSkillsManager.BuildSkillDocument(skill, ProjectSkillsManager.SkillPlatform.Agents);

            StringAssert.Contains("description: \"Align Unity output: to a target image\"", document);
            Assert.AreEqual("Plain text, with commas.", PackageSkillCatalog.YamlScalar("Plain text, with commas."));
            Assert.AreEqual("\"say \\\"hi\\\" # now\"", PackageSkillCatalog.YamlScalar("say \"hi\" # now"));
            Assert.AreEqual("\"two lines\"", PackageSkillCatalog.YamlScalar("two\nlines"));
        }

        [Test]
        public void CursorRule_CarriesThePackageSkillBody()
        {
            var skill = PackageSkillCatalog.Parse("match", "---\nname: match\ndescription: Match an image\n---\n\n# Match\n\nBody line.\n");

            var rule = ProjectSkillsManager.BuildCursorRuleContent(skill);

            StringAssert.Contains("# Match", rule);
            StringAssert.Contains("Body line.", rule);
            StringAssert.Contains("alwaysApply: false", rule);
            StringAssert.Contains(ProjectSkillsManager.ManagedMarker, rule);
        }

        [Test]
        public void PackageSkillVersion_IsNotThePlainHashOfTheSourceSoARendererChangeCanMoveIt()
        {
            const string markdown = "---\nname: match\ndescription: Match an image\n---\n\n# Match\n\nBody line.\n";
            string sourceOnly;
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                sourceOnly = string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(markdown))
                    .Take(5).Select(b => b.ToString("x2")));
            }

            Assert.AreNotEqual(sourceOnly, PackageSkillCatalog.Parse("match", markdown).Version);
        }

        private static string NewProjectWithAPortOffset()
        {
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var path = Path.Combine(Path.GetTempPath(), "KitWrightConfigSettings_" + Guid.NewGuid().ToString("N"));
                if (ProjectIdentity.PortOffsetFromProjectPath(path) == 0)
                    continue;

                Directory.CreateDirectory(Path.Combine(path, "UserSettings"));
                return path;
            }

            Assert.Ignore("No temp project path with a non-zero derived port offset.");
            return null;
        }

        private static string SettingsFile(string project) =>
            Path.Combine(project, "UserSettings", "KitWrightMcpSettings.json");

        [Test]
        public void Settings_KeepAFileTheyCannotParseAsBakBeforeWritingDefaults()
        {
            var project = NewProjectWithAPortOffset();
            try
            {
                const string handEdited = "not json at all";
                File.WriteAllText(SettingsFile(project), handEdited);

                var controller = new SettingsController(project);

                Assert.AreEqual(handEdited, File.ReadAllText(SettingsFile(project) + ".bak"));
                Assert.AreEqual(8765 + ProjectIdentity.PortOffsetFromProjectPath(project), controller.MCPServerPort);
                StringAssert.Contains("\"port\"", File.ReadAllText(SettingsFile(project)));
            }
            finally
            {
                try { Directory.Delete(project, true); } catch { }
            }
        }

        [Test]
        public void Settings_DerivePortForABlankFileButNotForOneThatParses()
        {
            var blank = NewProjectWithAPortOffset();
            var typed = NewProjectWithAPortOffset();
            try
            {
                File.WriteAllText(SettingsFile(blank), "  \n");
                File.WriteAllText(SettingsFile(typed), "{\"enabled\":false,\"port\":8765}");

                Assert.AreEqual(8765 + ProjectIdentity.PortOffsetFromProjectPath(blank), new SettingsController(blank).MCPServerPort);
                Assert.AreEqual(8765, new SettingsController(typed).MCPServerPort);
                Assert.IsFalse(File.Exists(SettingsFile(typed) + ".bak"));
            }
            finally
            {
                try { Directory.Delete(blank, true); } catch { }
                try { Directory.Delete(typed, true); } catch { }
            }
        }

        [Test]
        public void SettingsAndScreenshotWriters_RouteEveryWriteThroughAtomicFile()
        {
            foreach (var relative in new[]
                     {
                         "Editor/Settings/SettingsController.cs",
                         "Editor/Tools/Builtins/ScreenshotFunctions.cs",
                         "Editor/Tools/Builtins/ScreenshotFunctions.Compare.cs",
                     })
            {
                var file = Path.Combine(OptionalModuleGuardTests.PackageRoot(), relative);
                Assert.IsTrue(File.Exists(file), "Missing source file: " + file);

                Assert.IsFalse(Regex.IsMatch(File.ReadAllText(file), @"(?<!Atomic)File\.WriteAll(Text|Bytes)"),
                    relative + " writes directly instead of through AtomicFile.");
            }
        }

        [Test]
        public void PrefTools_RefuseATypeTheyDoNotKnowInsteadOfReadingItAsAuto()
        {
            var key = "KwConfigSkillsPrefs_" + Guid.NewGuid().ToString("N");
            try
            {
                foreach (var tool in new[] { "get_editor_pref", "get_player_pref" })
                {
                    var answer = Refused(tool, "key", key, "type", "wobble");
                    Assert.AreEqual("INVALID_PARAM", (string)answer["code"], tool);
                    StringAssert.Contains("string", (string)answer["data"]["expected"]);
                }

                foreach (var tool in new[] { "set_editor_pref", "set_player_pref" })
                {
                    Assert.AreEqual("INVALID_PARAM", Code(tool, "key", key, "value", "1", "type", "boolean"), tool);
                }

                Assert.IsFalse(EditorPrefs.HasKey(key), "a refused type still wrote the key");
                Assert.IsFalse(PlayerPrefs.HasKey(key), "a refused type still wrote the key");
            }
            finally
            {
                EditorPrefs.DeleteKey(key);
                PlayerPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void IsKnownPrefType_AcceptsTheDocumentedNamesAndABlankOne()
        {
            foreach (var known in new[] { null, "", " ", "auto", "AUTO", "string", "Int", "float", "bool" })
                Assert.IsTrue(PrefsFunctions.IsKnownPrefType(known), known ?? "null");

            foreach (var unknown in new[] { "wobble", "boolean", "integer", "str" })
                Assert.IsFalse(PrefsFunctions.IsKnownPrefType(unknown), unknown);
        }

        [Test]
        public void TryWritePref_ReadsAnIntWithTheInvariantCultureWhateverTheEditorCultureIs()
        {
            var key = "KwConfigSkillsPrefs_" + Guid.NewGuid().ToString("N");
            var thread = Thread.CurrentThread;
            var before = thread.CurrentCulture;
            try
            {
                var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                culture.NumberFormat.NegativeSign = "~";
                thread.CurrentCulture = culture;

                Assert.IsTrue(PrefsFunctions.TryWritePref(key, "-5", "int", isEditor: true, out var error), error);
                Assert.AreEqual(-5, EditorPrefs.GetInt(key));
                Assert.IsFalse(PrefsFunctions.TryWritePref(key, "~5", "int", isEditor: true, out _));
            }
            finally
            {
                thread.CurrentCulture = before;
                EditorPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void TryWritePref_BoolTakesTheLiteralsTheOtherToolsTake()
        {
            var key = "KwConfigSkillsPrefs_" + Guid.NewGuid().ToString("N");
            try
            {
                foreach (var (text, expected) in new[] { ("true", true), ("TRUE", true), ("1", true), ("yes", true), ("false", false), ("0", false), ("No", false) })
                {
                    Assert.IsTrue(PrefsFunctions.TryWritePref(key, text, "bool", isEditor: true, out var error), text + ": " + error);
                    Assert.AreEqual(expected, EditorPrefs.GetBool(key), text);
                }

                Assert.IsFalse(PrefsFunctions.TryWritePref(key, "maybe", "bool", isEditor: true, out _));
            }
            finally
            {
                EditorPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void TryWritePref_RefusesANonFiniteFloatInBothStores()
        {
            var key = "KwConfigSkillsPrefs_" + Guid.NewGuid().ToString("N");
            try
            {
                foreach (var isEditor in new[] { true, false })
                {
                    foreach (var text in new[] { "NaN", "Infinity", "-Infinity", "1e999" })
                        Assert.IsFalse(PrefsFunctions.TryWritePref(key, text, "float", isEditor, out _), text + " editor=" + isEditor);
                }

                Assert.IsFalse(EditorPrefs.HasKey(key), "a refused float still wrote the key");
                Assert.IsFalse(PlayerPrefs.HasKey(key), "a refused float still wrote the key");
                Assert.IsTrue(PrefsFunctions.TryWritePref(key, "1.5", "float", isEditor: true, out var error), error);
            }
            finally
            {
                EditorPrefs.DeleteKey(key);
                PlayerPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void GetEditorPref_AutoReportsAStoredZeroOrOneAsIntAndFlagsItBoolLike()
        {
            var key = "KwConfigSkillsPrefs_" + Guid.NewGuid().ToString("N");
            try
            {
                foreach (var stored in new[] { 0, 1 })
                {
                    EditorPrefs.SetInt(key, stored);

                    var auto = Ok("get_editor_pref", "key", key);
                    Assert.AreEqual("int", (string)auto["data"]["type"], "an int written must read back as an int");
                    Assert.AreEqual(stored, (int)auto["data"]["value"]);
                    Assert.IsTrue((bool)auto["data"]["boolLike"]);

                    var forced = Ok("get_editor_pref", "key", key, "type", "bool");
                    Assert.AreEqual("bool", (string)forced["data"]["type"]);
                    Assert.AreEqual(stored == 1, (bool)forced["data"]["value"]);
                    Assert.IsNull(forced["data"]["boolLike"]);
                }

                EditorPrefs.SetInt(key, 7);
                var seven = Ok("get_editor_pref", "key", key);
                Assert.AreEqual("int", (string)seven["data"]["type"]);
                Assert.IsNull(seven["data"]["boolLike"]);

                EditorPrefs.SetFloat(key, 1.5f);
                Assert.IsNull(Ok("get_editor_pref", "key", key)["data"]["boolLike"]);
            }
            finally
            {
                EditorPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void IsBoolLike_OnlyForAnIntZeroOrOne()
        {
            Assert.IsTrue(PrefsFunctions.IsBoolLike("int", 0));
            Assert.IsTrue(PrefsFunctions.IsBoolLike("int", 1));
            Assert.IsFalse(PrefsFunctions.IsBoolLike("int", 2));
            Assert.IsFalse(PrefsFunctions.IsBoolLike("int", -1));
            Assert.IsFalse(PrefsFunctions.IsBoolLike("bool", 1));
            Assert.IsFalse(PrefsFunctions.IsBoolLike("float", 1f));
            Assert.IsFalse(PrefsFunctions.IsBoolLike("string", "1"));
        }

        [Test]
        public void SetPlayerPref_BoolIsStoredAsAnIntZeroOrOne()
        {
            var key = "KwConfigSkillsPrefs_" + Guid.NewGuid().ToString("N");
            try
            {
                foreach (var (text, expected) in new[] { ("true", 1), ("YES", 1), ("1", 1), ("false", 0), ("no", 0), ("0", 0) })
                {
                    var answer = Ok("set_player_pref", "key", key, "value", text, "type", "bool");
                    Assert.AreEqual("int", (string)answer["data"]["type"], text);
                    Assert.AreEqual(expected, PlayerPrefs.GetInt(key, -1), text);
                }

                Assert.AreEqual("INVALID_VALUE", Code("set_player_pref", "key", key, "value", "maybe", "type", "bool"));
                Assert.IsFalse(EditorPrefs.HasKey(key), "a player pref was written to EditorPrefs");
            }
            finally
            {
                PlayerPrefs.DeleteKey(key);
                EditorPrefs.DeleteKey(key);
            }
        }
    }
}
