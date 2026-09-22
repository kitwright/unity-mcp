// Copyright (C) KitWright. All rights reserved.

#if KITWRIGHT_TERRAIN
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The terrain writers, on a throwaway terrain. Every one of them edits TerrainData in place and
    /// reports what it meant to do, so each test reads the heightmap, the splatmap or the tree list
    /// back instead of trusting the answer.
    /// </summary>
    public sealed class TerrainToolsTests
    {
        private const string FolderName = "__KitWrightTerrainToolTests";
        private const string Folder = "Assets/" + FolderName;
        private const string Subject = "KwTerrainSubject";
        private const string LayerPath = Folder + "/KwTerrainLayer.terrainlayer";
        private const string TreePath = Folder + "/KwTerrainTree.prefab";

        private GameObject subject;

        [SetUp]
        public void CreateTerrain()
        {
            // A layer with no diffuse texture and a tree with no LOD group each draw a complaint of
            // their own. The assertions below read TerrainData, not the console.
            LogAssert.ignoreFailingMessages = true;

            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", FolderName);

            // Deliberately tiny: paint_terrain_layer allocates alphamapWidth * alphamapHeight * layers
            // floats, and the default 512 alphamap makes that a megabyte a call for no extra signal.
            var data = new TerrainData { heightmapResolution = 33 };
            data.size = new Vector3(50f, 20f, 50f);
            data.alphamapResolution = 32;
            AssetDatabase.CreateAsset(data, Folder + "/KwTerrainData.asset");
            AssetDatabase.CreateAsset(new TerrainLayer(), LayerPath);

            subject = new GameObject(Subject);
            subject.AddComponent<Terrain>().terrainData = data;

            var tree = new GameObject("KwTerrainTree");
            tree.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            tree.AddComponent<MeshRenderer>();
            PrefabUtility.SaveAsPrefabAsset(tree, TreePath);
            Object.DestroyImmediate(tree);
        }

        [TearDown]
        public void DestroyTerrain()
        {
            LogAssert.ignoreFailingMessages = false;

            if (subject != null)
                Object.DestroyImmediate(subject);
            subject = null;

            AssetDatabase.DeleteAsset(Folder);
        }

        private TerrainData Data() => subject.GetComponent<Terrain>().terrainData;

        [Test]
        public void FlattenAndAdjustWriteRealHeightsAndStayInsideTheTerrain()
        {
            Ok("flatten_terrain", "target", Subject, "height", "5");
            Assert.AreEqual(0.25f, Data().GetHeights(0, 0, 1, 1)[0, 0], 0.001f,
                "5 units of a 20-unit terrain is a normalized height of 0.25.");

            Ok("adjust_terrain_height", "target", Subject, "delta", "4");
            Assert.AreEqual(0.45f, Data().GetHeights(0, 0, 1, 1)[0, 0], 0.001f);

            // A delta far past the floor has to clamp, not wrap into the top of the terrain.
            Ok("adjust_terrain_height", "target", Subject, "delta", "-100");
            Assert.AreEqual(0f, Data().GetHeights(0, 0, 1, 1)[0, 0], 0.001f);

            var plain = new GameObject("KwTerrainlessObject");
            try
            {
                Assert.AreEqual("NO_TERRAIN", Code("flatten_terrain", "target", plain.name));
            }
            finally
            {
                Object.DestroyImmediate(plain);
            }
        }

        [Test]
        public void ALayerIsAddedOnceAndPaintingItFillsTheSplatmap()
        {
            var added = Ok("add_terrain_layer", "target", Subject, "asset_path", LayerPath);

            Assert.AreEqual(0, (int)added["data"]["layerIndex"]);
            Assert.AreEqual(1, Data().terrainLayers.Length);

            Assert.AreEqual("LAYER_ALREADY_ADDED",
                Code("add_terrain_layer", "target", Subject, "asset_path", LayerPath));
            Assert.AreEqual(1, Data().terrainLayers.Length, "The refused call must not add a second copy.");
            Assert.AreEqual("ASSET_NOT_FOUND",
                Code("add_terrain_layer", "target", Subject, "asset_path", Folder + "/NoSuch.terrainlayer"));

            Ok("paint_terrain_layer", "target", Subject, "layer_index", "0");
            var maps = Data().GetAlphamaps(0, 0, Data().alphamapWidth, Data().alphamapHeight);
            Assert.AreEqual(1f, maps[0, 0, 0], 0.001f, "The only layer should own every splatmap cell.");

            Assert.AreEqual("LAYER_INDEX_OUT_OF_RANGE",
                Code("paint_terrain_layer", "target", Subject, "layer_index", "7"));
        }

        [Test]
        public void TreesAreScatteredOnlyForAPrototypeThatWasRegisteredFirst()
        {
            Assert.AreEqual("PROTOTYPE_INDEX_OUT_OF_RANGE",
                Code("place_terrain_trees", "target", Subject, "prototype_index", "0", "count", "5"),
                "Scattering before add_tree_prototype has nothing to scatter.");

            var proto = Ok("add_tree_prototype", "target", Subject, "prefab_path", TreePath);
            Assert.AreEqual(0, (int)proto["data"]["prototypeIndex"]);

            Ok("place_terrain_trees", "target", Subject, "prototype_index", "0", "count", "5", "scale", "1.5");
            Assert.AreEqual(5, Data().treeInstanceCount);
            Assert.AreEqual(1.5f, Data().treeInstances[0].widthScale, 0.001f);

            Assert.AreEqual("INVALID_COUNT",
                Code("place_terrain_trees", "target", Subject, "prototype_index", "0", "count", "0"));
            Assert.AreEqual("PREFAB_NOT_FOUND",
                Code("add_tree_prototype", "target", Subject, "prefab_path", Folder + "/NoSuch.prefab"));
        }
    }
}
#endif
