// Copyright (C) KitWright. Licensed under MIT.

// com.unity.modules.terrain is optional; without it these tools disappear instead of breaking the build.
#if KITWRIGHT_TERRAIN
using System.Collections.Generic;
using System.IO;
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
        internal const int MaxTreesPerCall = 100000;
        internal const float MaxTerrainSize = 100000f;

        private static readonly char[] InvalidNameChars =
            Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).ToArray();

        private static readonly HashSet<string> ReservedDeviceNames = new HashSet<string>(
            new[] { "CON", "PRN", "AUX", "NUL" }.Concat(Enumerable.Range(1, 9).SelectMany(i => new[] { "COM" + i, "LPT" + i })),
            System.StringComparer.OrdinalIgnoreCase);

        [Description("Create a Terrain GameObject with a fresh TerrainData. Returns the new instanceId for follow-up calls.")]
        public static object CreateTerrain(
            [ToolParam("Name of the new terrain object", Required = false)] string name = "Terrain",
            [ToolParam("Heightmap resolution (power of two + 1, e.g. 513, 1025)", Required = false)] int heightmap_resolution = 513,
            [ToolParam("Terrain width in world units (x), greater than 0 and at most 100000", Required = false)] float width = 500f,
            [ToolParam("Terrain length in world units (z), greater than 0 and at most 100000", Required = false)] float length = 500f,
            [ToolParam("Terrain max height in world units (y), greater than 0 and at most 100000", Required = false)] float height = 600f,
            [ToolParam("Position as 'x,y,z'", Required = false)] string position = "0,0,0")
        {
            var playMode = RefuseInPlayMode(EditorApplication.isPlaying);
            if (playMode != null) return playMode;

            if (!IsValidAssetName(name))
                return Response.Error("INVALID_NAME", new
                {
                    name,
                    hint = "Use a plain name: not empty, no folders, '..', ':', characters a file name cannot hold or a Windows device name (CON, NUL, COM1 ...)."
                });
            if (!(IsTerrainSize(width) && IsTerrainSize(length) && IsTerrainSize(height)))
                return Response.Error("INVALID_SIZE", new
                {
                    width,
                    length,
                    height,
                    max = MaxTerrainSize,
                    hint = $"width, length and height must be greater than 0 and at most {MaxTerrainSize}."
                });
            if (!ValueConverter.TryParseVector3(position, out var pos, out var positionError))
                return Response.Error("INVALID_VECTOR", new { position, hint = positionError });

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
            go.transform.position = pos;

            Selection.activeGameObject = go;
            AssetDatabase.SaveAssetIfDirty(data);

            return Response.Success($"Created terrain '{name}'.", new
            {
                instanceId = ObjectIdCodec.GetSerializableId(go),
                name = go.name,
                assetPath,
                heightmapResolution = data.heightmapResolution,
                size = Vec(data.size),
                position = Vec(go.transform.position)
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

        [Description("Flatten the entire terrain to a uniform height. Height is in world units, clamped to the terrain's max height; the reply says the height that was applied.")]
        public static object FlattenTerrain(
            [ToolParam("Terrain GameObject name, path, or instance ID")] string target,
            [ToolParam("Target height in world units", Required = false)] float height = 0f)
        {
            if (!TryResolveTerrainForWrite(target, out var terrain, out var err)) return err;
            var d = terrain.terrainData;
            if (d.size.y <= 0f) return Response.Error("INVALID_TERRAIN_HEIGHT", new { target });

            var applied = FlattenHeight(height, d.size.y, out var normalized, out var clamped);
            int res = d.heightmapResolution;
            var heights = new float[res, res];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    heights[y, x] = normalized;

            Undo.RegisterCompleteObjectUndo(d, "Flatten Terrain");
            d.SetHeights(0, 0, heights);
            SaveTerrainData(d);

            var message = clamped
                ? $"Flattened '{terrain.name}' to {applied} units (requested {height}, clamped to the terrain's height range)."
                : $"Flattened '{terrain.name}' to {applied} units.";
            return Response.Success(message, new { target = terrain.name, height = applied, requestedHeight = height, normalized, clamped });
        }

        [Description("Raise or lower the whole terrain by a delta height in world units (positive raises, negative lowers). Clamped to [0, max height].")]
        public static object AdjustTerrainHeight(
            [ToolParam("Terrain GameObject name, path, or instance ID")] string target,
            [ToolParam("Delta height in world units (+ up, - down)")] float delta)
        {
            if (!TryResolveTerrainForWrite(target, out var terrain, out var err)) return err;
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
            SaveTerrainData(d);

            return Response.Success($"Adjusted '{terrain.name}' by {delta} units.", new { target = terrain.name, delta });
        }

        [Description("Add a TerrainLayer (texture) to a terrain. Provide the asset path of a TerrainLayer asset, or a Texture2D to wrap in a new layer.")]
        public static object AddTerrainLayer(
            [ToolParam("Terrain GameObject name, path, or instance ID")] string target,
            [ToolParam("Asset path of a TerrainLayer (.terrainlayer) or a Texture2D to wrap")] string asset_path,
            [ToolParam("Tile size as 'x,y' in world units, both greater than 0 (only used when wrapping a texture)", Required = false)] string tile_size = "15,15")
        {
            if (!TryResolveTerrainForWrite(target, out var terrain, out var err)) return err;

            var d = terrain.terrainData;
            var layers = d.terrainLayers?.ToList() ?? new List<TerrainLayer>();

            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(asset_path);
            if (layer == null)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(asset_path);
                if (tex == null)
                    return Response.Error("ASSET_NOT_FOUND", new { asset_path, expected = "TerrainLayer or Texture2D" });

                if (!TryParseTileSize(tile_size, out var ts, out var tileError))
                    return Response.Error("INVALID_TILE_SIZE", new { tile_size, hint = tileError });

                var wrapping = layers.Find(l => l != null && l.diffuseTexture == tex);
                if (wrapping != null)
                    return Response.Error("LAYER_ALREADY_ADDED", new { target = terrain.name, layer = wrapping.name });

                layer = new TerrainLayer { diffuseTexture = tex, tileSize = ts, name = tex.name };

                // In Play Mode only a terrain created at runtime gets here; its layer stays in memory like its data.
                if (!EditorApplication.isPlaying)
                {
                    var layerPath = AssetDatabase.GenerateUniqueAssetPath($"Assets/{tex.name}.terrainlayer");
                    AssetDatabase.CreateAsset(layer, layerPath);
                    AssetDatabase.SaveAssetIfDirty(layer);
                }
            }

            if (layers.Contains(layer))
                return Response.Error("LAYER_ALREADY_ADDED", new { target = terrain.name, layer = layer.name });

            Undo.RegisterCompleteObjectUndo(d, "Add Terrain Layer");
            layers.Add(layer);
            d.terrainLayers = layers.ToArray();
            SaveTerrainData(d);

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
            if (!TryResolveTerrainForWrite(target, out var terrain, out var err)) return err;
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
            SaveTerrainData(d);

            return Response.Success($"Painted layer {layer_index} across '{terrain.name}'.", new { target = terrain.name, layer_index });
        }

        [Description("Register a tree prefab as a tree prototype on the terrain. Returns the prototype index used by place_terrain_trees; a prefab that is already registered answers its existing index.")]
        public static object AddTreePrototype(
            [ToolParam("Terrain GameObject name, path, or instance ID")] string target,
            [ToolParam("Asset path of a tree prefab (GameObject)")] string prefab_path)
        {
            if (!TryResolveTerrainForWrite(target, out var terrain, out var err)) return err;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path);
            if (prefab == null) return Response.Error("PREFAB_NOT_FOUND", new { prefab_path });

            var d = terrain.terrainData;
            var protos = d.treePrototypes?.ToList() ?? new List<TreePrototype>();

            var existing = protos.FindIndex(p => p != null && p.prefab == prefab);
            if (existing >= 0)
                return Response.Success($"'{prefab.name}' is already tree prototype {existing}.",
                    new { target = terrain.name, prototypeIndex = existing, added = false });

            protos.Add(new TreePrototype { prefab = prefab });

            Undo.RegisterCompleteObjectUndo(d, "Add Tree Prototype");
            d.treePrototypes = protos.ToArray();
            d.RefreshPrototypes();
            SaveTerrainData(d);

            var warning = prefab.GetComponentInChildren<MeshRenderer>(true) == null
                ? "The prefab has no MeshRenderer (on it or its children), so Unity cannot draw it as a tree and logs errors for every tree placed."
                : null;
            var message = $"Added tree prototype '{prefab.name}'.";
            return Response.Success(warning == null ? message : $"{message} {warning}",
                new { target = terrain.name, prototypeIndex = protos.Count - 1, added = true, warning });
        }

        [Description("Scatter tree instances randomly across the terrain using a registered tree prototype. Uses uniform random placement over the terrain surface. At most 100000 trees per call.")]
        public static object PlaceTerrainTrees(
            [ToolParam("Terrain GameObject name, path, or instance ID")] string target,
            [ToolParam("Tree prototype index (see add_tree_prototype / get_terrain_info)")] int prototype_index,
            [ToolParam("Number of trees to scatter (1 to 100000 per call)")] int count,
            [ToolParam("Uniform tree scale, greater than 0", Required = false)] float scale = 1f)
        {
            if (!TryResolveTerrainForWrite(target, out var terrain, out var err)) return err;
            var d = terrain.terrainData;
            int protoCount = d.treePrototypes?.Length ?? 0;
            if (prototype_index < 0 || prototype_index >= protoCount)
                return Response.Error("PROTOTYPE_INDEX_OUT_OF_RANGE", new { prototype_index, protoCount });
            if (count <= 0 || count > MaxTreesPerCall)
                return Response.Error("INVALID_COUNT", new
                {
                    count,
                    max = MaxTreesPerCall,
                    hint = $"Place 1 to {MaxTreesPerCall} trees per call and call again for more."
                });
            if (scale <= 0f) return Response.Error("INVALID_SCALE", new { scale, hint = "scale must be greater than 0." });

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
            SaveTerrainData(d);

            return Response.Success($"Placed {count} trees on '{terrain.name}'.", new { target = terrain.name, prototype_index, total = instances.Count });
        }

        internal static object RefuseInPlayMode(bool isPlaying) =>
            isPlaying
                ? Response.Error("EDIT_MODE_REQUIRED", new
                {
                    hint = "TerrainData is an asset: an edit made in Play Mode is written to disk and survives it, while Undo history is dropped. Call exit_play_mode first, then retry."
                })
                : null;

        internal static object RefuseInPlayMode(bool isPlaying, TerrainData data) =>
            RefuseInPlayMode(isPlaying && EditorUtility.IsPersistent(data));

        internal static bool IsTerrainSize(float value) => value > 0f && value <= MaxTerrainSize;

        internal static bool IsValidAssetName(string name) =>
            !string.IsNullOrWhiteSpace(name)
            && name.IndexOfAny(InvalidNameChars) < 0
            && name[0] != '.'
            && !name.Contains("..")
            && !IsReservedDeviceName(name);

        // Windows treats CON, NUL, COM1 ... as a device even with an extension, so the name before the first dot decides.
        private static bool IsReservedDeviceName(string name)
        {
            var dot = name.IndexOf('.');
            return ReservedDeviceNames.Contains((dot >= 0 ? name.Substring(0, dot) : name).TrimEnd(' '));
        }

        internal static bool TryParseTileSize(string text, out Vector2 size, out string error)
        {
            if (!ValueConverter.TryParseVector2(text, out size, out error)) return false;
            if (size.x > 0f && size.y > 0f) return true;
            error = $"'{text}' must have both components greater than 0";
            return false;
        }

        internal static float FlattenHeight(float requested, float sizeY, out float normalized, out bool clamped)
        {
            var applied = sizeY > 0f ? Mathf.Clamp(requested, 0f, sizeY) : 0f;
            normalized = sizeY > 0f ? applied / sizeY : 0f;
            clamped = applied != requested;
            return applied;
        }

        private static object Vec(Vector3 v) => new { x = v.x, y = v.y, z = v.z };

        // Nothing writes the asset out until Unity decides to, and a batch run that never saves loses the edit.
        private static void SaveTerrainData(TerrainData data)
        {
            if (!EditorUtility.IsPersistent(data)) return;
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssetIfDirty(data);
        }

        private static bool TryResolveTerrainForWrite(string target, out Terrain terrain, out object error)
        {
            if (!TryResolveTerrain(target, out terrain, out error)) return false;
            error = RefuseInPlayMode(EditorApplication.isPlaying, terrain.terrainData);
            return error == null;
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
