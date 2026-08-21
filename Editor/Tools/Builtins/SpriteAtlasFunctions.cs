// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using Object = UnityEngine.Object;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("SpriteAtlas")]
    internal static class SpriteAtlasFunctions
    {
        [Description("Create a new SpriteAtlas asset (.spriteatlas) at the given path.")]
        public static object CreateSpriteAtlas(
            [ToolParam("Asset path, e.g. 'Assets/Atlases/MyAtlas.spriteatlas'")] string path,
            [ToolParam("Include the atlas in builds", Required = false)] bool include_in_build = true)
        {
            if (!IsUnderAssets(path)) return Response.Error("INVALID_PATH", new { path, hint = "path must be under Assets/ and end with .spriteatlas" });
            path = path.Replace('\\', '/');
            if (!path.EndsWith(".spriteatlas", StringComparison.OrdinalIgnoreCase))
                path += ".spriteatlas";
            if (File.Exists(path)) return Response.Error("ATLAS_EXISTS", new { path });

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var atlas = new SpriteAtlas();
            atlas.SetIncludeInBuild(include_in_build);
            AssetDatabase.CreateAsset(atlas, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);

            return Response.Success($"SpriteAtlas created at '{path}'.", new { path, includeInBuild = include_in_build });
        }

        [Description("Get SpriteAtlas details: packables, sprite count, include-in-build, packing/texture settings.")]
        [ReadOnlyTool]
        public static object GetSpriteAtlasInfo(
            [ToolParam("Asset path of the SpriteAtlas")] string path)
        {
            var atlas = LoadAtlas(path, out var error);
            if (atlas == null) return error;

            var packing = atlas.GetPackingSettings();
            var texture = atlas.GetTextureSettings();
            var packables = atlas.GetPackables().Where(p => p != null).Select(AssetDatabase.GetAssetPath).ToList();

            return Response.Success($"SpriteAtlas '{Path.GetFileName(path)}'.", new
            {
                path,
                includeInBuild = atlas.IsIncludeInBuild(),
                spriteCount = atlas.spriteCount,
                packableCount = packables.Count,
                packables,
                packing = new { padding = packing.padding, enableRotation = packing.enableRotation, enableTightPacking = packing.enableTightPacking },
                texture = new { readable = texture.readable, generateMipMaps = texture.generateMipMaps, sRGB = texture.sRGB, filterMode = texture.filterMode.ToString() }
            });
        }

        [Description("Add sprites, textures, or folders (as packables) to a SpriteAtlas. Pass a comma-separated list of asset paths.")]
        public static object AddToSpriteAtlas(
            [ToolParam("Asset path of the SpriteAtlas")] string path,
            [ToolParam("Comma-separated asset paths to add (sprites, textures, or folders)")] string asset_paths)
        {
            var atlas = LoadAtlas(path, out var error);
            if (atlas == null) return error;

            var (objects, missing) = LoadObjects(asset_paths);
            if (missing.Count > 0) return Response.Error("ASSET_NOT_FOUND", new { missing });
            if (objects.Count == 0) return Response.Error("NO_ASSETS", new { message = "no valid asset paths provided" });

            atlas.Add(objects.ToArray());
            EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);

            return Response.Success($"Added {objects.Count} packable(s) to '{Path.GetFileName(path)}'.", new { added = objects.Count, packableCount = atlas.GetPackables().Length });
        }

        [Description("Remove packables from a SpriteAtlas. Pass a comma-separated list of asset paths currently packed.")]
        public static object RemoveFromSpriteAtlas(
            [ToolParam("Asset path of the SpriteAtlas")] string path,
            [ToolParam("Comma-separated asset paths to remove")] string asset_paths)
        {
            var atlas = LoadAtlas(path, out var error);
            if (atlas == null) return error;

            var (objects, missing) = LoadObjects(asset_paths);
            if (missing.Count > 0) return Response.Error("ASSET_NOT_FOUND", new { missing });
            if (objects.Count == 0) return Response.Error("NO_ASSETS", new { message = "no valid asset paths provided" });

            atlas.Remove(objects.ToArray());
            EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);

            return Response.Success($"Removed {objects.Count} packable(s) from '{Path.GetFileName(path)}'.", new { removed = objects.Count, packableCount = atlas.GetPackables().Length });
        }

        [Description("Update SpriteAtlas packing and texture settings. Only the parameters you pass are applied.")]
        public static object SetSpriteAtlasSettings(
            [ToolParam("Asset path of the SpriteAtlas")] string path,
            [ToolParam("Include in builds", Required = false)] bool? include_in_build = null,
            [ToolParam("Allow sprite rotation during packing", Required = false)] bool? enable_rotation = null,
            [ToolParam("Use tight packing", Required = false)] bool? enable_tight_packing = null,
            [ToolParam("Padding between sprites (pixels)", Required = false)] int? padding = null,
            [ToolParam("Make atlas texture readable", Required = false)] bool? readable = null,
            [ToolParam("Generate mipmaps", Required = false)] bool? generate_mip_maps = null,
            [ToolParam("Use sRGB color space", Required = false)] bool? srgb = null,
            [ToolParam("Filter mode: Point, Bilinear, Trilinear", Required = false)] string filter_mode = null)
        {
            var atlas = LoadAtlas(path, out var error);
            if (atlas == null) return error;

            if (include_in_build.HasValue) atlas.SetIncludeInBuild(include_in_build.Value);

            if (enable_rotation.HasValue || enable_tight_packing.HasValue || padding.HasValue)
            {
                var packing = atlas.GetPackingSettings();
                if (enable_rotation.HasValue) packing.enableRotation = enable_rotation.Value;
                if (enable_tight_packing.HasValue) packing.enableTightPacking = enable_tight_packing.Value;
                if (padding.HasValue) packing.padding = padding.Value;
                atlas.SetPackingSettings(packing);
            }

            if (readable.HasValue || generate_mip_maps.HasValue || srgb.HasValue || filter_mode != null)
            {
                var texture = atlas.GetTextureSettings();
                if (readable.HasValue) texture.readable = readable.Value;
                if (generate_mip_maps.HasValue) texture.generateMipMaps = generate_mip_maps.Value;
                if (srgb.HasValue) texture.sRGB = srgb.Value;
                if (filter_mode != null)
                {
                    if (!Enum.TryParse<FilterMode>(filter_mode, true, out var fm))
                        return Response.Error("INVALID_FILTER_MODE", new { filter_mode, valid = new[] { "Point", "Bilinear", "Trilinear" } });
                    texture.filterMode = fm;
                }
                atlas.SetTextureSettings(texture);
            }

            EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
            return Response.Success($"SpriteAtlas settings updated for '{Path.GetFileName(path)}'.");
        }

        [Description("Delete a SpriteAtlas asset by moving it to the OS trash (Recycle Bin). Not an Editor undo step, but the file can be restored by hand from the trash.")]
        public static object DeleteSpriteAtlas(
            [ToolParam("Asset path of the SpriteAtlas to delete. The file is moved to the OS trash, so it can be recovered by hand.")] string path)
        {
            var atlas = LoadAtlas(path, out var error);
            if (atlas == null) return error;
            if (!AssetDatabase.MoveAssetToTrash(path)) return Response.Error("DELETE_FAILED", new { path });
            return Response.Success($"SpriteAtlas '{Path.GetFileName(path)}' moved to OS trash.", new { path });
        }

        [Description("List all SpriteAtlas assets in the project, optionally scoped to a folder.")]
        [ReadOnlyTool]
        public static object ListSpriteAtlases(
            [ToolParam("Folder to search in (e.g. 'Assets/Atlases'). Omit for whole project.", Required = false)] string folder = null)
        {
            var searchFolders = string.IsNullOrEmpty(folder) ? null : new[] { folder.Replace('\\', '/').TrimEnd('/') };
            var guids = searchFolders == null
                ? AssetDatabase.FindAssets("t:SpriteAtlas")
                : AssetDatabase.FindAssets("t:SpriteAtlas", searchFolders);
            var paths = guids.Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p).ToList();
            return Response.Success($"Found {paths.Count} SpriteAtlas asset(s).", new { count = paths.Count, atlases = paths });
        }

        private static SpriteAtlas LoadAtlas(string path, out object error)
        {
            error = null;
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            if (atlas == null) error = Response.Error("ATLAS_NOT_FOUND", new { path });
            return atlas;
        }

        private static (List<Object> objects, List<string> missing) LoadObjects(string csv)
        {
            var objects = new List<Object>();
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(csv)) return (objects, missing);

            foreach (var raw in csv.Split(','))
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;
                var obj = AssetDatabase.LoadMainAssetAtPath(p);
                if (obj == null) missing.Add(p);
                else objects.Add(obj);
            }
            return (objects, missing);
        }

        private static bool IsUnderAssets(string path)
        {
            return !string.IsNullOrEmpty(path) && path.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
