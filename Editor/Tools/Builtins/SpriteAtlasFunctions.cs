// Copyright (C) KitWright. All rights reserved.
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
        private const int MaxAtlasPadding = 1024;

        [Description("Create a new SpriteAtlas asset (.spriteatlas) at the given path.")]
        public static object CreateSpriteAtlas(
            [ToolParam("Asset path, e.g. 'Assets/Atlases/MyAtlas.spriteatlas'")] string path,
            [ToolParam("Include the atlas in builds", Required = false)] bool include_in_build = true)
        {
            var requested = path;
            if (!string.IsNullOrWhiteSpace(path) && !path.TrimEnd().EndsWith(".spriteatlas", StringComparison.OrdinalIgnoreCase))
                path = path.TrimEnd() + ".spriteatlas";

            if (!AssetFunctions.TryNormalizeAssetPath(path, out path))
                return Response.Error("INVALID_PATH", new { path = requested },
                    "path must be under Assets/ with no '..' segments, and no folder or file name that starts or ends " +
                    "with a dot or space, ends with '~', or holds a character a file name cannot");

            string absolutePath;
            try { absolutePath = PathSafety.ResolveAssetPath(path); }
            catch (PathOutsideProjectException ex)
            { return Response.Error("INVALID_PATH", new { path, message = ex.Message }); }

            if (File.Exists(absolutePath)) return Response.Error("ATLAS_EXISTS", new { path });

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));

            var atlas = new SpriteAtlas();
            atlas.SetIncludeInBuild(include_in_build);
            AssetDatabase.CreateAsset(atlas, path);
            AssetDatabase.SaveAssetIfDirty(atlas);
            AssetDatabase.ImportAsset(path);

            if (AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path) == null)
            {
                if (!AssetDatabase.Contains(atlas)) Object.DestroyImmediate(atlas);
                return Response.Error("ATLAS_CREATE_FAILED", new { path });
            }

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

        [Description("Add sprites, textures, or folders (as packables) to a SpriteAtlas. Pass a comma-separated list of asset paths. Anything else (materials, prefabs, scripts) is refused; the reply counts what was actually added. Assets already packed are skipped.")]
        public static object AddToSpriteAtlas(
            [ToolParam("Asset path of the SpriteAtlas")] string path,
            [ToolParam("Comma-separated asset paths to add (sprites, textures, or folders)")] string asset_paths)
        {
            var atlas = LoadAtlas(path, out var error);
            if (atlas == null) return error;

            var (objects, missing) = LoadObjects(asset_paths);
            if (missing.Count > 0) return Response.Error("ASSET_NOT_FOUND", new { missing });
            if (objects.Count == 0) return Response.Error("NO_ASSETS", new { message = "no valid asset paths provided" });

            var notPackable = objects.Where(o => !IsPackable(o)).Select(AssetDatabase.GetAssetPath).ToList();
            if (notPackable.Count > 0)
                return Response.Error("NOT_PACKABLE", new { notPackable },
                    "A SpriteAtlas packs Sprites, Textures and folders only.");

            // Unity does not dedupe: Add lists an already packed asset (or one named twice) again.
            var packedPaths = PackedPaths(atlas);
            var fresh = objects.Where(o => packedPaths.Add(AssetDatabase.GetAssetPath(o))).ToArray();

            var added = 0;
            if (fresh.Length > 0)
            {
                var before = atlas.GetPackables().Length;
                atlas.Add(fresh);
                added = atlas.GetPackables().Length - before;
            }
            if (added > 0)
            {
                EditorUtility.SetDirty(atlas);
                AssetDatabase.SaveAssetIfDirty(atlas);
                AssetDatabase.ImportAsset(path);
            }

            return Response.Success(added > 0
                    ? $"Added {added} packable(s) to '{Path.GetFileName(path)}'."
                    : $"Nothing added to '{Path.GetFileName(path)}': everything given was already packed.",
                new { added, requested = objects.Count, packableCount = atlas.GetPackables().Length });
        }

        [Description("Remove packables from a SpriteAtlas. Pass a comma-separated list of asset paths currently packed. Answers NOT_PACKED when none of them was packed.")]
        public static object RemoveFromSpriteAtlas(
            [ToolParam("Asset path of the SpriteAtlas")] string path,
            [ToolParam("Comma-separated asset paths to remove")] string asset_paths)
        {
            var atlas = LoadAtlas(path, out var error);
            if (atlas == null) return error;

            var (objects, missing) = LoadObjects(asset_paths);
            if (missing.Count > 0) return Response.Error("ASSET_NOT_FOUND", new { missing });
            if (objects.Count == 0) return Response.Error("NO_ASSETS", new { message = "no valid asset paths provided" });

            // Only members go to Remove: what it does with a non-member is not documented.
            var packedPaths = PackedPaths(atlas);
            var members = objects.Where(o => packedPaths.Contains(AssetDatabase.GetAssetPath(o))).ToArray();

            var before = atlas.GetPackables().Length;
            if (members.Length > 0)
                atlas.Remove(members);
            var removed = before - atlas.GetPackables().Length;
            if (removed == 0)
                return Response.Error("NOT_PACKED", new { path, requested = objects.Count, packableCount = before },
                    "None of those assets is packed in this atlas, so nothing was removed. get_sprite_atlas_info lists the packables.");

            EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssetIfDirty(atlas);
            AssetDatabase.ImportAsset(path);

            return Response.Success($"Removed {removed} packable(s) from '{Path.GetFileName(path)}'.",
                new { removed, requested = objects.Count, packableCount = atlas.GetPackables().Length });
        }

        [Description("Update SpriteAtlas packing and texture settings. Only the parameters you pass are applied, and at least one is required.")]
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

            if (!include_in_build.HasValue && !enable_rotation.HasValue && !enable_tight_packing.HasValue && !padding.HasValue &&
                !readable.HasValue && !generate_mip_maps.HasValue && !srgb.HasValue && filter_mode == null)
                return Response.Error("NOTHING_TO_SET", new { path },
                    "Pass at least one setting: include_in_build, enable_rotation, enable_tight_packing, padding, readable, generate_mip_maps, srgb or filter_mode.");

            // Validated before anything is written. filter_mode used to be parsed last, after
            // include_in_build and the packing settings had already been applied, so a typo there
            // left half the call's changes on an asset the answer said had failed - and they reach
            // disk on whatever saves the atlas next, not here.
            FilterMode? filterMode = null;
            if (filter_mode != null)
            {
                if (!ValueConverter.TryParseEnum<FilterMode>(filter_mode, out var parsed))
                    return Response.Error("INVALID_FILTER_MODE", new { filter_mode, valid = new[] { "Point", "Bilinear", "Trilinear" } });
                filterMode = parsed;
            }

            if (padding.HasValue && (padding.Value < 0 || padding.Value > MaxAtlasPadding))
                return Response.Error("INVALID_PARAM", new { param = "padding", provided = padding.Value },
                    $"padding must be between 0 and {MaxAtlasPadding} pixels.");

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
                if (filterMode.HasValue) texture.filterMode = filterMode.Value;
                atlas.SetTextureSettings(texture);
            }

            EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssetIfDirty(atlas);
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

        private static HashSet<string> PackedPaths(SpriteAtlas atlas) =>
            new HashSet<string>(
                atlas.GetPackables().Where(p => p != null).Select(AssetDatabase.GetAssetPath), StringComparer.OrdinalIgnoreCase);

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

        private static bool IsPackable(Object obj) =>
            obj is Sprite || obj is Texture2D || (obj is DefaultAsset && AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(obj)));
    }
}
