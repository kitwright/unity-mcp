// Copyright (C) KitWright. All rights reserved.

using System.IO;
using System.Linq;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using static KitWright.Editor.Tests.ToolCall;
#if KITWRIGHT_ANIMATION
using UnityEditor.Animations;
#endif

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The tools that create, copy and delete assets by path: every one used to take a name or a path
    /// that left the folder it was meant for, or replaced an asset that was already there, and reported
    /// success. Each test names a call that must refuse and then checks that nothing was written.
    /// </summary>
    public sealed class AssetCreationSafetyTests
    {
        private const string FolderName = "__KitWrightAssetCreationSafetyTests";
        private const string Folder = "Assets/" + FolderName;

        private static readonly string[] StrayFiles =
        {
            "__KwEscape.anim", "__KwEscape.controller", "__KwDeleteEscape.txt", "__KwMoveEscape.mat"
        };

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        private static string OnDisk(string assetPath) => Path.Combine(ProjectRoot, assetPath);

        [SetUp]
        public void CreateFolder()
        {
            if (AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.CreateFolder("Assets", FolderName);
        }

        [TearDown]
        public void DeleteFolder()
        {
            AssetDatabase.DeleteAsset(Folder);

            foreach (var stray in StrayFiles)
            {
                var path = Path.Combine(ProjectRoot, stray);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static string NewMaterial(string name)
        {
            var path = $"{Folder}/{name}.mat";
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            Assert.IsNotNull(shader, "A test shader is required.");
            AssetDatabase.CreateAsset(new Material(shader), path);
            AssetDatabase.SaveAssets();
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(path), "Setup failed: " + path);
            return path;
        }

        [TestCase("Idle", true)]
        [TestCase("My Clip", true)]
        [TestCase("a.b", true)]
        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase(" ", false)]
        [TestCase(".", false)]
        [TestCase("..", false)]
        [TestCase(".hidden", false)]
        [TestCase("trailing.", false)]
        [TestCase("trailing ", false)]
        [TestCase("a/b", false)]
        [TestCase("a\\b", false)]
        [TestCase("../x", false)]
        public void IsValidAssetName_AcceptsOnlyAFileNameUnityKeepsAsWritten(string name, bool expected)
        {
            Assert.AreEqual(expected, AssetFunctions.IsValidAssetName(name), $"'{name}'");
        }

        [Test]
        public void DeleteAsset_RefusesAPathThatWalksOutOfAssets()
        {
            var stray = Path.Combine(ProjectRoot, "__KwDeleteEscape.txt");
            File.WriteAllText(stray, "keep");

            StringAssert.Contains("INVALID_PATH", AssetFunctions.DeleteAsset("Assets/../__KwDeleteEscape.txt"));
            StringAssert.Contains("INVALID_PATH", AssetFunctions.DeleteAsset(stray));
            Assert.IsTrue(File.Exists(stray), "A file outside Assets must not be touched.");
        }

        [Test]
        public void DeleteAsset_RefusesAMetaFileInsteadOfResettingTheImporter()
        {
            var path = NewMaterial("KwMeta");
            var meta = OnDisk(path + ".meta");
            Assert.IsTrue(File.Exists(meta), "Setup failed: the asset has no .meta.");

            StringAssert.Contains("INVALID_PATH", AssetFunctions.DeleteAsset(path + ".meta"));
            StringAssert.Contains("INVALID_PATH", AssetFunctions.DeleteAsset(path + ".META"));
            Assert.IsTrue(File.Exists(meta));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(path));
        }

        private static void SetColor(string path, Color color)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            material.color = color;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
        }

        [Test]
        public void CopyAsset_RefusesToReplaceAnExistingDestinationUnlessOverwriteIsSet()
        {
            var source = NewMaterial("KwCopySource");
            var destination = NewMaterial("KwCopyDestination");
            SetColor(source, Color.red);
            SetColor(destination, Color.blue);
            var guid = AssetDatabase.AssetPathToGUID(destination);

            StringAssert.Contains("ASSET_EXISTS", AssetFunctions.CopyAsset(source, destination));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(destination),
                "A refused copy must leave the destination, and so every reference to it, alone.");
            Assert.AreEqual(Color.blue, AssetDatabase.LoadAssetAtPath<Material>(destination).color);

            var replaced = AssetFunctions.CopyAsset(source, destination, overwrite: true);

            StringAssert.StartsWith("Copied", replaced);
            Assert.AreEqual(Color.red, AssetDatabase.LoadAssetAtPath<Material>(destination).color,
                "overwrite=true has to leave the source's content at the destination.");
        }

        [TestCase("Assets/a/../b.anim")]
        [TestCase("Assets/a/..")]
        [TestCase("Assets/..")]
        [TestCase("Assets/../x")]
        [TestCase("Assets/a/.. /b.anim")]
        [TestCase("Assets/a/b/../../../x")]
        [TestCase("Assets")]
        [TestCase("Assets/")]
        [TestCase("assets/")]
        [TestCase("Assets\\")]
        [TestCase("Assets//")]
        [TestCase("Assets/.")]
        [TestCase("Assets/ ")]
        [TestCase("Packages")]
        [TestCase("Packages/")]
        [TestCase("Packages/..")]
        [TestCase("")]
        [TestCase("  ")]
        [TestCase(null)]
        [TestCase("ProjectSettings/ProjectVersion.txt")]
        [TestCase("../x")]
        [TestCase("C:/Windows/win.ini")]
        [TestCase("/etc/passwd")]
        [TestCase("Assetsx/a.anim")]
        [TestCase("Assets/.hid/a.anim")]
        [TestCase("Assets/ends~/a.anim")]
        [TestCase("Assets/trail./a.anim")]
        public void TryResolveUnderAssetsOrPackages_RefusesWhatMustNeverReachAssetDatabase(string path)
        {
            var resolved = AssetFunctions.TryResolveUnderAssetsOrPackages(path, out var normalized, out var absolute);

            Assert.IsFalse(resolved, $"'{path}' resolved to '{normalized}'");
            Assert.IsNull(absolute);
        }

        [TestCase("Assets/a/b.anim", "Assets/a/b.anim")]
        [TestCase("assets\\a\\\\b.anim", "Assets/a/b.anim")]
        [TestCase("Assets//a/b.anim/", "Assets/a/b.anim")]
        [TestCase("Assets/a/b.anim.meta.", "Assets/a/b.anim.meta")]
        [TestCase("Assets/a/b.anim.meta/", "Assets/a/b.anim.meta")]
        [TestCase("Assets/a/b.anim.meta ", "Assets/a/b.anim.meta")]
        [TestCase("Assets/My Folder/a.b.anim", "Assets/My Folder/a.b.anim")]
        [TestCase("Packages/com.example.pkg/x.asset", "Packages/com.example.pkg/x.asset")]
        public void TryResolveUnderAssetsOrPackages_ReturnsOneCanonicalSpelling(string path, string expected)
        {
            Assert.IsTrue(AssetFunctions.TryResolveUnderAssetsOrPackages(path, out var normalized, out var absolute), path);

            Assert.AreEqual(expected, normalized);
            Assert.IsNotNull(absolute);
        }

        [Test]
        public void DeleteAsset_RefusesDotDotInsideAssets_AndLeavesTheAssetAlone()
        {
            var path = NewMaterial("KwDotDot");

            StringAssert.Contains("INVALID_PATH", AssetFunctions.DeleteAsset($"{Folder}/Sub/../KwDotDot.mat"));
            StringAssert.Contains("INVALID_PATH", AssetFunctions.DeleteAsset($"{Folder}/../{FolderName}/KwDotDot.mat"));

            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(path));
        }

        [Test]
        public void DeleteAsset_RefusesAMetaFileWhateverTrailsTheName()
        {
            var path = NewMaterial("KwMetaTrail");
            var meta = OnDisk(path + ".meta");

            foreach (var tail in new[] { "/", ".", " ", "//", "\\", "./", ". " })
                StringAssert.Contains("INVALID_PATH", AssetFunctions.DeleteAsset(path + ".meta" + tail), $"'{tail}'");

            Assert.IsTrue(File.Exists(meta));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(path));
        }

        [Test]
        public void CopyAndMoveAsset_RefuseASourceOutsideAssets_AndCreateNoFolder()
        {
            const string outside = "ProjectSettings/__KwNoSuchSource.txt";

            StringAssert.Contains("INVALID_PATH", AssetFunctions.CopyAsset(outside, Folder + "/CopySub/x.txt"));
            StringAssert.Contains("INVALID_PATH", AssetFunctions.CopyAsset("../__KwNoSuchSource.txt", Folder + "/CopySub/x.txt"));
            StringAssert.Contains("INVALID_PATH", AssetFunctions.MoveAsset(outside, Folder + "/MoveSub/x.txt"));
            StringAssert.Contains("INVALID_PATH", AssetFunctions.MoveAsset("../__KwNoSuchSource.txt", Folder + "/MoveSub/x.txt"));

            Assert.IsEmpty(Directory.GetFileSystemEntries(OnDisk(Folder)));
        }

        [Test]
        public void CopyAsset_RefusesADestinationWithDotDot_AndCreatesNoFolder()
        {
            var source = NewMaterial("KwCopyDotDot");

            StringAssert.Contains("INVALID_PATH", AssetFunctions.CopyAsset(source, Folder + "/Sub/../Elsewhere/x.mat"));

            Assert.IsFalse(Directory.Exists(OnDisk(Folder + "/Sub")));
            Assert.IsFalse(Directory.Exists(OnDisk(Folder + "/Elsewhere")));
        }

        [Test]
        public void MoveAsset_RefusesADestinationWithDotDot_AndCreatesNoFolder()
        {
            var source = NewMaterial("KwMoveDotDot");

            StringAssert.Contains("INVALID_PATH", AssetFunctions.MoveAsset(source, Folder + "/Sub/../Elsewhere/x.mat"));

            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(source));
            Assert.IsFalse(Directory.Exists(OnDisk(Folder + "/Sub")));
            Assert.IsFalse(Directory.Exists(OnDisk(Folder + "/Elsewhere")));
        }

        [Test]
        public void CreateMaterial_RefusesASavePathSegmentUnityIgnores_AndCreatesNothing()
        {
            foreach (var savePath in new[] { Folder + "/.hid/", Folder + "/ends~/", Folder + "/Sub/../" })
                StringAssert.Contains("INVALID_PATH",
                    AssetFunctions.CreateMaterial("KwHidden", "1,1,1,1", "Sprites/Default", savePath), savePath);

            Assert.IsEmpty(Directory.GetFileSystemEntries(OnDisk(Folder)));
        }

        [Test]
        public void ImportSettingNumbers_RejectNaNAndInfinityInEveryJsonSpelling()
        {
            var tokens = JObject.Parse(
                "{\"inf\":Infinity,\"negInf\":-Infinity,\"nan\":NaN,\"big\":1e39,\"strInf\":\"Infinity\",\"strNan\":\"NaN\"," +
                "\"strNegInf\":\"-Infinity\",\"ok\":2.5,\"strOk\":\"0.5\",\"int\":3}");

            foreach (var name in new[] { "inf", "negInf", "nan", "big", "strInf", "strNan", "strNegInf" })
            {
                Assert.IsFalse(AssetImportFunctions.TryGetFloat(tokens[name], out _), $"float '{name}'");
                Assert.IsFalse(AssetImportFunctions.TryGetInt(tokens[name], out _), $"int '{name}'");
            }

            Assert.IsTrue(AssetImportFunctions.TryGetFloat(tokens["ok"], out var ok));
            Assert.AreEqual(2.5f, ok);
            Assert.IsTrue(AssetImportFunctions.TryGetFloat(tokens["strOk"], out var strOk));
            Assert.AreEqual(0.5f, strOk);
            Assert.IsTrue(AssetImportFunctions.TryGetFloat(tokens["int"], out var intAsFloat));
            Assert.AreEqual(3f, intAsFloat);
            Assert.IsTrue(AssetImportFunctions.TryGetInt(tokens["int"], out var i));
            Assert.AreEqual(3, i);
        }

        [Test]
        public void CopyAsset_RefusedForItsSource_LeavesNoDestinationFolderBehind()
        {
            var destination = Folder + "/NewSub/Copy.mat";

            StringAssert.Contains("ASSET_NOT_FOUND", AssetFunctions.CopyAsset(Folder + "/NoSuch.mat", destination));

            Assert.IsFalse(Directory.Exists(OnDisk(Folder + "/NewSub")));
        }

        [Test]
        public void CopyAsset_RefusesToCopyOntoItselfEvenWithOverwrite()
        {
            var source = NewMaterial("KwSelfCopy");

            StringAssert.Contains("INVALID_PATH", AssetFunctions.CopyAsset(source, source, overwrite: true));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(source));
        }

        [Test]
        public void MoveAsset_RefusesADestinationOutsideAssets()
        {
            var source = NewMaterial("KwMoveSource");

            StringAssert.Contains("INVALID_PATH", AssetFunctions.MoveAsset(source, "Assets/../../__KwMoveEscape.mat"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(source));
        }

        [TestCase("we.")]
        [TestCase("we ")]
        [TestCase("..")]
        [TestCase(".hidden")]
        public void CreateFolder_RefusesANameUnityWouldSanitise_AndCreatesNothing(string name)
        {
            StringAssert.Contains("INVALID_PATH", AssetFunctions.CreateFolder($"{Folder}/{name}"));

            Assert.IsEmpty(Directory.GetFileSystemEntries(OnDisk(Folder)),
                "Unity creates the folder under a sanitised name even when the call reports failure.");
        }

        [Test]
        public void CreateFolder_RefusesAnEmptySegment_AndStillTakesATrailingSlash()
        {
            StringAssert.Contains("INVALID_PATH", AssetFunctions.CreateFolder($"{Folder}//Oops"));
            Assert.IsEmpty(Directory.GetFileSystemEntries(OnDisk(Folder)));

            Ok("create_folder", "path", Folder + "/Deep/Er/");
            Assert.IsTrue(AssetDatabase.IsValidFolder(Folder + "/Deep/Er"));
        }

        private static string CodeOf(object result) => (string)JObject.FromObject(result)["code"];

        [Test]
        public void SetLightingSettings_RejectsAFlagComboAndNonFiniteValues_BeforeChangingAnything()
        {
            var mode = RenderSettings.ambientMode;
            var density = RenderSettings.fogDensity;

            Assert.AreEqual("INVALID_PARAM", CodeOf(LightingFunctions.SetLightingSettings(ambient_mode: "Trilight,Flat")));
            Assert.AreEqual("INVALID_PARAM", CodeOf(LightingFunctions.SetLightingSettings(ambient_mode: "7")));
            Assert.AreEqual("INVALID_PARAM", CodeOf(LightingFunctions.SetLightingSettings(ambient_light: "NaN,0,0,1")));
            Assert.AreEqual("INVALID_PARAM", CodeOf(LightingFunctions.SetLightingSettings(fog_color: "0,Infinity,0")));
            Assert.AreEqual("INVALID_PARAM", CodeOf(LightingFunctions.SetLightingSettings(fog_density: float.NaN)));
            Assert.AreEqual("INVALID_PARAM", CodeOf(LightingFunctions.SetLightingSettings(fog_density: -5f)));

            Assert.AreEqual(mode, RenderSettings.ambientMode);
            Assert.AreEqual(density, RenderSettings.fogDensity);
        }

        [Test]
        public void SetActiveTool_AcceptsOnlyAnExactToolName_AndNeverReportsCustomAsSet()
        {
            var previous = UnityEditor.Tools.current;
            try
            {
                foreach (var tool in new[] { "Custom", "Move,Rotate", "99" })
                {
                    Assert.AreEqual("INVALID_TOOL", Code("set_active_tool", "tool", tool), tool);
                    Assert.AreEqual(previous, UnityEditor.Tools.current, $"'{tool}' must not change the tool.");
                }

                Ok("set_active_tool", "tool", "rotate");
                Assert.AreEqual(Tool.Rotate, UnityEditor.Tools.current);
            }
            finally
            {
                UnityEditor.Tools.current = previous;
            }
        }

        [Test]
        public void SetAssetImportSettings_RejectsAnEnumThatIsAFlagComboAndKeepsAcceptingAnyCase()
        {
            var path = Folder + "/kw_import.png";
            Ok("create_texture", "path", path, "width", "8", "height", "8");

            Assert.AreEqual("INVALID_SETTINGS",
                Code("set_asset_import_settings", "path", path, "settings_json", "{\"wrapMode\":\"Clamp, Mirror\"}"));
            Assert.AreEqual("INVALID_SETTINGS",
                Code("set_asset_import_settings", "path", path, "settings_json", "{\"filterMode\":\"Point, Bilinear\"}"));

            Ok("set_asset_import_settings", "path", path, "settings_json", "{\"wrapMode\":\"clamp\"}");
            Assert.AreEqual(TextureWrapMode.Clamp, ((TextureImporter)AssetImporter.GetAtPath(path)).wrapMode);
        }

#if KITWRIGHT_ANIMATION
        private const string ControllerPath = Folder + "/Ctrl.controller";
        private const string ClipPath = Folder + "/Clip.anim";

        private static void MakeControllerAndClip()
        {
            AnimationFunctions.CreateAnimatorController("Ctrl", Folder + "/");
            AnimationFunctions.CreateAnimationClip("Clip", Folder + "/");
        }

        private static AnimatorController Controller() =>
            AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);

        private static AnimatorStateMachine StateMachine() => Controller().layers[0].stateMachine;

        private static AnimatorState State(string name) =>
            StateMachine().states.Single(s => s.state.name == name).state;

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("../../__KwEscape")]
        [TestCase("a/b")]
        [TestCase("..")]
        [TestCase("trailing.")]
        public void CreateAnimationClipAndController_RefuseAnInvalidName_AndWriteNothing(string name)
        {
            StringAssert.Contains("INVALID_NAME", AnimationFunctions.CreateAnimationClip(name, Folder + "/"));
            StringAssert.Contains("INVALID_NAME", AnimationFunctions.CreateAnimatorController(name, Folder + "/"));

            Assert.IsEmpty(Directory.GetFileSystemEntries(OnDisk(Folder)));
            Assert.IsFalse(File.Exists(Path.Combine(ProjectRoot, "__KwEscape.anim")));
            Assert.IsFalse(File.Exists(Path.Combine(ProjectRoot, "__KwEscape.controller")));
        }

        [Test]
        public void CreateAnimationClipAndController_RefuseASavePathOutsideAssets()
        {
            StringAssert.Contains("INVALID_PATH", AnimationFunctions.CreateAnimationClip("Clip", "Assets/../../Elsewhere/"));
            StringAssert.Contains("INVALID_PATH", AnimationFunctions.CreateAnimatorController("Ctrl", "Assets/../../Elsewhere/"));
        }

        [TestCase("/.hid/")]
        [TestCase("/ends~/")]
        [TestCase("/trail./")]
        [TestCase("/Sub/../")]
        public void CreateAnimationClipAndController_RefuseASavePathSegmentUnityIgnoresOrRenames_AndCreateNothing(string tail)
        {
            StringAssert.Contains("INVALID_PATH", AnimationFunctions.CreateAnimationClip("Clip", Folder + tail));
            StringAssert.Contains("INVALID_PATH", AnimationFunctions.CreateAnimatorController("Ctrl", Folder + tail));

            Assert.IsEmpty(Directory.GetFileSystemEntries(OnDisk(Folder)),
                "A folder left behind is one Unity does not show.");
        }

        [Test]
        public void CreateAnimationClipAndController_ReplyWithTheCanonicalAssetPath()
        {
            Assert.AreEqual($"Created Animation Clip at {Folder}/Canon.anim",
                AnimationFunctions.CreateAnimationClip("Canon", Folder + "//"));
            Assert.AreEqual($"Created Animator Controller at {Folder}/Canon.controller",
                AnimationFunctions.CreateAnimatorController("Canon", Folder.Replace('/', '\\') + "\\"));
        }

        [Test]
        public void CreateAnimationClipAndController_TakeASavePathWithNoTrailingSlash()
        {
            AnimationFunctions.CreateAnimatorController("KwCtl", Folder);
            AnimationFunctions.CreateAnimationClip("KwClip", Folder);

            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<AnimatorController>($"{Folder}/KwCtl.controller"),
                "The name was glued onto the folder instead of joined to it.");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<AnimationClip>($"{Folder}/KwClip.anim"));
        }

        [Test]
        public void CreateAnimationClip_RefusesToReplaceAClipThatHasCurves()
        {
            MakeControllerAndClip();
            AnimationFunctions.SetClipCurve(ClipPath, "m_LocalPosition.x",
                "[{\"time\":0,\"value\":0},{\"time\":1,\"value\":1}]");
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
            Assert.AreEqual(1, AnimationUtility.GetCurveBindings(clip).Length, "Setup failed: no curve was written.");

            StringAssert.Contains("ANIMATION_CLIP_EXISTS", AnimationFunctions.CreateAnimationClip("Clip", Folder + "/"));

            clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
            Assert.AreEqual(1, AnimationUtility.GetCurveBindings(clip).Length,
                "A refused create must not reset the clip it found.");

            StringAssert.DoesNotContain("ANIMATION_CLIP_EXISTS",
                AnimationFunctions.CreateAnimationClip("Clip", Folder + "/", overwrite: true));
        }

        [Test]
        public void CreateAnimatorController_RefusesToReplaceAControllerThatHasParameters()
        {
            MakeControllerAndClip();
            AnimationFunctions.AddAnimatorParameter(ControllerPath, "Speed", "float");

            StringAssert.Contains("ANIMATOR_CONTROLLER_EXISTS", AnimationFunctions.CreateAnimatorController("Ctrl", Folder + "/"));

            Assert.AreEqual(1, Controller().parameters.Length, "A refused create must not reset the controller it found.");
        }

        [Test]
        public void AddAnimatorParameter_RejectsATypeThatIsNotAMemberName()
        {
            MakeControllerAndClip();

            foreach (var type in new[] { "99", "1", "Float, Int", "" })
                StringAssert.Contains("UNKNOWN_PARAMETER_TYPE", AnimationFunctions.AddAnimatorParameter(ControllerPath, "P", type));

            Assert.IsEmpty(Controller().parameters);
        }

        [Test]
        public void AddAnimatorParameter_RejectsAnEmptyNameAndANonFiniteDefault()
        {
            MakeControllerAndClip();

            StringAssert.Contains("INVALID_PARAM", AnimationFunctions.AddAnimatorParameter(ControllerPath, "", "float"));
            StringAssert.Contains("INVALID_PARAM", AnimationFunctions.AddAnimatorParameter(ControllerPath, "  ", "float"));
            StringAssert.Contains("INVALID_DEFAULT_VALUE",
                AnimationFunctions.AddAnimatorParameter(ControllerPath, "Speed", "float", "NaN"));
            StringAssert.Contains("INVALID_DEFAULT_VALUE",
                AnimationFunctions.AddAnimatorParameter(ControllerPath, "Speed", "float", "Infinity"));

            Assert.IsEmpty(Controller().parameters);
        }

        [Test]
        public void AddAnimatorState_RejectsAnEmptyOrDotOnlyName()
        {
            MakeControllerAndClip();

            foreach (var name in new[] { "", " ", ".", ".." })
                StringAssert.Contains("INVALID_PARAM", AnimationFunctions.AddAnimatorState(ControllerPath, name));

            Assert.IsEmpty(StateMachine().states);
        }

        [Test]
        public void AddAnimatorState_RefusesAnyDot_BecauseUnityRewritesItToAnUnderscore()
        {
            MakeControllerAndClip();

            foreach (var name in new[] { "a.b", ".a", "a.", "Base.Idle" })
                StringAssert.Contains("INVALID_PARAM", AnimationFunctions.AddAnimatorState(ControllerPath, name), name);

            Assert.IsEmpty(StateMachine().states);

            StringAssert.DoesNotContain("INVALID_PARAM", AnimationFunctions.AddAnimatorState(ControllerPath, "a_b"));
            Assert.AreEqual("a_b", State("a_b").name);
        }

        [Test]
        public void AddAnimatorTransition_RejectsANegativeDuration()
        {
            MakeControllerAndClip();
            AnimationFunctions.AddAnimatorState(ControllerPath, "Idle");
            AnimationFunctions.AddAnimatorState(ControllerPath, "Run");

            foreach (var duration in new[] { -0.01f, -1f, float.NegativeInfinity })
                StringAssert.Contains("INVALID_PARAM",
                    AnimationFunctions.AddAnimatorTransition(ControllerPath, "Idle", "Run", null, 0, false, duration), duration.ToString());

            Assert.IsEmpty(State("Idle").transitions);

            StringAssert.DoesNotContain("INVALID_PARAM",
                AnimationFunctions.AddAnimatorTransition(ControllerPath, "Idle", "Run", null, 0, false, 0f));
        }

        [Test]
        public void AddAnimatorTransition_RejectsConditionsOfTheWrongShape_AndLeavesNoTransition()
        {
            MakeControllerAndClip();
            AnimationFunctions.AddAnimatorState(ControllerPath, "Idle");
            AnimationFunctions.AddAnimatorState(ControllerPath, "Run");
            AnimationFunctions.AddAnimatorParameter(ControllerPath, "Speed", "float");

            foreach (var conditions in new[]
                     {
                         "[\"Speed\"]",
                         "[{\"parameter\":\"Speed\",\"threshold\":\"abc\"}]",
                         "[{\"parameter\":\"Speed\",\"threshold\":NaN}]",
                         "[{\"parameter\":\"Speed\",\"threshold\":{}}]"
                     })
                StringAssert.Contains("INVALID_PARAM",
                    AnimationFunctions.AddAnimatorTransition(ControllerPath, "Idle", "Run", conditions));

            StringAssert.Contains("INVALID_CONDITIONS_JSON",
                AnimationFunctions.AddAnimatorTransition(ControllerPath, "Idle", "Run", "{\"parameter\":\"Speed\"}"));
            StringAssert.Contains("UNKNOWN_CONDITION_MODE",
                AnimationFunctions.AddAnimatorTransition(ControllerPath, "Idle", "Run",
                    "[{\"parameter\":\"Speed\",\"mode\":\"99\"}]"));
            StringAssert.Contains("UNKNOWN_CONDITION_MODE",
                AnimationFunctions.AddAnimatorTransition(ControllerPath, "Idle", "Run",
                    "[{\"parameter\":\"Speed\",\"mode\":\"Greater, Less\"}]"));

            Assert.IsEmpty(State("Idle").transitions);
        }

        [Test]
        public void AddAnimatorTransition_RejectsANonFiniteDuration()
        {
            MakeControllerAndClip();
            AnimationFunctions.AddAnimatorState(ControllerPath, "Idle");
            AnimationFunctions.AddAnimatorState(ControllerPath, "Run");

            StringAssert.Contains("INVALID_PARAM",
                AnimationFunctions.AddAnimatorTransition(ControllerPath, "Idle", "Run", null, 0, false, float.NaN));
            StringAssert.Contains("INVALID_PARAM",
                AnimationFunctions.AddAnimatorTransition(ControllerPath, "Idle", "Run", null, 0, false, float.PositiveInfinity));

            Assert.IsEmpty(State("Idle").transitions);
        }

        [Test]
        public void SetClipCurve_RejectsKeysItWouldHaveDroppedOrThrownOn_AndWritesNoCurve()
        {
            MakeControllerAndClip();

            foreach (var keys in new[]
                     {
                         "[1,2]",
                         "[{\"time\":0,\"value\":NaN},{\"time\":1,\"value\":1}]",
                         "[{\"time\":Infinity,\"value\":1}]",
                         "[{\"time\":0,\"value\":1},{\"time\":0,\"value\":2}]",
                         "[{\"time\":\"abc\",\"value\":1}]"
                     })
                StringAssert.Contains("INVALID_PARAM", AnimationFunctions.SetClipCurve(ClipPath, "m_LocalPosition.x", keys));

            StringAssert.Contains("INVALID_PARAM", AnimationFunctions.SetClipCurve(ClipPath, "  ",
                "[{\"time\":0,\"value\":0},{\"time\":1,\"value\":1}]"));

            Assert.IsEmpty(AnimationUtility.GetCurveBindings(AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath)));
        }

        [Test]
        public void SetAnimatorParameter_RejectsANonFiniteFloat()
        {
            LogAssert.ignoreFailingMessages = true;
            var subject = new GameObject("KwNonFiniteSubject");
            try
            {
                MakeControllerAndClip();
                AnimationFunctions.AddAnimatorParameter(ControllerPath, "Speed", "float", "0");
                AnimationFunctions.AddAnimatorState(ControllerPath, "Idle", make_default: true);
                Ok("assign_animator", "game_object_name", subject.name, "controller_path", ControllerPath);
                Ok("play_animator_state", "target", subject.name, "state", "Idle");

                foreach (var value in new[] { "NaN", "Infinity", "-Infinity" })
                    Assert.AreEqual("INVALID_FLOAT",
                        Code("set_animator_parameter", "target", subject.name, "parameter", "Speed", "value", value));
            }
            finally
            {
                Object.DestroyImmediate(subject);
                LogAssert.ignoreFailingMessages = false;
            }
        }
#endif
    }
}
