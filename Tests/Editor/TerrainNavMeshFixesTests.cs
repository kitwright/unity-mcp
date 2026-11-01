// Copyright (C) KitWright. All rights reserved.

using System.IO;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
#if KITWRIGHT_TERRAIN
    public sealed class TerrainFixesTests
    {
        private const string FolderName = "__KitWrightTerrainFixesTests";
        private const string Folder = "Assets/" + FolderName;
        private const string Subject = "KwTerrainFixSubject";
        private const string CreatedName = "__KitWrightTerrainFixCreated";
        private const string TextureName = "KwTerrainFixTexture";
        private const string DataPath = Folder + "/KwTerrainFixData.asset";
        private const string TreePath = Folder + "/KwTerrainFixTree.prefab";
        private const string TexturePath = Folder + "/" + TextureName + ".asset";
        private const string LayerAPath = Folder + "/KwTerrainFixLayerA.terrainlayer";
        private const string LayerBPath = Folder + "/KwTerrainFixLayerB.terrainlayer";

        private GameObject subject;
        private string createdAssetPath;
        private Object[] selectionBefore;

        [SetUp]
        public void CreateTerrain()
        {
            // A layer with no diffuse texture and a tree with no LOD group each draw a complaint of their own.
            LogAssert.ignoreFailingMessages = true;
            selectionBefore = Selection.objects;
            createdAssetPath = null;

            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", FolderName);

            var data = new TerrainData { heightmapResolution = 33 };
            data.size = new Vector3(50f, 20f, 50f);
            data.alphamapResolution = 32;
            AssetDatabase.CreateAsset(data, DataPath);
            AssetDatabase.CreateAsset(new TerrainLayer(), LayerAPath);
            AssetDatabase.CreateAsset(new TerrainLayer(), LayerBPath);
            AssetDatabase.CreateAsset(new Texture2D(4, 4) { name = TextureName }, TexturePath);

            var tree = new GameObject("KwTerrainFixTree");
            tree.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            tree.AddComponent<MeshRenderer>();
            PrefabUtility.SaveAsPrefabAsset(tree, TreePath);
            Object.DestroyImmediate(tree);

            subject = new GameObject(Subject);
            subject.AddComponent<Terrain>().terrainData = data;
        }

        [TearDown]
        public void DestroyTerrain()
        {
            LogAssert.ignoreFailingMessages = false;

            if (subject != null)
            {
                // Wrapping a texture writes the new layer next to Assets/, outside the test folder.
                foreach (var layer in subject.GetComponent<Terrain>().terrainData.terrainLayers ?? new TerrainLayer[0])
                {
                    var path = AssetDatabase.GetAssetPath(layer);
                    if (!string.IsNullOrEmpty(path) && !path.StartsWith(Folder))
                        AssetDatabase.DeleteAsset(path);
                }
                Object.DestroyImmediate(subject);
            }
            subject = null;

            var created = GameObject.Find(CreatedName);
            if (created != null)
                Object.DestroyImmediate(created);
            AssetDatabase.DeleteAsset("Assets/" + CreatedName + ".asset");
            if (createdAssetPath != null)
                AssetDatabase.DeleteAsset(createdAssetPath);

            AssetDatabase.DeleteAsset(Folder);
            Selection.objects = selectionBefore;
        }

        private TerrainData Data() => subject.GetComponent<Terrain>().terrainData;

        private static byte[] OnDisk() =>
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), DataPath));

        [Test]
        public void EveryWriterSavesTheTerrainDataItChanged()
        {
            void Writes(string tool, params string[] pairs)
            {
                var before = OnDisk();
                Ok(tool, pairs);
                CollectionAssert.AreNotEqual(before, OnDisk(), $"{tool} left the TerrainData asset on disk unchanged.");
            }

            Writes("flatten_terrain", "target", Subject, "height", "5");
            Writes("adjust_terrain_height", "target", Subject, "delta", "4");
            Writes("add_terrain_layer", "target", Subject, "asset_path", LayerAPath);
            Writes("add_terrain_layer", "target", Subject, "asset_path", LayerBPath);
            Writes("paint_terrain_layer", "target", Subject, "layer_index", "1");
            Writes("add_tree_prototype", "target", Subject, "prefab_path", TreePath);
            Writes("place_terrain_trees", "target", Subject, "prototype_index", "0", "count", "3");
        }

        [Test]
        public void PlaceTreesRefusesACountPastTheCapAndAScaleThatIsNotPositive()
        {
            Ok("add_tree_prototype", "target", Subject, "prefab_path", TreePath);

            var tooMany = (TerrainFunctions.MaxTreesPerCall + 1).ToString();
            Assert.AreEqual("INVALID_COUNT",
                Code("place_terrain_trees", "target", Subject, "prototype_index", "0", "count", tooMany));
            Assert.AreEqual("INVALID_COUNT",
                Code("place_terrain_trees", "target", Subject, "prototype_index", "0", "count", int.MaxValue.ToString()));
            Assert.AreEqual("INVALID_SCALE",
                Code("place_terrain_trees", "target", Subject, "prototype_index", "0", "count", "5", "scale", "0"));
            Assert.AreEqual("INVALID_SCALE",
                Code("place_terrain_trees", "target", Subject, "prototype_index", "0", "count", "5", "scale", "-1"));
            Assert.AreEqual(0, Data().treeInstanceCount, "A refused call must not place anything.");

            Ok("place_terrain_trees", "target", Subject, "prototype_index", "0", "count", "5", "scale", "2");
            Assert.AreEqual(5, Data().treeInstanceCount);
        }

        [Test]
        public void ARegisteredTreePrefabAnswersItsExistingIndexInsteadOfAddingACopy()
        {
            var first = Ok("add_tree_prototype", "target", Subject, "prefab_path", TreePath);
            var second = Ok("add_tree_prototype", "target", Subject, "prefab_path", TreePath);

            Assert.IsTrue((bool)first["data"]["added"]);
            Assert.IsFalse((bool)second["data"]["added"]);
            Assert.AreEqual(0, (int)second["data"]["prototypeIndex"]);
            Assert.AreEqual(1, Data().treePrototypes.Length);
        }

        [Test]
        public void WrappingTheSameTextureTwiceAddsOneLayerAndABadTileSizeAddsNone()
        {
            Assert.AreEqual("INVALID_TILE_SIZE",
                Code("add_terrain_layer", "target", Subject, "asset_path", TexturePath, "tile_size", "x,y"));
            Assert.AreEqual("INVALID_TILE_SIZE",
                Code("add_terrain_layer", "target", Subject, "asset_path", TexturePath, "tile_size", "0,5"));
            Assert.AreEqual(0, Data().terrainLayers.Length, "A refused tile size must not add a layer.");

            Ok("add_terrain_layer", "target", Subject, "asset_path", TexturePath, "tile_size", "4,6");
            Assert.AreEqual(new Vector2(4f, 6f), Data().terrainLayers[0].tileSize);

            Assert.AreEqual("LAYER_ALREADY_ADDED",
                Code("add_terrain_layer", "target", Subject, "asset_path", TexturePath));
            Assert.AreEqual(1, Data().terrainLayers.Length);
        }

        [Test]
        public void FlattenReportsTheHeightThatWasAppliedWhenItHadToClamp()
        {
            var reply = Ok("flatten_terrain", "target", Subject, "height", "1000");

            Assert.AreEqual(20f, (float)reply["data"]["height"], 0.001f);
            Assert.AreEqual(1000f, (float)reply["data"]["requestedHeight"], 0.001f);
            Assert.IsTrue((bool)reply["data"]["clamped"]);
            Assert.AreEqual(1f, Data().GetHeights(0, 0, 1, 1)[0, 0], 0.001f);
        }

        [TestCase(1000f, 600f, 600f, 1f, true)]
        [TestCase(5f, 20f, 5f, 0.25f, false)]
        [TestCase(-3f, 20f, 0f, 0f, true)]
        [TestCase(7f, 0f, 0f, 0f, true)]
        public void FlattenHeightClampsToTheTerrainAndSaysSo(float requested, float sizeY, float applied, float normalized, bool clamped)
        {
            var actual = TerrainFunctions.FlattenHeight(requested, sizeY, out var actualNormalized, out var actualClamped);

            Assert.AreEqual(applied, actual, 0.0001f);
            Assert.AreEqual(normalized, actualNormalized, 0.0001f);
            Assert.AreEqual(clamped, actualClamped);
        }

        [TestCase("")]
        [TestCase("Sub/Terrain")]
        [TestCase("a:b")]
        [TestCase("CON")]
        [TestCase("nul.txt")]
        [TestCase("COM1.")]
        public void CreateTerrainRefusesANameThatIsNotAPlainFileName(string name)
        {
            Assert.AreEqual("INVALID_NAME", Code("create_terrain", "name", name));
        }

        [Test]
        public void CreateTerrainRefusesABadPositionOrSizeBeforeWritingAnything()
        {
            Assert.AreEqual("INVALID_VECTOR", Code("create_terrain", "name", CreatedName, "position", "1,2"));
            Assert.AreEqual("INVALID_SIZE", Code("create_terrain", "name", CreatedName, "width", "0"));
            Assert.AreEqual("INVALID_SIZE", Code("create_terrain", "name", CreatedName, "height", "-5"));
            Assert.AreEqual("INVALID_SIZE", Code("create_terrain", "name", CreatedName, "width", "1e30"));
            Assert.AreEqual("INVALID_SIZE", Code("create_terrain", "name", CreatedName, "length", "100001"));
            Assert.AreEqual("INVALID_SIZE", Code("create_terrain", "name", CreatedName, "height", "100001"));

            Assert.IsNull(AssetDatabase.LoadAssetAtPath<TerrainData>("Assets/" + CreatedName + ".asset"),
                "A refused create_terrain must not leave a TerrainData asset behind.");
            Assert.IsNull(GameObject.Find(CreatedName), "A refused create_terrain must not leave a GameObject behind.");
        }

        [Test]
        public void CreateTerrainReportsTheSizeAndPositionTheTerrainGot()
        {
            var made = Ok("create_terrain", "name", CreatedName, "heightmap_resolution", "33",
                "width", "80", "length", "60", "height", "30", "position", "1,2,3");
            createdAssetPath = (string)made["data"]["assetPath"];

            Assert.AreEqual(80f, (float)made["data"]["size"]["x"], 0.001f);
            Assert.AreEqual(30f, (float)made["data"]["size"]["y"], 0.001f);
            Assert.AreEqual(60f, (float)made["data"]["size"]["z"], 0.001f);
            Assert.AreEqual(2f, (float)made["data"]["position"]["y"], 0.001f);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), GameObject.Find(CreatedName).transform.position);
        }

        [TestCase("Terrain", true)]
        [TestCase("My Terrain 2", true)]
        [TestCase("", false)]
        [TestCase("  ", false)]
        [TestCase("../Escape", false)]
        [TestCase("Sub/Terrain", false)]
        [TestCase("Sub\\Terrain", false)]
        [TestCase("a:b", false)]
        [TestCase(".hidden", false)]
        [TestCase("a..b", false)]
        [TestCase("CON", false)]
        [TestCase("con", false)]
        [TestCase("NUL.txt", false)]
        [TestCase("aux.", false)]
        [TestCase("PRN ", false)]
        [TestCase("COM1", false)]
        [TestCase("lpt9.tar.gz", false)]
        [TestCase("COM0", true)]
        [TestCase("COM10", true)]
        [TestCase("CONSOLE", true)]
        [TestCase("Console.Terrain", true)]
        public void IsValidAssetName_AcceptsOnlyAPlainFileName(string name, bool valid)
        {
            Assert.AreEqual(valid, TerrainFunctions.IsValidAssetName(name));
        }

        [TestCase("15,15", true)]
        [TestCase("(2, 3)", true)]
        [TestCase("x,y", false)]
        [TestCase("15", false)]
        [TestCase("0,0", false)]
        [TestCase("-5,5", false)]
        public void TryParseTileSize_NeedsTwoPositiveNumbers(string text, bool valid)
        {
            Assert.AreEqual(valid, TerrainFunctions.TryParseTileSize(text, out _, out var error));
            Assert.AreEqual(valid, error == null);
        }

        [Test]
        public void TheWritersRefuseInPlayModeBecauseTheEditSurvivesIt()
        {
            var refusal = JObject.FromObject(TerrainFunctions.RefuseInPlayMode(true));

            Assert.AreEqual("EDIT_MODE_REQUIRED", (string)refusal["code"]);
            Assert.IsNotNull(refusal["data"]["hint"]);
            Assert.IsNull(TerrainFunctions.RefuseInPlayMode(false));
        }

        [Test]
        public void PlayModeRefusesOnlyATerrainDataThatIsAnAsset()
        {
            Assert.AreEqual("EDIT_MODE_REQUIRED", (string)JObject.FromObject(TerrainFunctions.RefuseInPlayMode(true, Data()))["code"]);
            Assert.IsNull(TerrainFunctions.RefuseInPlayMode(false, Data()));

            var runtime = new TerrainData();
            try
            {
                Assert.IsNull(TerrainFunctions.RefuseInPlayMode(true, runtime),
                    "A TerrainData made at runtime is not an asset, so editing it in Play Mode leaves nothing behind.");
            }
            finally
            {
                Object.DestroyImmediate(runtime);
            }
        }

        [Test]
        public void ATreePrefabWithNoMeshRendererIsRegisteredWithAWarning()
        {
            const string barePath = Folder + "/KwTerrainFixBare.prefab";
            var bare = new GameObject("KwTerrainFixBare");
            PrefabUtility.SaveAsPrefabAsset(bare, barePath);
            Object.DestroyImmediate(bare);

            var warned = Ok("add_tree_prototype", "target", Subject, "prefab_path", barePath);
            Assert.IsTrue((bool)warned["data"]["added"]);
            StringAssert.Contains("MeshRenderer", (string)warned["data"]["warning"]);
            StringAssert.Contains("MeshRenderer", (string)warned["message"]);

            var fine = Ok("add_tree_prototype", "target", Subject, "prefab_path", TreePath);
            Assert.AreEqual(JTokenType.Null, fine["data"]["warning"]?.Type ?? JTokenType.Null);
            StringAssert.DoesNotContain("MeshRenderer", (string)fine["message"]);
        }
    }
#endif

#if KITWRIGHT_AI
    public sealed class NavMeshFixesTests
    {
        private const string Subject = "KwNavFixSubject";

        private GameObject subject;

        [SetUp]
        public void CreateSubject()
        {
            // An agent in a scene with nothing baked logs an error of its own; the assertions read components.
            LogAssert.ignoreFailingMessages = true;
            Undo.IncrementCurrentGroup();
            subject = new GameObject(Subject);
        }

        [TearDown]
        public void DestroySubject()
        {
            LogAssert.ignoreFailingMessages = false;
            if (subject != null)
                Object.DestroyImmediate(subject);
            subject = null;
        }

        [Test]
        public void AnExistingAgentKeepsTheFieldsTheCallDidNotName()
        {
            var existing = subject.AddComponent<UnityEngine.AI.NavMeshAgent>();
            existing.speed = 8f;
            existing.radius = 0.3f;
            existing.height = 1.4f;

            var reply = Ok("add_nav_mesh_agent", "target", Subject, "speed", "10");

            Assert.AreEqual(10f, existing.speed, 0.001f);
            Assert.AreEqual(0.3f, existing.radius, 0.001f);
            Assert.AreEqual(1.4f, existing.height, 0.001f);
            Assert.IsFalse((bool)reply["data"]["added"]);
            Assert.AreEqual(0.3f, (float)reply["data"]["radius"], 0.001f, "The reply reads the component back.");
            Assert.AreEqual(1, subject.GetComponents<UnityEngine.AI.NavMeshAgent>().Length);
        }

        [Test]
        public void ConfiguringAnExistingAgentCanBeUndone()
        {
            var existing = subject.AddComponent<UnityEngine.AI.NavMeshAgent>();
            existing.speed = 8f;
            Undo.IncrementCurrentGroup();

            Ok("add_nav_mesh_agent", "target", Subject, "speed", "10");
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();

            Assert.AreEqual(8f, existing.speed, 0.001f);
        }

        [Test]
        public void ANewAgentIsAddedAndABadValueRefusesBeforeAddingAnything()
        {
            Assert.AreEqual("INVALID_VALUE", Code("add_nav_mesh_agent", "target", Subject, "radius", "0"));
            Assert.AreEqual("INVALID_VALUE", Code("add_nav_mesh_agent", "target", Subject, "speed", "-1"));
            Assert.IsNull(subject.GetComponent<UnityEngine.AI.NavMeshAgent>(),
                "A refused add_nav_mesh_agent must not leave a component behind.");

            var reply = Ok("add_nav_mesh_agent", "target", Subject, "speed", "6");
            Assert.IsTrue((bool)reply["data"]["added"]);
            Assert.AreEqual(6f, subject.GetComponent<UnityEngine.AI.NavMeshAgent>().speed, 0.001f);
        }

        [Test]
        public void AnExistingObstacleKeepsCarvingUnlessTheCallNamesIt()
        {
            var existing = subject.AddComponent<UnityEngine.AI.NavMeshObstacle>();
            existing.carving = false;

            var untouched = Ok("add_nav_mesh_obstacle", "target", Subject);
            Assert.IsFalse(existing.carving, "Omitting carving must not flip an existing obstacle to the tool's default.");
            Assert.IsFalse((bool)untouched["data"]["added"]);
            Assert.IsFalse((bool)untouched["data"]["carving"]);

            Ok("add_nav_mesh_obstacle", "target", Subject, "carving", "true");
            Assert.IsTrue(existing.carving);

            var fresh = new GameObject(Subject + "Fresh");
            try
            {
                var added = Ok("add_nav_mesh_obstacle", "target", fresh.name);
                Assert.IsTrue((bool)added["data"]["added"]);
                Assert.IsTrue(fresh.GetComponent<UnityEngine.AI.NavMeshObstacle>().carving, "A new obstacle carves by default.");
            }
            finally
            {
                Object.DestroyImmediate(fresh);
            }
        }

        [Test]
        public void ClearNavMeshSaysWhenThereWasNothingToClear()
        {
            if ((bool)Ok("get_nav_mesh_info")["data"]["hasNavMesh"])
                Assert.Ignore("The open scene has a baked NavMesh, and throwing away someone's bake is not this test's job.");

            Assert.IsFalse((bool)Ok("clear_nav_mesh")["data"]["cleared"]);
        }

        [Test]
        public void TheDestinationReplySerializesWithItsVectorAsPlainNumbers()
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(
                NavMeshFunctions.DestinationData("Guard", new Vector3(1f, 2f, 3f), true, false)));

            Assert.AreEqual("Guard", (string)json["target"]);
            Assert.AreEqual(1f, (float)json["destination"]["x"], 0.001f);
            Assert.AreEqual(2f, (float)json["destination"]["y"], 0.001f);
            Assert.AreEqual(3f, (float)json["destination"]["z"], 0.001f);
            Assert.IsTrue((bool)json["pathPending"]);
            Assert.IsFalse((bool)json["isStopped"]);
        }

        [Test]
        public void ABakeThatProducedNothingCarriesAHint()
        {
            var empty = JObject.FromObject(NavMeshFunctions.BakeReply(0, 0));
            Assert.IsTrue((bool)empty["success"]);
            Assert.IsNotNull(empty["hint"]);

            Assert.IsNull(JObject.FromObject(NavMeshFunctions.BakeReply(30, 10))["hint"]);
        }

        [Test]
        public void AnUntitledSceneIsRefusedBeforeABakeCouldSaveItUnderAName()
        {
            var refusal = JObject.FromObject(NavMeshFunctions.RefuseUnsavedScene(""));

            Assert.AreEqual("SCENE_NOT_SAVED", (string)refusal["code"]);
            StringAssert.Contains("save_scene", (string)refusal["hint"]);
            Assert.IsNull(NavMeshFunctions.RefuseUnsavedScene("Assets/Level.unity"));
        }

        [Test]
        public void ClearNavMeshOnlyClaimsClearedWhenNothingIsLeftLoaded()
        {
            var wiped = JObject.FromObject(NavMeshFunctions.ClearReply(30, 0, 0));
            Assert.IsTrue((bool)wiped["data"]["cleared"]);

            var empty = JObject.FromObject(NavMeshFunctions.ClearReply(0, 0, 0));
            Assert.IsFalse((bool)empty["data"]["cleared"]);

            var runtime = JObject.FromObject(NavMeshFunctions.ClearReply(30, 12, 4));
            Assert.IsTrue((bool)runtime["success"]);
            Assert.IsFalse((bool)runtime["data"]["cleared"]);
            Assert.AreEqual(12, (int)runtime["data"]["remainingVertexCount"]);
            Assert.AreEqual(4, (int)runtime["data"]["remainingTriangleCount"]);
            StringAssert.Contains("12 vertices", (string)runtime["message"]);
            StringAssert.Contains("NavMeshSurface", (string)runtime["message"]);
        }

        [TestCase(null, null, null, null)]
        [TestCase(0f, 0.5f, 2f, null)]
        [TestCase(-1f, null, null, "speed")]
        [TestCase(null, 0f, null, "radius")]
        [TestCase(null, null, -2f, "height")]
        public void AgentSettingsError_NamesTheFirstValueOutOfRange(float? speed, float? radius, float? height, string mentions)
        {
            var error = NavMeshFunctions.AgentSettingsError(speed, radius, height);

            if (mentions == null)
                Assert.IsNull(error);
            else
                StringAssert.Contains(mentions, error);
        }

        [Test]
        public void ADisabledOrInactiveAgentGetsAHintThatSaysWhich()
        {
            Assert.IsNull(NavMeshFunctions.UnusableAgentHint(true, true));
            StringAssert.Contains("disabled", NavMeshFunctions.UnusableAgentHint(false, true));
            StringAssert.Contains("inactive", NavMeshFunctions.UnusableAgentHint(true, false));
        }
    }
#endif
}
