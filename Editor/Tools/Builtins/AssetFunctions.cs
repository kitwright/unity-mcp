// Copyright (C) KitWright. Licensed under MIT.
using System;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using System.Linq;
using KitWright.Editor.Services;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Asset")]
    internal static class AssetFunctions
    {
        [Description("Create a new material with a specified color")]
        [ToolProvider("Materials & Shaders")]
        public static string CreateMaterial(
            [ToolParam("Name of the material")] string name,
            [ToolParam("Color as 'r,g,b,a' or hex '#RRGGBB'", Required = false)] string color = "1,1,1,1",
            [ToolParam("Shader name (default: Standard)", Required = false)] string shader = "Standard",
            [ToolParam("Save path (e.g. 'Assets/Materials/')", Required = false)] string save_path = "Assets/Materials/",
            [ToolParam("Replace the material already at that path instead of refusing", Required = false)] bool overwrite = false)
        {
            // Before anything is allocated, and on the path actually written: the name is part of it,
            // so validating the directory alone leaves "../../x" as a name unchecked.
            if (!TryNormalizeAssetPath($"{save_path}/{name}.mat", out var fullPath))
                return ToolResultFormatter.Error("INVALID_PATH", new { save_path, name }, FolderPathHint);

            string materialAbsolute;
            try { materialAbsolute = PathSafety.ResolveAssetPath(fullPath); }
            catch (PathOutsideProjectException ex)
            { return ToolResultFormatter.Error("INVALID_PATH", new { path = fullPath, message = ex.Message }); }

            if (!ValueConverter.TryParseColor(color, Color.white, out var c, out var colorError))
                return ToolResultFormatter.Error("INVALID_PARAM",
                    new { param = "color", provided = color, detail = colorError },
                    "Use 'r,g,b,a' numbers, a #hex color, or a color name such as 'red'.");

            string actualShader = shader;
            string colorProperty = "_Color";

            if (shader == "Standard")
            {
                var pipeline = GraphicsSettings.currentRenderPipeline;
                if (pipeline != null)
                {
                    string pipelineName = pipeline.GetType().Name;
                    if (pipelineName.Contains("Universal") || pipelineName.Contains("URP"))
                    {
                        actualShader = "Universal Render Pipeline/Lit";
                        colorProperty = "_BaseColor";
                    }
                    else if (pipelineName.Contains("HD") || pipelineName.Contains("HDRP"))
                    {
                        actualShader = "HDRP/Lit";
                        colorProperty = "_BaseColor";
                    }
                }
            }
            else
            {
                if (shader.StartsWith("Universal Render Pipeline") || shader.StartsWith("HDRP"))
                    colorProperty = "_BaseColor";
            }

            var shaderObj = Shader.Find(actualShader);
            if (shaderObj == null)
                return ToolResultFormatter.Error("SHADER_NOT_FOUND", new { shader = actualShader });

            var material = new Material(shaderObj);
            material.name = name;

            if (material.HasProperty(colorProperty))
                material.SetColor(colorProperty, c);
            else
                material.color = c;

            Directory.CreateDirectory(Path.GetDirectoryName(materialAbsolute));

            // CreateAsset over an existing path REPLACES the asset's contents but keeps its GUID, so
            // every prefab and scene referencing that material silently gets this new one instead.
            // Refusing is recoverable; a reset material across a project is not.
            if (!overwrite && AssetDatabase.LoadAssetAtPath<Material>(fullPath) != null)
            {
                UnityEngine.Object.DestroyImmediate(material);
                return ToolResultFormatter.Error("MATERIAL_EXISTS", new { path = fullPath },
                    "A material already lives there and everything referencing it would be overwritten " +
                    "in place. Pass overwrite=true, or pick another name.");
            }

            AssetDatabase.CreateAsset(material, fullPath);
            AssetDatabase.Refresh();

            string pipelineInfo = actualShader != shader ? $" (auto-detected: {actualShader})" : "";
            return $"Created material '{name}' at {fullPath}{pipelineInfo}";
        }

        [Description("Assign a material to a GameObject's renderer")]
        [ToolProvider("Materials & Shaders")]
        public static string AssignMaterial(
            [ToolParam("GameObject name, hierarchy path, or instance ID. Finds inactive objects too.")] string game_object_name,
            [ToolParam("Path to the material asset")] string material_path)
        {
            var go = ObjectsHelper.FindTarget(game_object_name);
            if (go == null)
                return ObjectsHelper.NotFoundText("game_object_name", game_object_name);

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return ToolResultFormatter.Error("RENDERER_NOT_FOUND", new { game_object_name });

            var mat = AssetDatabase.LoadAssetAtPath<Material>(material_path);
            if (mat == null)
                return ToolResultFormatter.Error("MATERIAL_NOT_FOUND", new { material_path });

            Undo.RecordObject(renderer, $"Assign material to {go.name}");
            renderer.sharedMaterial = mat;
            return $"Assigned material '{mat.name}' to '{go.name}'";
        }

        [Description("Search for assets by type and name. Paths come back sorted, and a page cut short by " +
                     "max reports a next_cursor to pass back as cursor for the rest.")]
        [ReadOnlyTool]
        public static string FindAssets(
            [ToolParam("Search filter (e.g. 't:Material red', 't:Prefab Player', 't:Texture')")] string filter,
            [ToolParam("Maximum paths to return (1-500). Default 50.", Required = false)] int max = 50,
            [ToolParam(Paging.CursorParam, Required = false)] int cursor = 0,
            [ToolParam("Only search under this folder (e.g. 'Assets/Prefabs').", Required = false)] string folder = null)
        {
            if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder.TrimEnd('/')))
                return ToolResultFormatter.Error("INVALID_PARAM", new { param = nameof(folder), provided = folder },
                    "folder must be an existing project folder such as 'Assets/Prefabs'.");

            // Sorted because AssetDatabase.FindAssets promises no order, and paging an unordered
            // result set silently skips and repeats entries between calls.
            var paths = (string.IsNullOrEmpty(folder)
                    ? AssetDatabase.FindAssets(filter)
                    : AssetDatabase.FindAssets(filter, new[] { folder.TrimEnd('/') }))
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            if (paths.Count == 0)
                return $"No assets found for filter: {filter}" +
                       (filter != null && filter.Contains("t:")
                           ? " (Unity raises no error for a type name it does not know, so a misspelled 't:Name' looks the same as no match)"
                           : string.Empty);

            var pageSize = Mathf.Clamp(max, 1, 500);
            var page = Paging.Page(paths, cursor, pageSize);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Found {paths.Count} assets.{Paging.Suffix(cursor, page.Count, paths.Count)}" +
                          (pageSize != max ? $" (max {max} is outside 1-500; showing up to {pageSize})" : string.Empty));
            foreach (var path in page)
                sb.AppendLine($"  - {path}");
            return sb.ToString();
        }

        [Description("Delete an asset by moving it to the OS trash (Recycle Bin). Not an Editor undo step, but the file can be restored by hand from the trash.")]
        public static string DeleteAsset(
            [ToolParam("Path to the asset. The file is moved to the OS trash, so it can be recovered by hand.")] string path)
        {
            if (!TryResolveUnderAssetsOrPackages(path, out var normalized, out var absolutePath))
                return ToolResultFormatter.Error("INVALID_PATH", new { path }, AssetPathHint);

            if (normalized.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                return ToolResultFormatter.Error("INVALID_PATH", new { path },
                    "A .meta file is not an asset: trashing it resets the importer settings of the asset beside it. Delete the asset itself.");

            if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
                return ToolResultFormatter.Error("ASSET_NOT_FOUND", new { path });

            bool deleted = AssetDatabase.MoveAssetToTrash(normalized);
            return deleted ? $"Moved asset to OS trash: {normalized}" : ToolResultFormatter.Error("ASSET_DELETE_FAILED", new { path });
        }

        [Description("Rename an asset")]
        public static string RenameAsset(
            [ToolParam("Current path of the asset")] string path,
            [ToolParam("New name (without extension)")] string new_name)
        {
            var result = AssetDatabase.RenameAsset(path, new_name);
            return string.IsNullOrEmpty(result)
                ? $"Renamed to '{new_name}'"
                : ToolResultFormatter.Error("ASSET_RENAME_FAILED", new { path, new_name, message = result });
        }

        [Description("Copy an asset to a new location. Refuses to replace an existing asset unless overwrite=true.")]
        public static string CopyAsset(
            [ToolParam("Source asset path")] string source_path,
            [ToolParam("Destination asset path")] string destination_path,
            [ToolParam("Replace an asset that already exists at destination_path (it gets a new GUID)", Required = false)] bool overwrite = false)
        {
            // Unity answers a source outside the project with a modal dialog that freezes the editor,
            // so both paths are settled before the disk or AssetDatabase is touched.
            if (!TryResolveUnderAssetsOrPackages(source_path, out var source, out var sourceAbsolute))
                return ToolResultFormatter.Error("INVALID_PATH", new { source_path }, AssetPathHint);

            if (!TryResolveUnderAssetsOrPackages(destination_path, out var destination, out var destinationAbsolute)
                || !destination.StartsWith("Assets/", StringComparison.Ordinal))
                return ToolResultFormatter.Error("INVALID_PATH", new { destination_path },
                    "destination_path must be under Assets/, with no '..' segments.");

            if (!File.Exists(sourceAbsolute) && !Directory.Exists(sourceAbsolute))
                return ToolResultFormatter.Error("ASSET_NOT_FOUND", new { source_path });

            if (string.Equals(sourceAbsolute, destinationAbsolute, StringComparison.OrdinalIgnoreCase))
                return ToolResultFormatter.Error("INVALID_PATH", new { source_path, destination_path },
                    "destination_path is the source itself; a copy needs a path of its own.");

            if (!overwrite && (File.Exists(destinationAbsolute) || Directory.Exists(destinationAbsolute)))
                return ToolResultFormatter.Error("ASSET_EXISTS", new { destination_path },
                    "An asset is already at destination_path and a copy would replace it under a new GUID, breaking every reference to it. " +
                    "Pass overwrite=true to replace it, or choose another path.");

            var dir = Path.GetDirectoryName(destinationAbsolute);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bool copied = AssetDatabase.CopyAsset(source, destination);
            return copied
                ? $"Copied '{source}' to '{destination}'"
                : ToolResultFormatter.Error("ASSET_COPY_FAILED", new { source_path, destination_path });
        }

        [Description("Move or rename an asset to a new path (creates missing destination folders). Use to reorganize the project; keeps GUID/references intact.")]
        public static string MoveAsset(
            [ToolParam("Source asset path (e.g. 'Assets/Old/Player.prefab')")] string source_path,
            [ToolParam("Destination asset path (e.g. 'Assets/Characters/Player.prefab')")] string destination_path)
        {
            if (!TryResolveUnderAssetsOrPackages(source_path, out var source, out var sourceAbsolute))
                return ToolResultFormatter.Error("INVALID_PATH", new { source_path }, AssetPathHint);

            if (!TryResolveUnderAssetsOrPackages(destination_path, out var destination, out _))
                return ToolResultFormatter.Error("INVALID_PATH", new { destination_path }, AssetPathHint);

            if (!File.Exists(sourceAbsolute) && !Directory.Exists(sourceAbsolute))
                return ToolResultFormatter.Error("ASSET_NOT_FOUND", new { source_path });

            var dir = destination.Substring(0, destination.LastIndexOf('/'));
            if (!AssetDatabase.IsValidFolder(dir))
                CreateFolderRecursive(dir);

            var result = AssetDatabase.MoveAsset(source, destination);
            return string.IsNullOrEmpty(result)
                ? $"Moved '{source}' to '{destination}'"
                : ToolResultFormatter.Error("ASSET_MOVE_FAILED", new { source_path, destination_path, message = result });
        }

        [Description("Create a project folder (and any missing parent folders) under Assets. No-op if it already exists.")]
        public static string CreateFolder(
            [ToolParam("Folder path under Assets (e.g. 'Assets/Art/Characters')")] string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return ToolResultFormatter.Error("INVALID_PATH", new { path });

            var segments = path.Replace('\\', '/').TrimEnd('/').Split('/');
            if (segments[0] != "Assets")
                return ToolResultFormatter.Error("PATH_NOT_UNDER_ASSETS", new { path });

            // Unity creates the folder under a sanitised name and still reports failure, so the
            // check has to come before anything is created.
            var badSegment = segments.Skip(1).FirstOrDefault(s => !IsValidFolderName(s));
            if (badSegment != null)
                return ToolResultFormatter.Error("INVALID_PATH", new { path, segment = badSegment },
                    "Each folder name must be non-empty, free of characters a file name cannot hold, must not start or end with a dot or space, and must not end with '~'.");

            var folder = string.Join("/", segments);
            if (AssetDatabase.IsValidFolder(folder))
                return $"Folder already exists: {path}";

            CreateFolderRecursive(folder);
            return AssetDatabase.IsValidFolder(folder)
                ? $"Created folder: {path}"
                : ToolResultFormatter.Error("FOLDER_CREATE_FAILED", new { path });
        }

        private static readonly char[] InvalidNameChars =
            Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).Distinct().ToArray();

        // Unity renames or hides an asset whose name has a leading or trailing dot or space or a
        // character a file name cannot hold, so the one that appears is not the one asked for.
        internal static bool IsValidAssetName(string name) =>
            !string.IsNullOrEmpty(name) && name.Trim(' ', '.') == name && name.IndexOfAny(InvalidNameChars) < 0;

        // Unity skips a folder ending in '~' on import, so an asset saved inside one never shows up.
        internal static bool IsValidFolderName(string name) =>
            IsValidAssetName(name) && !name.EndsWith("~", StringComparison.Ordinal);

        private const string AssetPathHint =
            "The path must be an asset under Assets/ or Packages/ (not those folders themselves), with no '..' segments.";

        private const string FolderPathHint =
            "save_path must be a folder under Assets/ with no '..' segments, and no folder or file name that starts or ends " +
            "with a dot or space, ends with '~', or holds a character a file name cannot.";

        // The one spelling that reaches Unity: forward slashes, no empty segments, no trailing '.' or
        // space (Windows drops them, so a check for ".meta" on the raw text would miss ".meta."). A path
        // that is only Assets or Packages names the root folder, never an asset, and is refused.
        internal static bool TryNormalizeAssetPath(string path, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            path = AuthoringRoot.Resolve(path);
            var segments = path.Replace('\\', '/').Split('/').Where(s => s.Length > 0).ToArray();
            if (segments.Length < 2)
                return false;

            if (string.Equals(segments[0], "Assets", StringComparison.OrdinalIgnoreCase))
                segments[0] = "Assets";
            else if (string.Equals(segments[0], "Packages", StringComparison.OrdinalIgnoreCase))
                segments[0] = "Packages";
            else
                return false;

            var last = segments.Length - 1;
            segments[last] = segments[last].TrimEnd('.', ' ');
            if (!segments.Skip(1).All(IsValidFolderName))
                return false;

            normalized = string.Join("/", segments);
            if (AuthoringRoot.Contains(normalized))
                return true;

            normalized = null;
            return false;
        }

        internal static bool TryResolveUnderAssetsOrPackages(string path, out string normalized, out string absolutePath)
        {
            absolutePath = null;
            if (!TryNormalizeAssetPath(path, out normalized))
                return false;

            try { absolutePath = PathSafety.ResolveProjectPath(normalized); }
            catch (PathOutsideProjectException) { return false; }

            var root = ApplicationPaths.ProjectRoot;
            return PathSafety.IsInsideDirectory(absolutePath, Path.Combine(root, "Assets"))
                || PathSafety.IsInsideDirectory(absolutePath, Path.Combine(root, "Packages"));
        }

        internal static void CreateFolderRecursive(string folder)
        {
            folder = folder.Replace('\\', '/');
            var parts = folder.Split('/');
            var current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

    }
}
