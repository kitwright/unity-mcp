// Copyright (C) KitWright. Licensed under MIT.
using System.Collections;
using System.IO;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    public sealed class PortedUnityToolsTests
    {
        private const string Folder = "Assets/__KwPortedToolsTests";

        [SetUp]
        public void SetUp() => Directory.CreateDirectory(Folder);

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.Refresh();
        }

        private static JObject Json(object result) => JObject.FromObject(result);

        private static void AssertError(object result, string code)
        {
            var json = Json(result);
            Assert.IsFalse(json.Value<bool>("success"), json.ToString());
            Assert.AreEqual(code, json.Value<string>("code"));
        }

        private static JObject AssertSuccess(object result)
        {
            var json = Json(result);
            Assert.IsTrue(json.Value<bool>("success"), json.ToString());
            return json;
        }

#if KITWRIGHT_ANIMATION
        private const string ControllerPath = Folder + "/Ctrl.controller";
        private const string ClipPath = Folder + "/Clip.anim";

        [Test]
        public void AddAnimatorLayer_StoresWeightAndBlending_AndRefusesADuplicate()
        {
            AnimationFunctions.CreateAnimatorController("Ctrl", Folder + "/");

            AssertSuccess(AnimationInspectFunctions.AddAnimatorLayer(ControllerPath, "Upper", "Additive", 0.5f));

            var layers = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(ControllerPath).layers;
            Assert.AreEqual(2, layers.Length);
            Assert.AreEqual(UnityEditor.Animations.AnimatorLayerBlendingMode.Additive, layers[1].blendingMode);
            Assert.AreEqual(0.5f, layers[1].defaultWeight, 0.0001f);
            AssertError(AnimationInspectFunctions.AddAnimatorLayer(ControllerPath, "Upper"), "LAYER_EXISTS");
        }

        [Test]
        public void GetAnimatorController_ListsParametersAndStates()
        {
            AnimationFunctions.CreateAnimatorController("Ctrl", Folder + "/");
            AnimationFunctions.AddAnimatorParameter(ControllerPath, "Speed", "float", "0.5");
            AnimationFunctions.AddAnimatorState(ControllerPath, "Run");

            var data = AssertSuccess(AnimationInspectFunctions.GetAnimatorController(ControllerPath))["data"];

            Assert.AreEqual("Speed", (string)data["parameters"][0]["name"]);
            Assert.AreEqual("Run", (string)data["layers"][0]["states"][0]["name"]);
        }

        [Test]
        public void RemoveAnimationCurve_NeedsConfirm_ThenRemovesTheBinding()
        {
            AnimationFunctions.CreateAnimationClip("Clip", Folder + "/");
            AnimationFunctions.SetClipCurve(ClipPath, "m_LocalPosition.x", "[{\"time\":0,\"value\":0},{\"time\":1,\"value\":2}]");
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
            var binding = EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalPosition.x");

            AssertError(AnimationInspectFunctions.RemoveAnimationCurve(ClipPath, "Transform", "m_LocalPosition.x"), "CONFIRM_REQUIRED");
            Assert.IsNotNull(AnimationUtility.GetEditorCurve(clip, binding));

            var listed = AssertSuccess(AnimationInspectFunctions.GetAnimationClip(ClipPath, true))["data"];
            Assert.AreEqual(1, (int)listed["curveCount"]);
            Assert.AreEqual(2, (int)listed["bindings"][0]["keyCount"]);

            AssertSuccess(AnimationInspectFunctions.RemoveAnimationCurve(ClipPath, "Transform", "m_LocalPosition.x", confirm: true));
            Assert.IsNull(AnimationUtility.GetEditorCurve(clip, binding));
        }
#endif

        [Test]
        public void BakeControl_RefusesUnconfirmedClear_AndCancelWithNothingRunning()
        {
            AssertError(BakeControlFunctions.ClearBakedLighting(), "CONFIRM_REQUIRED");
            AssertError(BakeControlFunctions.ClearOcclusionCulling(), "CONFIRM_REQUIRED");
            AssertError(BakeControlFunctions.CancelLightingBake(), "NO_BAKE_RUNNING");
            AssertError(BakeControlFunctions.CancelOcclusionBake(), "NO_BAKE_RUNNING");
            Assert.AreEqual("idle", (string)AssertSuccess(BakeControlFunctions.OcclusionBakeStatus())["data"]["state"]);
            Assert.AreEqual("idle", (string)AssertSuccess(BakeControlFunctions.LightingBakeStatus())["data"]["state"]);
        }

        [Test]
        public void BakeOcclusionCulling_RejectsABackfaceThresholdOutsideItsRange()
        {
            AssertError(BakeControlFunctions.BakeOcclusionCulling(backface_threshold: 500f), "INVALID_PARAM");
        }

        [Test]
        public void ListBuildTargets_FlagsTheActiveTarget()
        {
            var data = AssertSuccess(BuildInfoFunctions.ListBuildTargets())["data"];

            Assert.AreEqual(EditorUserBuildSettings.activeBuildTarget.ToString(), (string)data["active"]);
            Assert.IsTrue(((JArray)data["targets"]).Count > 0);
        }

        [Test]
        public void WaitFor_MeetsATimesOutAndFailsOnStaticMembers()
        {
            AssertSuccess(WaitFunctions.WaitFor("UnityEngine.Application.isEditor", "equals", "true").Result);
            AssertError(WaitFunctions.WaitFor("UnityEngine.Application.isEditor", "equals", "false", timeout_seconds: 0).Result, "WAIT_TIMEOUT");
            AssertError(WaitFunctions.WaitFor("UnityEngine.Application.noSuchMember", "equals", "1").Result, "WAIT_FAILED");
            AssertError(WaitFunctions.WaitFor("UnityEngine.Application.noSuchMember", "equals", "1", timeout_seconds: 0, tolerate_missing: true).Result, "WAIT_TIMEOUT");
            AssertError(WaitFunctions.WaitStatus("nope"), "WAIT_NOT_FOUND");
        }

        [Test]
        public void Build_ValidatesBeforeQueueing_AndNeedsConfirm()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scenePath = Folder + "/Build.unity";
            Assert.IsTrue(EditorSceneManager.SaveScene(scene, scenePath));

            AssertError(BuildAsyncFunctions.Build(scenes: scenePath, options: "NoSuchOption"), "UNKNOWN_BUILD_OPTION");
            AssertError(BuildAsyncFunctions.Build(target: "nowhere"), "UNKNOWN_TARGET");
            AssertError(BuildAsyncFunctions.Build(scenes: Folder + "/Missing.unity"), "SCENE_NOT_FOUND");
            AssertError(BuildAsyncFunctions.Build(scenes: scenePath, output_path: Path.GetFullPath(".")), "OUTPUT_PATH_REFUSED");
            AssertError(BuildAsyncFunctions.Build(scenes: scenePath), "CONFIRM_REQUIRED");
            AssertSuccess(BuildAsyncFunctions.Build(scenes: scenePath, dry_run: true));
            CollectionAssert.DoesNotContain(new[] { "queued", "building" }, (string)AssertSuccess(BuildAsyncFunctions.BuildStatus())["data"]["status"]);
        }

        // A real player build blocks the editor for a minute or more, so it only runs when asked for by name.
        [UnityTest]
        [Explicit("Builds a Windows player")]
        public IEnumerator Build_RunsAStandalonePlayerBuildToCompletion()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor ||
                !BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
                Assert.Ignore("Needs the Windows Mono build support module on a Windows editor.");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var scenePath = Folder + "/PlayerBuild.unity";
            Assert.IsTrue(EditorSceneManager.SaveScene(scene, scenePath));
            var output = Path.Combine("Builds", "KwBuildProbe", "Probe.exe");

            try
            {
                AssertSuccess(BuildAsyncFunctions.Build("windows64", output, scenePath, "Development,DetailedBuildReport", confirm: true));
                Assert.AreEqual("queued", (string)AssertSuccess(BuildAsyncFunctions.BuildStatus())["data"]["status"]);

                var deadline = EditorApplication.timeSinceStartup + 600;
                string status;
                do
                {
                    yield return null;
                    status = (string)AssertSuccess(BuildAsyncFunctions.BuildStatus())["data"]["status"];
                }
                while (status != "completed" && EditorApplication.timeSinceStartup < deadline);

                var result = AssertSuccess(BuildAsyncFunctions.BuildStatus())["data"]["result"];
                Assert.AreEqual("completed", status);
                Assert.AreEqual("Succeeded", (string)result["result"], result.ToString());
                Assert.IsTrue(File.Exists(output));
            }
            finally
            {
                if (Directory.Exists("Builds")) Directory.Delete("Builds", true);
            }
        }

        [Test]
        public void ListBuildProfiles_AnswersOnEveryUnityVersion()
        {
            Assert.IsTrue(Json(BuildInfoFunctions.ListBuildProfiles()).ContainsKey("success"));
        }

        [Test]
        public void SaveAll_SkipsAnUntitledSceneInsteadOfOpeningItsSaveDialog()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Dirty");
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            var result = AssertSuccess(EditorUtilityFunctions.SaveAll());

            Assert.AreEqual(1, ((JArray)result["data"]["skippedUntitled"]).Count);
            Assert.AreEqual(0, ((JArray)result["data"]["savedScenes"]).Count);
        }

        [Test]
        public void EditorFocus_AnswersWithoutThrowing_WhateverTheEditorHasForAWindow()
        {
            Assert.IsTrue(Json(EditorUtilityFunctions.EditorFocus()).ContainsKey("success"));
        }

        [Test]
        public void ExecuteCodeFile_RefusesAMissingFile_AndAFileThatIsTooLarge()
        {
            AssertError(EditorUtilityFunctions.ExecuteCodeFile(Folder + "/NoSuchSnippet.cs").Result, "FILE_NOT_FOUND");

            var big = Folder + "/Big.cs";
            File.WriteAllText(big, new string('/', 300 * 1024));
            AssertError(EditorUtilityFunctions.ExecuteCodeFile(big).Result, "FILE_TOO_LARGE");
        }

        [UnityTest]
        public IEnumerator ListTests_ListsTheTestsOfThisProject()
        {
            var task = TestListFunctions.ListTests("editmode", "PortedUnityToolsTests", 5);
            while (!task.IsCompleted)
                yield return null;

            var data = AssertSuccess(task.Result)["data"];
            Assert.Greater((int)data["total"], 0);
        }

        [UnityTest]
        public IEnumerator PackageSearch_FindsAPackageInTheLocalCache()
        {
            var task = PackageFunctions.PackageSearch("com.unity.test-framework", true);
            while (!task.IsCompleted)
                yield return null;

            var result = Json(task.Result);
            Assert.IsTrue(result.ContainsKey("success"), result.ToString());
        }

        [UnityTest]
        public IEnumerator WaitCancel_StopsABackgroundWait()
        {
            var started = AssertSuccess(WaitFunctions.WaitFor("UnityEngine.Application.isEditor", "equals", "false", timeout_seconds: 60, background: true).Result);
            var id = (string)started["data"]["wait_id"];

            AssertSuccess(WaitFunctions.WaitCancel(id));

            var deadline = Time.realtimeSinceStartup + 10f;
            string state;
            do
            {
                yield return null;
                state = (string)AssertSuccess(WaitFunctions.WaitStatus(id))["data"]["state"];
            }
            while (state == "waiting" && Time.realtimeSinceStartup < deadline);

            Assert.AreEqual("cancelled", state);
        }

#if KITWRIGHT_AI
        [Test]
        public void NavMeshBakeControl_ReportsIdle_RefusesToCancelNothing_AndReadsTheAgentSettings()
        {
            Assert.AreEqual("idle", (string)AssertSuccess(NavMeshBakeFunctions.NavMeshBakeStatus())["data"]["state"]);
            AssertError(NavMeshBakeFunctions.CancelNavMeshBake(), "NO_BAKE_RUNNING");
            Assert.GreaterOrEqual(((JArray)AssertSuccess(NavMeshBakeFunctions.GetNavMeshSettings())["data"]["agents"]).Count, 1);
        }
#endif

        [Test]
        public void SwitchBuildTargetStatus_IsIdleUntilASwitchIsRequested_ThenCompletedOnTheActiveTarget()
        {
            SessionState.EraseString("KitWright.Build.SwitchRequest");
            Assert.AreEqual("idle", (string)AssertSuccess(BuildAsyncFunctions.SwitchBuildTargetStatus())["data"]["state"]);

            BuildAsyncFunctions.RecordSwitchRequest(EditorUserBuildSettings.activeBuildTarget);

            Assert.AreEqual("completed", (string)AssertSuccess(BuildAsyncFunctions.SwitchBuildTargetStatus())["data"]["state"]);
            SessionState.EraseString("KitWright.Build.SwitchRequest");
        }

        [Test]
        public void CreateAsset_MakesAMaterialAndAScriptableObject_AndRefusesAComponent()
        {
            var material = Folder + "/Wall.mat";

            AssertSuccess(AssetCreateFunctions.CreateAsset(material, "Material"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(material));
            AssertError(AssetCreateFunctions.CreateAsset(material, "Material"), "ASSET_EXISTS");
            AssertSuccess(AssetCreateFunctions.CreateAsset(Folder + "/Clip.anim", "AnimationClip"));
            AssertError(AssetCreateFunctions.CreateAsset(Folder + "/Nope.asset", "Rigidbody"), "NOT_CREATABLE");
        }

        [Test]
        public void AuthoringRoot_ResolvesBarePaths_AndConfinesThePathHelpers_UntilItIsReset()
        {
            AssetDatabase.Refresh();
            try
            {
                AssertError(AuthoringRootFunctions.SetAuthoringRoot("Elsewhere"), "INVALID_ROOT");
                AssertError(AuthoringRootFunctions.SetAuthoringRoot("Assets/DoesNotExist"), "ROOT_NOT_FOUND");

                AssertSuccess(AuthoringRootFunctions.SetAuthoringRoot(Folder));

                Assert.IsTrue(AssetFunctions.TryNormalizeAssetPath("x.asset", out var bare));
                Assert.AreEqual(Folder + "/x.asset", bare);
                Assert.IsFalse(AssetFunctions.TryNormalizeAssetPath("Assets/Outside/x.asset", out _));
                Assert.IsFalse(AssetFunctions.TryNormalizeAssetPath("../x.asset", out _));
                Assert.IsTrue((bool)AssertSuccess(AuthoringRootFunctions.GetAuthoringRoot())["data"]["confined"]);
            }
            finally
            {
                AssertSuccess(AuthoringRootFunctions.SetAuthoringRoot("Assets"));
            }

            Assert.IsFalse(AssetFunctions.TryNormalizeAssetPath("x.asset", out _));
            Assert.IsTrue(AssetFunctions.TryNormalizeAssetPath("Assets/Outside/x.asset", out _));
        }

        [Test]
        public void ReportEvals_AggregatesWhateverTheSessionHasRun()
        {
            Assert.IsTrue(Json(EvalReportFunctions.ReportEvals(10)).Value<bool>("success"));
        }

        [Test]
        public void PackageResolve_StartsAndPackageStatusTracksIt()
        {
            AssertSuccess(PackageFunctions.PackageResolve());

            Assert.Contains((string)AssertSuccess(PackageFunctions.PackageStatus())["data"]["status"], new[] { "in_progress", "completed" });
        }

        [Test]
        public void ReloadFile_RefusesAPathOutsideTheProject_AndAnUnusableFileWithoutThrowingAway()
        {
            Assert.Throws<KitWright.Editor.Tools.Helpers.PathOutsideProjectException>(() => CodeReloadFunctions.ReloadFile("../../outside.cs"));
            AssertError(CodeReloadFunctions.ReloadFile(" "), "INVALID_PARAM");

            Assert.IsFalse(Json(CodeReloadFunctions.ReloadFile(Folder + "/NoSuchFile.cs")).Value<bool>("success"));
            Assert.IsFalse(Json(CodeReloadFunctions.ReloadFileEditorInterpreter(Folder + "/NoSuchFile.cs")).Value<bool>("success"));
            Assert.IsFalse(Json(CodeReloadFunctions.ReloadFilePlayerInterpreter(Folder + "/NoSuchFile.cs")).Value<bool>("success"));
        }

        [Test]
        public void SetAutotick_RefusesAnIntervalOutOfRange_AndTurnsOffAgain()
        {
            AssertError(EditorTickFunctions.SetAutotick(true, -1), "INVALID_PARAM");

            Assert.IsTrue((bool)AssertSuccess(EditorTickFunctions.SetAutotick(true, 50, false))["data"]["enabled"]);
            Assert.IsFalse((bool)AssertSuccess(EditorTickFunctions.SetAutotick(false, 16, false))["data"]["enabled"]);
        }

        [Test]
        public void AttachScript_RefusesAMissingScript()
        {
            var go = new GameObject("KwAttachProbe");
            try
            {
                AssertError(AttachScriptFunctions.AttachScript(go.GetInstanceID().ToString(), "Assets/NoSuchScript.cs"), "SCRIPT_NOT_FOUND");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ImportAsset_CopiesTheFile_AndRefusesToReplaceItWithoutConfirm()
        {
            var source = Path.Combine(Path.GetTempPath(), "kw_import_source.txt");
            File.WriteAllText(source, "hello");
            try
            {
                var destination = Folder + "/imported.txt";

                AssertSuccess(EditorUtilityFunctions.ImportAsset(source, destination));
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<TextAsset>(destination));
                AssertError(EditorUtilityFunctions.ImportAsset(source, destination), "ASSET_EXISTS");
                AssertSuccess(EditorUtilityFunctions.ImportAsset(source, destination, confirm: true));
            }
            finally
            {
                File.Delete(source);
            }
        }

        [Test]
        public void SavePrefabContents_RenamesAChild_AndLeavesThePrefabAloneWhenAChildIsMissing()
        {
            const string prefabPath = Folder + "/Thing.prefab";
            var root = new GameObject("Thing");
            new GameObject("Body").transform.SetParent(root.transform);
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);

            AssertError(EditorUtilityFunctions.SavePrefabContents(prefabPath, "Body", "Torso", "Missing"), "CHILD_NOT_FOUND");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).transform.Find("Body"));

            AssertSuccess(EditorUtilityFunctions.SavePrefabContents(prefabPath, "Body", "Torso", "Body", false));
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).transform.Find("Torso");
            Assert.IsNotNull(saved);
            Assert.IsFalse(saved.gameObject.activeSelf);
        }
    }
}
