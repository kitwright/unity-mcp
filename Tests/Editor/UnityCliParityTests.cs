// Copyright (C) KitWright. Licensed under MIT.
using System.Collections;
using System.IO;
using System.Linq;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    /// <summary>The tools that answer to the Unity CLI's names and parameter shapes.</summary>
    public sealed class UnityCliParityTests
    {
        private const string Folder = "Assets/__KwParityTests";

        // Every name the Unity CLI uses that KitWright did not already spell the same way. Not run here, only found: play mode,
        // a recompile, a bake, a menu item or a screenshot cannot run unattended.
        private static readonly string[] CliNames =
        {
            "add_scene_to_build", "bake_lighting", "bake_navmesh", "batch", "cancel_navmesh_bake", "cancel_tests", "clear_navmesh",
            "console", "console_status", "create_gameobject", "create_gameobjects", "create_scene", "delete_gameobject", "editor_pause",
            "editor_play", "editor_status", "editor_stop", "eval", "eval_file", "find_gameobjects", "get_audio_settings",
            "get_graphics_settings", "get_import_settings", "get_input_settings", "get_navmesh_settings", "get_performance_stats",
            "get_physics_settings", "get_player_settings", "get_quality_settings", "get_runtime_pipeline_settings", "get_scene_hierarchy",
            "get_serialized_fields", "get_shader_properties", "get_tags_layers", "get_time_settings", "list_open_scenes", "menu",
            "navmesh_bake_status", "package_add", "package_list", "package_remove", "read_text_file", "recompile", "recompile_status",
            "remove_scene_from_build", "rename_gameobject", "run_script", "screenshot", "search", "set_animation_curve",
            "set_audio_settings", "set_build_settings", "set_graphics_settings", "set_import_settings", "set_input_settings", "set_layer",
            "set_material_properties", "set_navmesh_settings", "set_physics_settings", "set_player_settings", "set_quality_settings",
            "set_runtime_pipeline_settings", "set_tag", "set_tags_layers", "set_time_settings", "test_status", "write_text_file"
        };

        [SetUp]
        public void SetUp()
        {
            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.Refresh();
        }

        private static JObject Json(object result) => JObject.FromObject(result);

        private static JObject Success(object result)
        {
            var json = Json(result);
            Assert.IsTrue(json.Value<bool>("success"), json.ToString());
            return json;
        }

        private static void Error(object result, string code)
        {
            var json = Json(result);
            Assert.IsFalse(json.Value<bool>("success"), json.ToString());
            Assert.AreEqual(code, json.Value<string>("code"));
        }

        [Test]
        public void EveryCliNameIsARegisteredTool()
        {
            var missing = CliNames.Where(n => ToolRegistry.GetMethod(n) == null).ToArray();

            Assert.IsEmpty(missing, string.Join(", ", missing));
        }

        [Test]
        public void SettingsTools_ReadThePagesTheyNameAndRefuseWithoutConfirm()
        {
            Assert.Less((float)Success(UnityCliSettingsFunctions.GetPhysicsSettings())["data"]["gravityY"], 0f);
            Assert.GreaterOrEqual((float)Success(UnityCliSettingsFunctions.GetAudioSettings())["data"]["volume"], 0f);
            Assert.Greater((float)Success(UnityCliSettingsFunctions.GetTimeSettings())["data"]["fixedDeltaTime"], 0f);
            Assert.IsNotEmpty((string)Success(UnityCliSettingsFunctions.GetPlayerSettings())["data"]["productName"]);
            Assert.Greater(((JArray)Success(UnityCliSettingsFunctions.GetQualitySettings())["data"]["levelNames"]).Count, 0);
            Assert.IsTrue(Json(UnityCliSettingsFunctions.GetGraphicsSettings()).Value<bool>("success"));
            Assert.Greater((int)Success(UnityCliSettingsFunctions.GetInputSettings())["data"]["count"], 0);

            Error(UnityCliSettingsFunctions.SetPhysicsSettings("{\"gravityY\":-5}"), "CONFIRM_REQUIRED");
            Error(UnityCliSettingsFunctions.SetPhysicsSettings("{\"nonsense\":1}", true), "UNKNOWN_SETTING");
            Assert.IsTrue((bool)Success(UnityCliSettingsFunctions.SetTimeSettings("{\"timeScale\":2}", dry_run: true))["data"]["dry_run"]);
            Assert.IsTrue((bool)Success(UnityCliSettingsFunctions.SetAudioSettings("{\"volume\":0.5}", dry_run: true))["data"]["dry_run"]);
            Error(UnityCliSettingsFunctions.SetQualitySettings("{\"level\":999}", true), "INVALID_PARAM");
            Error(UnityCliSettingsFunctions.SetPlayerSettings("{\"scriptingBackend\":\"Nope\"}", true), "INVALID_PARAM");
            Error(UnityCliSettingsFunctions.SetInputSettings("{\"axis\":\"NoSuchAxis\",\"dead\":0.2}", true), "AXIS_NOT_FOUND");
            Assert.IsTrue((bool)Success(UnityCliSettingsFunctions.SetGraphicsSettings("{\"renderPipelineAsset\":null}", dry_run: true))["data"]["dry_run"]);
            Assert.IsTrue((bool)Success(UnityCliSettingsFunctions.SetBuildSettings("{\"developmentBuild\":true}", dry_run: true))["data"]["dry_run"]);
            Assert.IsTrue(Json(UnityCliSettingsFunctions.GetRuntimePipelineSettings()).ContainsKey("success"));
            Assert.IsTrue(Json(UnityCliSettingsFunctions.SetRuntimePipelineSettings("{\"port\":0}", dry_run: true)).ContainsKey("success"));
        }

        [Test]
        public void TagsAndLayers_AreAddedAndRemovedThroughTheTypedTool()
        {
            const string tag = "KwParityTag";

            Assert.Contains("Untagged", ((JArray)Success(UnityCliSettingsFunctions.GetTagsLayers())["data"]["tags"]).Select(t => (string)t).ToList());
            Error(UnityCliSettingsFunctions.SetTagsLayers("{\"setLayers\":[{\"index\":3,\"name\":\"X\"}]}", true), "INVALID_PARAM");
            Error(UnityCliSettingsFunctions.SetTagsLayers("{\"addTags\":[\"" + tag + "\"]}"), "CONFIRM_REQUIRED");

            try
            {
                Success(UnityCliSettingsFunctions.SetTagsLayers("{\"addTags\":[\"" + tag + "\"]}", true));
                Assert.Contains(tag, ((JArray)Success(UnityCliSettingsFunctions.GetTagsLayers())["data"]["tags"]).Select(t => (string)t).ToList());

                var go = new GameObject("KwTagProbe");
                try
                {
                    Success(UnityCliSettingsFunctions.SetTag(go.GetInstanceID().ToString(), tag));
                    Success(UnityCliSettingsFunctions.SetLayer(go.GetInstanceID().ToString(), "Default"));
                    Assert.AreEqual(tag, go.tag);
                }
                finally
                {
                    Object.DestroyImmediate(go);
                }
            }
            finally
            {
                Success(UnityCliSettingsFunctions.SetTagsLayers("{\"removeTags\":[\"" + tag + "\"]}", true));
            }
        }

        [Test]
        public void GameObjectTools_CreateFindRenameAndDeleteTogether()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var created = Success(UnityCliEditorFunctions.CreateGameobjects(3, "Probe", null, "cube", "[[0,0,0],[1,0,0],[2,0,0]]"));
            Assert.AreEqual(3, ((JArray)created["data"]["created"]).Count);
            Error(UnityCliEditorFunctions.CreateGameobjects(2, positions: "[[0,0,0]]"), "INVALID_PARAM");
            Error(UnityCliEditorFunctions.CreateGameobjects(1, primitive: "teapot"), "UNKNOWN_PRIMITIVE");

            var found = Success(UnityCliEditorFunctions.FindGameobjects(name: "Probe2"));
            Assert.AreEqual(1, (int)found["data"]["count"]);
            var id = (string)found["data"]["objects"][0]["instanceId"];

            Success(UnityCliEditorFunctions.RenameGameobject(id, "Renamed"));
            Assert.AreEqual(1, (int)Success(UnityCliEditorFunctions.FindGameobjects(hierarchy_path: "/Renamed", type: "MeshFilter"))["data"]["count"]);
            Assert.AreEqual(3, ((JArray)Success(UnityCliEditorFunctions.GetSceneHierarchy())["data"]["roots"]).Count);
            Assert.AreEqual(1, ((JArray)Success(UnityCliEditorFunctions.ListOpenScenes())["data"]["scenes"]).Count);

            Success(UnityCliEditorFunctions.CreateGameobject("Empty"));
            Success(UnityCliEditorFunctions.CreateGameobject(primitive: "sphere"));
            Success(UnityCliEditorFunctions.DeleteGameobject(id));
            Assert.AreEqual(0, (int)Success(UnityCliEditorFunctions.FindGameobjects(name: "Renamed"))["data"]["count"]);
        }

        [Test]
        public void SceneTools_CreateASceneAndListItInTheBuildSettingsIdempotently()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var before = EditorBuildSettings.scenes;
            try
            {
                Success(UnityCliEditorFunctions.CreateScene(Folder + "/Parity"));
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(Folder + "/Parity.unity"));
                Error(UnityCliEditorFunctions.CreateScene(Folder + "/Parity"), "SCENE_EXISTS");

                Success(UnityCliEditorFunctions.AddSceneToBuild(Folder + "/Parity.unity"));
                Success(UnityCliEditorFunctions.AddSceneToBuild(Folder + "/Parity.unity", false));
                Assert.AreEqual(1, EditorBuildSettings.scenes.Count(s => s.path == Folder + "/Parity.unity"));
                Success(UnityCliEditorFunctions.RemoveSceneFromBuild(Folder + "/Parity"));
                Assert.AreEqual(0, EditorBuildSettings.scenes.Count(s => s.path == Folder + "/Parity.unity"));
                Error(UnityCliEditorFunctions.AddSceneToBuild(Folder + "/Missing"), "SCENE_NOT_FOUND");
            }
            finally
            {
                EditorBuildSettings.scenes = before;
            }
        }

        [Test]
        public void ScriptTools_RunAnEntryPoint_AndRefuseWhatTheyCannotFind()
        {
            var file = Folder + "/Run.cs";
            File.WriteAllText(file, "public static class ParityRun { public static int Add(int a, int b) { return a + b; } }");

            var run = Success(UnityCliEditorFunctions.RunScript(file, "ParityRun.Add", "[2,3]").Result);
            Assert.AreEqual(5, (int)run["data"]["result"]);
            Assert.IsTrue((bool)Success(UnityCliEditorFunctions.RunScript(file, dry_run: true).Result)["data"]["dry_run"]);
            Error(UnityCliEditorFunctions.RunScript(file, "ParityRun.Nope").Result, "ENTRY_NOT_FOUND");
            Error(UnityCliEditorFunctions.RunScript(Folder + "/Nope.cs").Result, "FILE_NOT_FOUND");
            Error(UnityCliEditorFunctions.EvalFile(Folder + "/Nope.cs").Result, "FILE_NOT_FOUND");
        }

        [UnityTest]
        public IEnumerator Eval_RunsASnippet_AndBatchRunsRegisteredTools()
        {
            var eval = UnityCliEditorFunctions.Eval("return 6 * 7;");
            while (!eval.IsCompleted)
                yield return null;
            Assert.AreEqual("42", (string)Success(eval.Result)["data"]["result"]);

            Error(UnityCliEditorFunctions.Batch("[{\"command\":\"no_such_tool\"}]").Result, "UNKNOWN_COMMAND");
            Assert.IsTrue((bool)Success(UnityCliEditorFunctions.Batch("[{\"command\":\"get_editor_state\"}]", dry_run: true).Result)["data"]["dry_run"]);

            var batch = UnityCliEditorFunctions.Batch("[{\"id\":\"a\",\"command\":\"get_editor_state\"}]");
            while (!batch.IsCompleted)
                yield return null;
            Assert.IsTrue(Json(batch.Result).ContainsKey("success"));

            var status = UnityCliEditorFunctions.TestStatus();
            while (!status.IsCompleted)
                yield return null;
            Assert.IsTrue(Json(status.Result).ContainsKey("success"));
        }

        [Test]
        public void EditorReadTools_AnswerWithTheirOwnShape()
        {
            Assert.IsTrue(Json(UnityCliEditorFunctions.EditorStatus()).ContainsKey("success"));
            Assert.AreEqual("idle", (string)Success(UnityCliEditorFunctions.RecompileStatus())["data"]["status"]);
            Assert.GreaterOrEqual((int)Success(UnityCliEditorFunctions.ConsoleStatus())["data"]["consoleErrors"], 0);
            Assert.IsNotEmpty(UnityCliEditorFunctions.Console("log", 5) ?? "");
            Assert.IsNotEmpty(UnityCliEditorFunctions.GetPerformanceStats() ?? "");
            Assert.IsTrue(Json(UnityCliEditorFunctions.Menu()).ContainsKey("success"));
            Assert.IsTrue((bool)Success(UnityCliEditorFunctions.BakeLighting(dry_run: true))["data"]["dry_run"]);
            Assert.IsTrue(Json(UnityCliEditorFunctions.CancelTests()).ContainsKey("success"));
        }

#if KITWRIGHT_AI
        [Test]
        public void NavmeshTools_ReadAndPreviewTheAgentSettings()
        {
            Assert.AreEqual("idle", (string)Success(UnityCliNavMeshFunctions.NavmeshBakeStatus())["data"]["state"]);
            Error(UnityCliNavMeshFunctions.CancelNavmeshBake(), "NO_BAKE_RUNNING");
            Assert.GreaterOrEqual(((JArray)Success(UnityCliNavMeshFunctions.GetNavmeshSettings())["data"]["agents"]).Count, 1);

            var preview = Success(UnityCliNavMeshFunctions.SetNavmeshSettings("{\"agentRadius\":0.6,\"nonsense\":1}", true));
            Assert.AreEqual(1, ((JArray)preview["data"]["applied"]).Count);
            Assert.AreEqual(1, ((JArray)preview["data"]["unknown"]).Count);
        }
#endif

        [Test]
        public void TextFiles_AreWrittenGuardedAndReadBack()
        {
            var path = Folder + "/note.txt";

            Assert.IsTrue((bool)Success(UnityCliAssetFunctions.WriteTextFile(path, "hello", dry_run: true))["data"]["dry_run"]);
            Assert.IsFalse(File.Exists(path));
            Success(UnityCliAssetFunctions.WriteTextFile(path, "hello"));
            Error(UnityCliAssetFunctions.WriteTextFile(path, "again"), "FILE_EXISTS");
            Success(UnityCliAssetFunctions.WriteTextFile(path, "again", true));
            Assert.AreEqual("again", (string)Success(UnityCliAssetFunctions.ReadTextFile(path))["data"]["contents"]);
            Error(UnityCliAssetFunctions.ReadTextFile(path, 1), "FILE_TOO_LARGE");
            Error(UnityCliAssetFunctions.ReadTextFile("../outside.txt"), "INVALID_PATH");
        }

        [Test]
        public void MaterialTools_SetTypedPropertiesAndReportTheUnknownOnes()
        {
            var path = Folder + "/Probe.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Sprites/Default")), path);

            var result = Success(UnityCliAssetFunctions.SetMaterialProperties(path, "{\"_Color\":[1,0,0,1],\"_NoSuch\":3}"));
            Assert.AreEqual(1, ((JArray)result["data"]["applied"]).Count);
            Assert.AreEqual(1, ((JArray)result["data"]["unknown"]).Count);
            Assert.AreEqual(Color.red, AssetDatabase.LoadAssetAtPath<Material>(path).GetColor("_Color"));

            Assert.IsTrue((bool)Success(UnityCliAssetFunctions.SetMaterialProperties(path, "{\"_Color\":\"#00FF00\"}", dry_run: true))["data"]["dry_run"]);
            Error(UnityCliAssetFunctions.SetMaterialProperties(path, null, "No/Such/Shader"), "SHADER_NOT_FOUND");
            Assert.Greater(((JObject)Success(UnityCliAssetFunctions.GetShaderProperties(material: path))).Count, 0);
            Assert.IsTrue(Json(UnityCliAssetFunctions.GetShaderProperties("Sprites/Default")).ContainsKey("success"));
        }

        [Test]
        public void SerializedFields_AreReadFromAComponentAndFromAnAsset()
        {
            var go = new GameObject("KwFieldProbe");
            try
            {
                var all = Success(UnityCliAssetFunctions.GetSerializedFields(go.GetInstanceID().ToString(), "Transform"));
                Assert.Greater(((JArray)all["data"]["fields"]).Count, 0);

                var values = Success(UnityCliAssetFunctions.GetSerializedFields(go.GetInstanceID().ToString(), "Transform", "m_LocalPosition", "value"));
                Assert.IsNotNull(values["data"]["fields"]["m_LocalPosition"]);
                Error(UnityCliAssetFunctions.GetSerializedFields(go.GetInstanceID().ToString(), "Transform", "nope"), "FIELD_NOT_FOUND");
                Error(UnityCliAssetFunctions.GetSerializedFields(go.GetInstanceID().ToString()), "COMPONENT_REQUIRED");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ImportSettings_AreReadAndPreviewedForTheDefaultPlatformOnly()
        {
            var path = Folder + "/Tex.png";
            File.WriteAllBytes(path, new Texture2D(4, 4).EncodeToPNG());
            AssetDatabase.ImportAsset(path);

            Assert.IsTrue(Json(UnityCliAssetFunctions.GetImportSettings(path)).ContainsKey("success"));
            Error(UnityCliAssetFunctions.GetImportSettings(path, "Android"), "PLATFORM_UNSUPPORTED");
            Assert.IsTrue((bool)Success(UnityCliAssetFunctions.SetImportSettings(path, "{\"isReadable\":true}", dry_run: true))["data"]["dry_run"]);
            Error(UnityCliAssetFunctions.SetImportSettings(path, "{\"isReadable\":true}", "iOS"), "PLATFORM_UNSUPPORTED");
        }

#if KITWRIGHT_ANIMATION
        [Test]
        public void AnimationCurve_KeepsItsTangents()
        {
            var path = Folder + "/Curve.anim";
            AssetDatabase.CreateAsset(new AnimationClip(), path);

            Success(UnityCliAssetFunctions.SetAnimationCurve(path, "Transform", "m_LocalPosition.x",
                "[{\"time\":0,\"value\":0,\"outTangent\":2},{\"time\":1,\"value\":1,\"inTangent\":3,\"weightedMode\":\"Both\"}]"));

            var curve = AnimationUtility.GetEditorCurve(AssetDatabase.LoadAssetAtPath<AnimationClip>(path),
                EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalPosition.x"));
            Assert.AreEqual(2f, curve.keys[0].outTangent, 0.0001f);
            Assert.AreEqual(3f, curve.keys[1].inTangent, 0.0001f);
            Assert.AreEqual(WeightedMode.Both, curve.keys[1].weightedMode);
            Error(UnityCliAssetFunctions.SetAnimationCurve(path, "Transform", "m_LocalPosition.x", "[]"), "NO_KEYFRAMES");
        }
#endif

        [Test]
        public void SearchAndScreenshot_AreRegisteredAndSearchAnswers()
        {
            Assert.IsTrue(Json(UnityCliAssetFunctions.Search("t:Material", 3)).ContainsKey("success"));
            Error(UnityCliAssetFunctions.Search(" "), "INVALID_PARAM");
        }

        [Test]
        public void PackageTools_PreviewAndRefuseWithoutConfirm()
        {
            Error(UnityCliAssetFunctions.PackageAdd("com.unity.mathematics").Result, "CONFIRM_REQUIRED");
            Assert.AreEqual("com.unity.mathematics", (string)Success(UnityCliAssetFunctions.PackageAdd("com.unity.mathematics", dry_run: true).Result)["data"]["identifier"]);
            Error(UnityCliAssetFunctions.PackageRemove("com.unity.mathematics").Result, "CONFIRM_REQUIRED");
            Error(UnityCliAssetFunctions.PackageRemove("com.kitwright.unity.mcp", true).Result, "PACKAGE_PROTECTED");
            Error(UnityCliAssetFunctions.PackageList("everything").Result, "INVALID_PARAM");
        }

        [UnityTest]
        public IEnumerator PackageList_ListsTheInstalledPackages()
        {
            var task = UnityCliAssetFunctions.PackageList();
            while (!task.IsCompleted)
                yield return null;

            Assert.Greater(((JArray)Success(task.Result)["data"]["installed"]).Count, 0);
        }
    }
}
