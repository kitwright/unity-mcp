// Copyright (C) KitWright. Licensed under MIT.

// com.unity.modules.terrain is optional; without it these tools disappear instead of breaking the build.
#if KITWRIGHT_TERRAIN
using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Terrain")]
    internal static class TerrainFunctions
    {
        [Description("Create a Terrain GameObject with a fresh TerrainData. Returns the new instanceId for follow-up calls.")]
        public static object CreateTerrain(
            [ToolParam("Name of the new terrain object", Required = false)] string name = "Terrain",
            [ToolParam("Heightmap resolution (power of two + 1, e.g. 513, 1025)", Required = false)] int heightmap_resolution = 513,
            [ToolParam("Terrain width in world units (x)", Required = false)] float width = 500f,
            [ToolParam("Terrain length in world units (z)", Required = false)] float length = 500f,
            [ToolParam("Terrain max height in world units (y)", Required = false)] float height = 600f,
            [ToolParam("Position as 'x,y,z'", Required = false)] string position = "0,0,0")
        {
            var data = new TerrainData
            {
                heightmapResolution = heightmap_resolution,
                size = new Vector3(width, height, length)
            };

            var assetPath = AssetDatabase.GenerateUniqueAssetPath($"Assets/{name}.asset");
            AssetDatabase.CreateAsset(data, assetPath);

            var go = Terrain.CreateTerrainGameObject(data);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

            if (ValueConverter.TryParseVector3(position, out var pos, out _))
                go.transform.position = pos;

            Selection.activeGameObject = go;
            AssetDatabase.SaveAssets();

            return Response.Success($"Created terrain '{name}'.", new
            {
                instanceId = go.GetInstanceID(),
                name = go.name,
                assetPath,
                heightmapResolution = data.heightmapResolution,
                size = new { x = width, y = height, z = length }
            });
        }

        [Description("Get info about a terrain: size, heightmap resolution, terrain layers, tree/detail prototype counts.")]
        [ReadOnlyTool]
        public static object GetTerrainInfo(
            [ToolParam("Terrain GameObject name, path, or instance ID")] string target)
        {
            if (!TryResolveTerrain(target, out var terrain, out var err)) return err;
            var d = terrain.terrainData;

            return Response.Success($"Terrain '{terrain.name}'.", new
            {
                name = terrain.name,
                size = new { x = d.size.x, y = d.size.y, z = d.size.z },
                heightmapResolution = d.heightmapResolution,
                alphamapResolution = d.alphamapResolution,
                detailResolution = d.detailResolution,
                layers = d.terrainLayers?.Select(l => l != null ? l.name : "<null>").ToArray() ?? new string[0],
                treePrototypeCount = d.treePrototypes?.Length ?? 0,
                treeInstanceCount = d.treeInstanceCount,
                detailPrototypeCount = d.detailPrototypes?.Length ?? 0
            });
        }

        [Description("Flatten the entire terrain to a uniform height. Height is in world units, clamped to the terrain's max height.")]
        public static object FlattenTerrain(
            [ToolParam("Terrain GameObject name, path, or instance ID")] string target,
            [ToolParam("Target height in world units", Required = false)] float height = 0f)
        {
            if (!TryResolveTerrain(target, out var terrain, out var err)) return err;
            var d = terrain.terrainData;

            float normalized = d.size.y > 0f ? Mathf.Clamp01(height / d.size.y) : 0f;
            int res = d.heightmapResolution;
            var heights = new float[res, res];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    heights[y, x] = normalized;

            Undo.RegisterCompleteObjectUndo(d, "Flatten Terrain");
            d.SetHeights(0, 0, heights);

            return Response.Success($"Flattened '{terrain.name}' to {height} units.", new { target = terrain.name, height, normalized });
        }

        [Description("Raise or lower the whole terrain by a delta height in world units (positive raises, negative lowers). Clamped to [0, max height].")]
        public static object AdjustTerrainHeight(
            [ToolParam("Terrain GameObject name, path, or instance ID")] string target,
            [ToolParam("Delta height in world units (+ up, - down)")] float delta)
        {
            if (!TryResolveTerrain(target, out var terrain, out var err)) return err;
            var d = terrain.terrainData;
            if (d.size.y <= 0f) return Response.Error("INVALID_TERRAIN_HEIGHT", new { target });

            float deltaNorm = delta / d.size.y;
            int res = d.heightmapResolution;
            var heights = d.GetHeights(0, 0, res, res);
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    heights[y, x] = Mathf.Clamp01(heights[y, x] + deltaNorm);

            Undo.RegisterCompleteObjectUndo(d, "Adjust Terrain Height");
            d.SetHeights(0, 0, heights);

            return Response.Success($"Adjusted '{terrain.name}' by {delta} units.", new { target = terrain.name, delta });
        }

        [Description("Add a TerrainLayer (texture) to a terrain. Provide the asset path of a TerrainLayer asset, or a Texture2D to wrap in a new layer.")]
        public static object AddTerrainLayer(
            [ToolParam("Terrain GameObject name, path, or instance ID")] string target,
            [ToolParam("Asset path of a TerrainLayer (.terrainlayer) or a Texture2D to wrap")] string asset_path,
            [ToolParam("Tile size as 'x,y' in world units (only used when wrapping a texture)", Required = false)] string tile_size = "15,15")
        {
            if (!TryResolveTerrain(target, out var terrain, out var err)) return err;

            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(asset_path);
            if (layer == null)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(asset_path);
                if (tex == null)
                    return Response.Error("ASSET_NOT_FOUND", new { asset_path, expected = "TerrainLayer or Texture2D" });

                layer = new TerrainLayer { diffuseTexture = tex };
                if (ValueConverter.TryParseVector2(tile_size, out var ts, out _)) layer.tileSize = ts;

                var layerPath = AssetDatabase.GenerateUniqueAssetPath($"Assets/{tex.name}.terrainlayer");
                AssetDatabase.CreateAsset(layer, layerPath);
                AssetDatabase.SaveAssets();
            }

            var d = terrain.terrainData;
            var layers = d.terrainLayers?.ToList() ?? new List<TerrainLayer>();
            if (layers.Contains(layer))
                return Response.Error("LAYER_ALREADY_ADDED", new { target = terrain.name, layer = layer.name });

            Undo.RegisterCompleteObjectUndo(d, "Add Terrain Layer");
            layers.Add(layer);
            d.terrainLayers = layers.ToArray();

            return Response.Success($"Added layer '{layer.name}' to '{terrain.name}'.", new
            {
                target = terrain.name,
                layer = layer.name,
                layerIndex = layers.Count - 1
            });
        }

        [Description("Paint a single terrain layer across the entire terrain at full opacity (fills the splatmap with that layer). Layer index refers to terrain_layers order.")]
        public static object PaintTerrainLayer(
            [ToolParam("Terrain GameObject name, path, or instance ID")] string target,
            [ToolParam("Index of the layer to paint (0-based, see get_terrain_info)")] int layer_index)
        {
            if (!TryResolveTerrain(target, out var terrain, out var err)) return err;
            var d = terrain.terrainData;
            int layerCount = d.terrainLayers?.Length ?? 0;
            if (layer_index < 0 || layer_index >= layerCount)
                return Response.Error("LAYER_INDEX_OUT_OF_RANGE", new { layer_index, layerCount });

            int w = d.alphamapWidth, h = d.alphamapHeight;
            var maps = new float[h, w, layerCount];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    maps[y, x, layer_index] = 1f;

            Undo.RegisterCompleteObjectUndo(d, "Paint Terrain Layer");
            d.SetAlphamaps(0, 0, maps);

            return Response.Success($"Painted layer {layer_index} across '{terrain.name}'.", new { target = terrain.name, layer_index });
        }

        [Description("Register a tree prefab as a tree prototype on the terrain. Returns the prototype index used by place_terrain_trees.")]
        public static object AddTreePrototype(
            [ToolParam("Terrain GameObject name, path, or instance ID")] string target,
            [ToolParam("Asset path of a tree prefab (GameObject)")] string prefab_path)
        {
            if (!TryResolveTerrain(target, out var terrain, out var err)) return err;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path);
            if (prefab == null) return Response.Error("PREFAB_NOT_FOUND", new { prefab_path });

            var d = terrain.terrainData;
            var protos = d.treePrototypes?.ToList() ?? new List<TreePrototype>();
            protos.Add(new TreePrototype { prefab = prefab });

            Undo.RegisterCompleteObjectUndo(d, "Add Tree Prototype");
            d.treePrototypes = protos.ToArray();
            d.RefreshPrototypes();

            return Response.Success($"Added tree prototype '{prefab.name}'.", new { target = terrain.name, prototypeIndex = protos.Count - 1 });
        }

        [Description("Scatter tree instances randomly across the terrain using a registered tree prototype. Uses uniform random placement over the terrain surface.")]
        public static object PlaceTerrainTrees(
            [ToolParam("Terrain GameObject name, path, or instance ID")] string target,
            [ToolParam("Tree prototype index (see add_tree_prototype / get_terrain_info)")] int prototype_index,
            [ToolParam("Number of trees to scatter")] int count,
            [ToolParam("Uniform tree scale", Required = false)] float scale = 1f)
        {
            if (!TryResolveTerrain(target, out var terrain, out var err)) return err;
            var d = terrain.terrainData;
            int protoCount = d.treePrototypes?.Length ?? 0;
            if (prototype_index < 0 || prototype_index >= protoCount)
                return Response.Error("PROTOTYPE_INDEX_OUT_OF_RANGE", new { prototype_index, protoCount });
            if (count <= 0) return Response.Error("INVALID_COUNT", new { count });

            var instances = d.treeInstances?.ToList() ?? new List<TreeInstance>();
            var rng = new System.Random();
            for (int i = 0; i < count; i++)
            {
                instances.Add(new TreeInstance
                {
                    prototypeIndex = prototype_index,
                    position = new Vector3((float)rng.NextDouble(), 0f, (float)rng.NextDouble()),
                    widthScale = scale,
                    heightScale = scale,
                    rotation = (float)(rng.NextDouble() * Mathf.PI * 2f),
                    color = Color.white,
                    lightmapColor = Color.white
                });
            }

            Undo.RegisterCompleteObjectUndo(d, "Place Terrain Trees");
            d.SetTreeInstances(instances.ToArray(), true);

            return Response.Success($"Placed {count} trees on '{terrain.name}'.", new { target = terrain.name, prototype_index, total = instances.Count });
        }

        private static bool TryResolveTerrain(string target, out Terrain terrain, out object error)
        {
            terrain = null;
            error = null;
            var go = ObjectsHelper.FindTarget(target);
            if (go == null)
            {
                error = ObjectsHelper.NotFound("target", target);
                return false;
            }
            terrain = go.GetComponent<Terrain>();
            if (terrain == null || terrain.terrainData == null)
            {
                error = Response.Error("NO_TERRAIN", new { target, hint = "GameObject has no Terrain component or missing TerrainData." });
                return false;
            }
            return true;
        }
    }
}
#endif
