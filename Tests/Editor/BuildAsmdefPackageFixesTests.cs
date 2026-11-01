// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KitWright.Editor.Services;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static KitWright.Editor.Tests.ToolCall;
using PackageSource = UnityEditor.PackageManager.PackageSource;

namespace KitWright.Editor.Tests
{
    public sealed class BuildAsmdefPackageFixesTests
    {
        private const string UnityStyleAsmdef = "{\n    \"name\": \"KwH3\",\n    \"references\": [\n        \"GUID:abc\"\n    ]\n}";

        private EditorBuildSettingsScene[] _scenesBefore;
        private readonly List<string> _folders = new List<string>();
        private readonly List<string> _assets = new List<string>();

        [SetUp]
        public void RememberBuildSettings() => _scenesBefore = EditorBuildSettings.scenes;

        [TearDown]
        public void CleanUp()
        {
            if (Describe(EditorBuildSettings.scenes) != Describe(_scenesBefore))
                EditorBuildSettings.scenes = _scenesBefore;

            foreach (var folder in _folders)
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
                if (File.Exists(folder + ".meta"))
                    File.Delete(folder + ".meta");
            }
            _folders.Clear();

            foreach (var asset in _assets)
                AssetDatabase.DeleteAsset(asset);
            _assets.Clear();
        }

        private static string Describe(EditorBuildSettingsScene[] scenes) =>
            string.Join("|", scenes.Select(s => s.path + ":" + s.enabled));

        private string MadeFolder(string parent, string stem)
        {
            var folder = Path.Combine(parent, stem + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            _folders.Add(folder);
            return folder;
        }

        private string AsmdefInTemp(string text, out string projectPath)
        {
            var name = "__KitWrightH3_" + Guid.NewGuid().ToString("N");
            var folder = Path.Combine(ApplicationPaths.ProjectRoot, "Temp", name);
            Directory.CreateDirectory(folder);
            _folders.Add(folder);
            File.WriteAllText(Path.Combine(folder, "A.asmdef"), text);
            projectPath = "Temp/" + name + "/A.asmdef";
            return Path.Combine(folder, "A.asmdef");
        }

        [TestCase("Assets/Scenes/Main.unity", true)]
        [TestCase("Assets\\Scenes\\Main.unity", true)]
        [TestCase("  Assets/Scenes/Main.unity ", true)]
        [TestCase("Packages/com.acme.pack/Scenes/Demo.unity", true)]
        [TestCase("Assets/Scripts/Player.cs", false)]
        [TestCase("Assets/Scenes/Main.unity.meta", false)]
        [TestCase("ProjectSettings/ProjectVersion.txt", false)]
        [TestCase("Assets/../Outside.unity", false)]
        [TestCase("C:\\Builds\\Outside.unity", false)]
        [TestCase("/tmp/Outside.unity", false)]
        [TestCase("Main.unity", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void IsSceneAssetPath_AcceptsOnlyUnityScenesInsideAssetsOrPackages(string path, bool expected) =>
            Assert.AreEqual(expected, BuildFunctions.IsSceneAssetPath(path));

        [Test]
        public void ModifyBuildScenes_RefusesAnExistingFileThatIsNotAProjectSceneAndLeavesTheListAlone()
        {
            var outside = Path.Combine(MadeFolder(Path.GetTempPath(), "KitWrightH3"), "Outside.unity");
            File.WriteAllText(outside, "%YAML 1.1");

            foreach (var scenePath in new[] { outside, "ProjectSettings/ProjectVersion.txt", "Assets/../ProjectSettings/ProjectVersion.txt" })
                Assert.AreEqual("SCENE_NOT_FOUND", Code("modify_build_scenes", "action", "add", "scene_path", scenePath), scenePath);

            Assert.AreEqual(Describe(_scenesBefore), Describe(EditorBuildSettings.scenes));
        }

        [Test]
        public void ClassifySwitch_OnlyCallsItSwitchedWhenTheActiveTargetIsTheRequestedOne()
        {
            Assert.AreEqual(BuildFunctions.SwitchOutcome.Switched,
                BuildFunctions.ClassifySwitch(true, BuildTarget.Android, BuildTarget.Android));
            Assert.AreEqual(BuildFunctions.SwitchOutcome.Queued,
                BuildFunctions.ClassifySwitch(true, BuildTarget.Android, BuildTarget.StandaloneWindows64));
            Assert.AreEqual(BuildFunctions.SwitchOutcome.Failed,
                BuildFunctions.ClassifySwitch(false, BuildTarget.Android, BuildTarget.StandaloneWindows64));
            Assert.AreEqual(BuildFunctions.SwitchOutcome.Failed,
                BuildFunctions.ClassifySwitch(false, BuildTarget.Android, BuildTarget.Android));
        }

        [TestCase("{\n    \"name\": \"A\",\n    \"references\": [\n        \"GUID:abc\"\n    ],\n    \"includePlatforms\": []\n}")]
        [TestCase("{\r\n    \"name\": \"A\",\r\n    \"references\": []\r\n}\r\n")]
        [TestCase("{\n\t\"name\": \"A\",\n\t\"references\": [\n\t\t\"GUID:abc\"\n\t]\n}\n")]
        public void Serialize_WritesAFileBackInItsOwnIndentAndLineEndings(string text) =>
            Assert.AreEqual(text, AssemblyDefinitionFunctions.Serialize(JsonParse.Object(text), text));

        [Test]
        public void Serialize_AChangeTouchesOnlyTheChangedLine()
        {
            var obj = JsonParse.Object(UnityStyleAsmdef);
            obj["name"] = "Renamed";

            Assert.AreEqual(UnityStyleAsmdef.Replace("\"KwH3\"", "\"Renamed\""), AssemblyDefinitionFunctions.Serialize(obj, UnityStyleAsmdef));
        }

        [Test]
        public void Serialize_ANewFileUsesUnitysFourSpaceIndent()
        {
            var text = AssemblyDefinitionFunctions.Serialize(new JObject { ["name"] = "A" }, null);

            StringAssert.Contains("\n    \"name\": \"A\"", text.Replace("\r\n", "\n"));
        }

        private static string Canon(string reference) =>
            reference == "My.Asm" ? "GUID:abc" : reference == "Their.Asm" ? "GUID:def" : reference;

        [Test]
        public void NewReferences_TreatsANameAndTheGuidOfTheSameAssemblyAsOneEntry()
        {
            var current = new JArray("My.Asm");

            var added = AssemblyDefinitionFunctions.NewReferences(current, new[] { "GUID:ABC", "Other", "Other" }, Canon);

            CollectionAssert.AreEqual(new[] { "Other" }, added);
        }

        [Test]
        public void WithoutReferences_RemovesAnEntryWrittenAsANameWhenAskedByGuidAndTheReverse()
        {
            var current = new JArray("My.Asm", "GUID:def", "Keep");

            var kept = AssemblyDefinitionFunctions.WithoutReferences(current, new[] { "GUID:abc", "Their.Asm", "Nope" }, Canon, out var notFound);

            CollectionAssert.AreEqual(new[] { "Keep" }, kept.Select(t => (string)t));
            CollectionAssert.AreEqual(new[] { "Nope" }, notFound);
        }

        [Test]
        public void Canonical_LeavesAGuidAnUnknownNameAndNothingAlone()
        {
            Assert.AreEqual("GUID:abc", AssemblyDefinitionFunctions.Canonical("GUID:abc"));
            Assert.AreEqual("Definitely.Not.An.Assembly", AssemblyDefinitionFunctions.Canonical("Definitely.Not.An.Assembly"));
            Assert.IsNull(AssemblyDefinitionFunctions.Canonical(null));
        }

        [Test]
        public void UnresolvedReferences_ListsOnlyNamesNoAssemblyDefinitionCarries()
        {
            var unresolved = AssemblyDefinitionFunctions.UnresolvedReferences(new[] { "GUID:abc", "Definitely.Not.An.Assembly" });

            CollectionAssert.AreEqual(new[] { "Definitely.Not.An.Assembly" }, unresolved);
        }

        [Test]
        public void CreateAssemblyDef_RefusesBothPlatformListsAndAnExistingNameBeforeWritingAnything()
        {
            var folder = "__KwH3_" + Guid.NewGuid().ToString("N");
            var absolute = Path.Combine(ApplicationPaths.ProjectRoot, "Assets", folder);
            _folders.Add(absolute);
            var path = "Assets/" + folder + "/Made.asmdef";

            Assert.AreEqual("INVALID_PARAM",
                Code("create_assembly_def", "path", path, "include_platforms", "Editor", "exclude_platforms", "Android"));
            Assert.AreEqual("ASMDEF_NAME_EXISTS",
                Code("create_assembly_def", "path", path, "name", "KitWright.Editor.Tests"));
            Assert.IsFalse(Directory.Exists(absolute));
        }

        [Test]
        public void UpdateAssemblyDefSettings_WithNothingToChangeLeavesTheFileAsItWas()
        {
            var file = AsmdefInTemp(UnityStyleAsmdef, out var projectPath);

            Assert.AreEqual("NO_PARAMS", Code("update_assembly_def_settings", "path", projectPath));
            Assert.AreEqual(0, ((JArray)Ok("update_assembly_def_settings", "path", projectPath, "name", "KwH3")["data"]["changed"]).Count);
            Assert.AreEqual(UnityStyleAsmdef, File.ReadAllText(file));
        }

        [Test]
        public void UpdateAssemblyDefSettings_RefusesANameAnotherAssemblyDefinitionCarries()
        {
            var file = AsmdefInTemp(UnityStyleAsmdef, out var projectPath);

            Assert.AreEqual("ASMDEF_NAME_EXISTS",
                Code("update_assembly_def_settings", "path", projectPath, "name", "KitWright.Editor.Tests"));
            Assert.AreEqual(UnityStyleAsmdef, File.ReadAllText(file));
        }

        [Test]
        public void NameTakenRefusal_SkipsOnlyTheFileThatAlreadyCarriesTheName()
        {
            const string taken = "KitWright.Editor.Tests";
            var existing = UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(taken);
            Assume.That(existing, Is.Not.Empty, "The test assembly's own definition is not known to the compilation pipeline.");
            var own = Path.GetFullPath(Path.Combine(ApplicationPaths.ProjectRoot, existing));
            var other = Path.GetFullPath(Path.Combine(ApplicationPaths.ProjectRoot, "Temp", "Other.asmdef"));

            Assert.IsNull(AssemblyDefinitionFunctions.NameTakenRefusal(taken, own));
            Assert.AreEqual("ASMDEF_NAME_EXISTS", (string)JObject.FromObject(AssemblyDefinitionFunctions.NameTakenRefusal(taken, other))["code"]);
            Assert.AreEqual("ASMDEF_NAME_EXISTS", (string)JObject.FromObject(AssemblyDefinitionFunctions.NameTakenRefusal(taken, null))["code"]);
            Assert.IsNull(AssemblyDefinitionFunctions.NameTakenRefusal("Definitely.Not.An.Assembly", other));
        }

        [Test]
        public void SetAssemblyPlatforms_RefusesBothListsInsteadOfKeepingOnlyTheExcludes()
        {
            var file = AsmdefInTemp(UnityStyleAsmdef, out var projectPath);

            Assert.AreEqual("INVALID_PARAM",
                Code("set_assembly_platforms", "path", projectPath, "include_platforms", "Editor", "exclude_platforms", "Android"));
            Assert.AreEqual(UnityStyleAsmdef, File.ReadAllText(file));
        }

        [Test]
        public void AddAssemblyReferences_WithNoReferenceInTheListAnswersNoParamsAndLeavesTheFileAlone()
        {
            var file = AsmdefInTemp(UnityStyleAsmdef, out var projectPath);

            Assert.AreEqual("NO_PARAMS", Code("add_assembly_references", "path", projectPath, "references", ", ,"));
            Assert.AreEqual(UnityStyleAsmdef, File.ReadAllText(file));
        }

        [Test]
        public void RemoveAssemblyReferences_WithNoReferenceInTheListAnswersNoParamsAndLeavesTheFileAlone()
        {
            var file = AsmdefInTemp(UnityStyleAsmdef, out var projectPath);

            Assert.AreEqual("NO_PARAMS", Code("remove_assembly_references", "path", projectPath, "references", ", ,"));
            Assert.AreEqual(UnityStyleAsmdef, File.ReadAllText(file));
        }

        [Test]
        public void AddAndRemoveAssemblyReferences_WithNothingToDoLeaveTheFileAsItWas()
        {
            var file = AsmdefInTemp(UnityStyleAsmdef, out var projectPath);

            var added = Ok("add_assembly_references", "path", projectPath, "references", "GUID:ABC");
            Assert.AreEqual(0, ((JArray)added["data"]["added"]).Count);

            var removed = Ok("remove_assembly_references", "path", projectPath, "references", "Not.There");
            Assert.AreEqual(0, (int)removed["data"]["removed"]);
            CollectionAssert.AreEqual(new[] { "Not.There" }, removed["data"]["notFound"].Select(t => (string)t));

            Assert.AreEqual(UnityStyleAsmdef, File.ReadAllText(file));
        }

        [Test]
        public void IsEditableLocation_OnlyAnEmbeddedPackageOrAnAssetsPathCanBeEdited()
        {
            const string packageAsmdef = "Packages/com.acme.pack/Runtime/A.asmdef";

            Assert.IsTrue(AssemblyDefinitionFunctions.IsEditableLocation("Assets/Scripts/A.asmdef", null));
            Assert.IsTrue(AssemblyDefinitionFunctions.IsEditableLocation(packageAsmdef, PackageSource.Embedded));
            Assert.IsTrue(AssemblyDefinitionFunctions.IsEditableLocation("packages\\com.acme.pack\\A.asmdef", PackageSource.Embedded));
            foreach (var outside in new PackageSource?[] { PackageSource.Local, PackageSource.Registry, PackageSource.Git, PackageSource.BuiltIn, null })
                Assert.IsFalse(AssemblyDefinitionFunctions.IsEditableLocation(packageAsmdef, outside), outside?.ToString() ?? "no package info");
            Assert.IsFalse(AssemblyDefinitionFunctions.IsEditableLocation("Packages\\com.acme.pack\\A.asmdef", PackageSource.Local));
        }

        [Test]
        public void NotEmbeddedHint_SaysSoOnlyForAPackageKnownToLiveOutsideTheProject()
        {
            const string packageAsmdef = "Packages/com.acme.pack/Runtime/A.asmdef";

            StringAssert.Contains("not embedded", AssemblyDefinitionFunctions.NotEmbeddedHint(packageAsmdef, PackageSource.Local));
            StringAssert.Contains("not embedded", AssemblyDefinitionFunctions.NotEmbeddedHint(packageAsmdef, PackageSource.Registry));
            Assert.IsNull(AssemblyDefinitionFunctions.NotEmbeddedHint(packageAsmdef, PackageSource.Embedded));
            Assert.IsNull(AssemblyDefinitionFunctions.NotEmbeddedHint(packageAsmdef, null));
            Assert.IsNull(AssemblyDefinitionFunctions.NotEmbeddedHint("Assets/Scripts/A.asmdef", null));
            Assert.IsNull(AssemblyDefinitionFunctions.NotEmbeddedHint(null, null));
        }

        [Test]
        public void ListAssemblyDefs_MarksEveryEntryEditableOrNot()
        {
            var listed = Ok("list_assembly_defs", "folder", "Assets", "include_packages", "true")["data"]["assemblies"];

            Assert.Greater(listed.Count(), 0);
            foreach (var entry in listed)
            {
                var path = (string)entry["path"];
                Assert.AreEqual(JTokenType.Boolean, entry["editable"].Type, path);
                if (path.StartsWith("Assets/"))
                    Assert.IsTrue((bool)entry["editable"], path);
                if (path.StartsWith("Packages/") &&
                    UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path)?.source == PackageSource.Registry)
                    Assert.IsFalse((bool)entry["editable"], path);
            }
        }

        [Test]
        public void ListManifest_ReturnsTheWholeFileNotTheFirst5000Characters()
        {
            var file = Path.Combine(MadeFolder(Path.GetTempPath(), "KitWrightH3"), "manifest.json");
            var lines = Enumerable.Range(0, 400).Select(i => $"    \"com.example.package{i:D3}\": \"1.0.0\"");
            var content = "{\n  \"dependencies\": {\n" + string.Join(",\n", lines) + "\n  }\n}";
            Assert.Greater(content.Length, 5000);
            File.WriteAllText(file, content);

            var listing = PackageFunctions.ListManifest(file);

            StringAssert.EndsWith(content, listing);
            StringAssert.DoesNotContain("truncated", listing);
        }

        [Test]
        public void ListManifest_ForAMissingFileSaysSoInsteadOfClaimingAListingStarted()
        {
            var answer = JObject.Parse(PackageFunctions.ListManifest(Path.Combine(Path.GetTempPath(), "KitWrightH3_NoSuchManifest.json")));

            Assert.IsFalse((bool)answer["success"]);
            Assert.AreEqual("MANIFEST_NOT_FOUND", (string)answer["code"]);
        }

        [Test]
        public void SetProjectSettings_WhenNoFieldApplies_DoesNotSaveOtherDirtyAssets()
        {
            var path = "Assets/__KwH3Texture_" + Guid.NewGuid().ToString("N") + ".asset";
            var texture = new Texture2D(2, 2);
            AssetDatabase.CreateAsset(texture, path);
            _assets.Add(path);
            EditorUtility.SetDirty(texture);
            Assert.IsTrue(EditorUtility.IsDirty(texture));

            StringAssert.Contains("PROPERTY_SET_FAILED",
                ProjectSettingsFunctions.SetProjectSettings("TimeManager", "{\"m_NoSuchTimeField\": 1}").ToString());

            Assert.IsTrue(EditorUtility.IsDirty(texture), "A write that applied nothing still saved every dirty asset.");
        }
    }
}
