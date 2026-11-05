// Copyright (C) KitWright. Licensed under MIT.
using System.IO;
using System.Linq;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    /// <summary>The tools that answer to the Unity CLI's names and parameter shapes, and execute_code_file's entry mode (the CLI's run_script).</summary>
    public sealed class UnityCliParityTests
    {
        private const string Folder = "Assets/__KwParityTests";

        // The Unity CLI names kept because they do something no KitWright tool does; the ones that only repeated a KitWright tool are gone.
        private static readonly string[] CliNames =
        {
            "get_player_settings", "get_runtime_pipeline_settings", "get_tags_layers", "search", "set_animation_curve",
            "set_build_settings", "set_graphics_settings", "set_input_settings", "set_material_properties", "set_navmesh_settings",
            "set_player_settings", "set_quality_settings", "set_runtime_pipeline_settings", "set_tags_layers", "set_time_settings"
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
            Assert.IsNotEmpty((string)Success(UnityCliSettingsFunctions.GetPlayerSettings())["data"]["productName"]);

            Assert.IsTrue((bool)Success(UnityCliSettingsFunctions.SetTimeSettings("{\"timeScale\":2}", dry_run: true))["data"]["dry_run"]);
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
            }
            finally
            {
                Success(UnityCliSettingsFunctions.SetTagsLayers("{\"removeTags\":[\"" + tag + "\"]}", true));
            }
        }

        [Test]
        public void ExecuteCodeFile_RunsAnEntryPoint_AndRefusesWhatItCannotFind()
        {
            var file = Folder + "/Run.cs";
            File.WriteAllText(file, "public static class ParityRun { public static int Add(int a, int b) { return a + b; } }");

            var run = Success(EditorUtilityFunctions.ExecuteCodeFile(file, entry: "ParityRun.Add", args: "[2,3]").Result);
            Assert.AreEqual(5, (int)run["data"]["result"]);
            Assert.IsTrue((bool)Success(EditorUtilityFunctions.ExecuteCodeFile(file, entry: "Add", dry_run: true).Result)["data"]["dry_run"]);
            Error(EditorUtilityFunctions.ExecuteCodeFile(file, dry_run: true).Result, "INVALID_PARAM");
            Error(EditorUtilityFunctions.ExecuteCodeFile(file, entry: "ParityRun.Nope").Result, "ENTRY_NOT_FOUND");
            Error(EditorUtilityFunctions.ExecuteCodeFile(Folder + "/Nope.cs", entry: "Nope").Result, "FILE_NOT_FOUND");
            Assert.IsNull(run["data"]["warnings"], "a clean file reports no warnings, not even the compiler's reference notes");
        }

        [Test]
        public void ExecuteCodeFile_ReportsEntryFailuresWithTheirStack_EvenFromAFaultedTask()
        {
            var file = Folder + "/Fail.cs";
            File.WriteAllText(file,
                "public static class ParityFail {\n" +
                "  public static int Throws() { throw new System.InvalidOperationException(\"sync boom\"); }\n" +
                "  public static async System.Threading.Tasks.Task Faults() { throw new System.ArgumentException(\"task boom\"); }\n" +
                "}");

            foreach (var entry in new[] { "ParityFail.Throws", "ParityFail.Faults" })
            {
                var json = Json(EditorUtilityFunctions.ExecuteCodeFile(file, entry: entry).Result);
                Assert.IsFalse(json.Value<bool>("success"), entry + " must not read as a success: " + json);
                Assert.AreEqual("RUN_FAILED", json.Value<string>("code"), entry);
                Assert.IsNotEmpty(json["data"].Value<string>("type"), entry);
                Assert.IsNotEmpty(json["data"].Value<string>("stack"), entry);
            }
        }

        [Test]
        public void ExecuteCodeFile_EntryTakesAndReturnsUnityObjects()
        {
            var file = Folder + "/Objects.cs";
            File.WriteAllText(file,
                "public static class ParityObjects {\n" +
                "  public static string Name(UnityEngine.GameObject go) => go.name;\n" +
                "  public static UnityEngine.GameObject Same(UnityEngine.GameObject go) => go;\n" +
                "}");
            var go = new GameObject("kw-parity-run-object");
            try
            {
                var id = ObjectIdCodec.GetSerializableId(go);
                Assert.AreEqual("kw-parity-run-object",
                    (string)Success(EditorUtilityFunctions.ExecuteCodeFile(file, entry: "ParityObjects.Name", args: "[\"" + id + "\"]").Result)["data"]["result"]);

                var same = Success(EditorUtilityFunctions.ExecuteCodeFile(file, entry: "ParityObjects.Same", args: "[\"" + id + "\"]").Result)["data"]["result"];
                Assert.AreEqual(id, (string)same["instanceId"]);
                Assert.AreEqual("GameObject", (string)same["type"]);

                Error(EditorUtilityFunctions.ExecuteCodeFile(file, entry: "ParityObjects.Name", args: "[\"999999999:1\"]").Result, "INVALID_ARGUMENT");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ExecuteCodeFile_ReportsEntryWarningsOnTheFileLine_PastItsDefines()
        {
            var file = Folder + "/Warn.cs";
            File.WriteAllText(file,
                "public static class ParityWarn {\n" +
                "  public static int Go() { int unused = 1; return 2; }\n" +
                "}");

            var run = Success(EditorUtilityFunctions.ExecuteCodeFile(file, entry: "ParityWarn.Go", defines: "[\"KW_PARITY_A\"]").Result);
            var warning = run["data"]["warnings"].Single();
            Assert.AreEqual("CS0219", (string)warning["code"]);
            Assert.AreEqual(2, (int)warning["line"], "the prepended #define line is not the file's");
        }

#if KITWRIGHT_AI
        [Test]
        public void NavmeshSettings_PreviewTheAgentSettings()
        {
            var preview = Success(UnityCliNavMeshFunctions.SetNavmeshSettings("{\"agentRadius\":0.6,\"nonsense\":1}", true));
            Assert.AreEqual(1, ((JArray)preview["data"]["applied"]).Count);
            Assert.AreEqual(1, ((JArray)preview["data"]["unknown"]).Count);
        }
#endif

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

        [Test]
        public void AnimationCurve_OmittedTangentsAreLinearByDefault_OrFlat()
        {
            var path = Folder + "/Shape.anim";
            AssetDatabase.CreateAsset(new AnimationClip(), path);
            var binding = EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalPosition.x");
            const string keys = "[{\"time\":0,\"value\":0},{\"time\":1,\"value\":2}]";

            Success(UnityCliAssetFunctions.SetAnimationCurve(path, "Transform", "m_LocalPosition.x", keys));
            var linear = AnimationUtility.GetEditorCurve(AssetDatabase.LoadAssetAtPath<AnimationClip>(path), binding);
            Assert.AreEqual(2f, linear.keys[0].outTangent, 0.0001f);
            Assert.AreEqual(2f, linear.keys[1].inTangent, 0.0001f);

            Success(UnityCliAssetFunctions.SetAnimationCurve(path, "Transform", "m_LocalPosition.x", keys, tangents: "flat"));
            var flat = AnimationUtility.GetEditorCurve(AssetDatabase.LoadAssetAtPath<AnimationClip>(path), binding);
            Assert.AreEqual(0f, flat.keys[0].outTangent, 0.0001f);

            Error(UnityCliAssetFunctions.SetAnimationCurve(path, "Transform", "m_LocalPosition.x", keys, tangents: "auto"), "INVALID_PARAM");
        }
#endif

        [Test]
        public void Search_Answers()
        {
            Assert.IsTrue(Json(UnityCliAssetFunctions.Search("t:Material", 3)).ContainsKey("success"));
            Error(UnityCliAssetFunctions.Search(" "), "INVALID_PARAM");
        }
    }
}
