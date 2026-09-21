// Copyright (C) KitWright. Licensed under MIT.
using System;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using System.Linq;
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
        public static string CreateMaterial(
            [ToolParam("Name of the material")] string name,
            [ToolParam("Color as 'r,g,b,a' or hex '#RRGGBB'", Required = false)] string color = "1,1,1,1",
            [ToolParam("Shader name (default: Standard)", Required = false)] string shader = "Standard",
            [ToolParam("Save path (e.g. 'Assets/Materials/')", Required = false)] string save_path = "Assets/Materials/",
            [ToolParam("Replace the material already at that path instead of refusing", Required = false)] bool overwrite = false)
        {
            // Before anything is allocated, and on the path actually written: the name is part of it,
            // so validating the directory alone leaves "../../x" as a name unchecked.
            var fullPath = $"{save_path.TrimEnd('/', '\\')}/{name}.mat";
            try { PathSafety.ResolveAssetPath(fullPath); }
            catch (PathOutsideProjectException ex)
            { return ToolResultFormatter.Error("INVALID_PATH", new { path = fullPath, message = ex.Message }); }

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

            var c = ValueConverter.ParseColor(color, Color.white);
            if (material.HasProperty(colorProperty))
                material.SetColor(colorProperty, c);
            else
                material.color = c;

            if (!Directory.Exists(save_path))
                Directory.CreateDirectory(save_path);

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
            [ToolParam(Paging.CursorParam, Required = false)] int cursor = 0)
        {
            // Sorted because AssetDatabase.FindAssets promises no order, and paging an unordered
            // result set silently skips and repeats entries between calls.
            var paths = AssetDatabase.FindAssets(filter)
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            if (paths.Count == 0)
                return $"No assets found for filter: {filter}";

            var page = Paging.Page(paths, cursor, Mathf.Clamp(max, 1, 500));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Found {paths.Count} assets.{Paging.Suffix(cursor, page.Count, paths.Count)}");
            foreach (var path in page)
                sb.AppendLine($"  - {path}");
            return sb.ToString();
        }

        [Description("Delete an asset by moving it to the OS trash (Recycle Bin). Not an Editor undo step, but the file can be restored by hand from the trash.")]
        public static string DeleteAsset(
            [ToolParam("Path to the asset. The file is moved to the OS trash, so it can be recovered by hand.")] string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return ToolResultFormatter.Error("ASSET_NOT_FOUND", new { path });

            bool deleted = AssetDatabase.MoveAssetToTrash(path);
            return deleted ? $"Moved asset to OS trash: {path}" : ToolResultFormatter.Error("ASSET_DELETE_FAILED", new { path });
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

        [Description("Copy an asset to a new location")]
        public static string CopyAsset(
            [ToolParam("Source asset path")] string source_path,
            [ToolParam("Destination asset path")] string destination_path)
        {
            try { PathSafety.ResolveAssetPath(destination_path); }
            catch (PathOutsideProjectException ex)
            { return ToolResultFormatter.Error("INVALID_PATH", new { destination_path, message = ex.Message }); }

            var dir = Path.GetDirectoryName(destination_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bool copied = AssetDatabase.CopyAsset(source_path, destination_path);
            return copied
                ? $"Copied '{source_path}' to '{destination_path}'"
                : ToolResultFormatter.Error("ASSET_COPY_FAILED", new { source_path, destination_path });
        }

        [Description("Move or rename an asset to a new path (creates missing destination folders). Use to reorganize the project; keeps GUID/references intact.")]
        public static string MoveAsset(
            [ToolParam("Source asset path (e.g. 'Assets/Old/Player.prefab')")] string source_path,
            [ToolParam("Destination asset path (e.g. 'Assets/Characters/Player.prefab')")] string destination_path)
        {
            if (!File.Exists(source_path) && !Directory.Exists(source_path))
                return ToolResultFormatter.Error("ASSET_NOT_FOUND", new { source_path });

            var dir = Path.GetDirectoryName(destination_path);
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                CreateFolderRecursive(dir);

            var result = AssetDatabase.MoveAsset(source_path, destination_path);
            return string.IsNullOrEmpty(result)
                ? $"Moved '{source_path}' to '{destination_path}'"
                : ToolResultFormatter.Error("ASSET_MOVE_FAILED", new { source_path, destination_path, message = result });
        }

        [Description("Create a project folder (and any missing parent folders) under Assets. No-op if it already exists.")]
        public static string CreateFolder(
            [ToolParam("Folder path under Assets (e.g. 'Assets/Art/Characters')")] string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return ToolResultFormatter.Error("INVALID_PATH", new { path });
            if (!path.Replace('\\', '/').StartsWith("Assets"))
                return ToolResultFormatter.Error("PATH_NOT_UNDER_ASSETS", new { path });

            if (AssetDatabase.IsValidFolder(path))
                return $"Folder already exists: {path}";

            CreateFolderRecursive(path);
            return AssetDatabase.IsValidFolder(path)
                ? $"Created folder: {path}"
                : ToolResultFormatter.Error("FOLDER_CREATE_FAILED", new { path });
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
