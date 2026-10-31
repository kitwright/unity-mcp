// Copyright (C) KitWright. Licensed under MIT.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Prefab")]
    internal static class PrefabFunctions
    {
        [Description("Create a prefab from a GameObject in the scene. Refuses to replace an existing prefab unless overwrite=true. " +
                     "A GameObject that is already a prefab instance is saved as a Variant of its source, and the reply says so.")]
        public static object CreatePrefab(
            [ToolParam("GameObject name, hierarchy path, or instance ID. Finds inactive objects too.")] string game_object_name,
            [ToolParam("Folder to save into (e.g. 'Assets/Prefabs/'), or a full path ending in .prefab", Required = false)] string save_path = "Assets/Prefabs/",
            [ToolParam("Replace a prefab that already exists at that path", Required = false)] bool overwrite = false)
        {
            var go = ObjectsHelper.FindTarget(game_object_name);
            if (go == null)
                return ObjectsHelper.NotFound("game_object_name", game_object_name);

            // Path.Combine, not concatenation: save_path without a trailing slash used to give
            // 'Assets/PrefabsCube.prefab'. And the path is resolved before anything is created, so
            // a save_path of '../..' cannot make directories outside the project.
            var givenPath = save_path ?? string.Empty;
            var fullPath = (givenPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase)
                    ? givenPath
                    : Path.Combine(givenPath, go.name + ".prefab")).Replace('\\', '/');
            string absolutePath;
            try
            {
                absolutePath = PathSafety.ResolveAssetPath(fullPath);
            }
            catch (PathOutsideProjectException ex)
            {
                return Response.Error("INVALID_PATH", new { save_path, message = ex.Message },
                    "save_path must be under Assets/, and must stay there once '..' segments are resolved");
            }

            if (!overwrite && File.Exists(absolutePath))
                return Response.Error("PREFAB_EXISTS", new { path = fullPath },
                    "A prefab is already at this path. Pass overwrite=true to replace it, or choose another save_path.");

            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, fullPath, InteractionMode.UserAction);
            if (prefab == null)
                return Response.Error("PREFAB_CREATE_FAILED", new { path = fullPath });

            var sourcePath = source == null ? null : AssetDatabase.GetAssetPath(source);
            return Response.Success(string.IsNullOrEmpty(sourcePath) || string.Equals(sourcePath, fullPath, System.StringComparison.OrdinalIgnoreCase)
                ? $"Created prefab at {fullPath}"
                : $"Created prefab at {fullPath} as a Variant of {sourcePath}, because '{go.name}' is already a prefab instance");
        }

        [Description("Instantiate a prefab in the scene")]
        public static object InstantiatePrefab(
            [ToolParam("Path to the prefab asset")] string prefab_path,
            [ToolParam("Name for the instance", Required = false)] string name = null,
            [ToolParam("Position as 'x,y,z'", Required = false)] string position = "0,0,0")
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path);
            if (prefab == null)
                return Response.Error("PREFAB_NOT_FOUND", new { prefab_path });

            // Parsed before the instantiate: checking it after left the refused call's instance sitting
            // in the scene, unregistered with Undo, next to an answer that said the call had failed.
            if (!ValueConverter.TryParseVector3(position, out var pos, out var posErr))
                return Response.Error("INVALID_PARAM", new { param = "position", provided = position, expected = "Vector3 'x,y,z'", detail = posErr });

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null)
                return Response.Error("PREFAB_INSTANTIATE_FAILED", new { prefab_path });

            Undo.RegisterCreatedObjectUndo(instance, "Instantiate prefab");

            if (!string.IsNullOrEmpty(name))
                instance.name = name;

            instance.transform.position = pos;
            Selection.activeGameObject = instance;

            return Response.Success($"Instantiated prefab '{prefab.name}' as '{instance.name}' at {instance.transform.position}");
        }

        [Description("Unpack a prefab instance in the scene")]
        public static object UnpackPrefab(
            [ToolParam("Prefab instance name, hierarchy path, or instance ID. Finds inactive objects too.")] string game_object_name,
            [ToolParam("Unpack mode: 'completely' or 'outermost'", Required = false)] string mode = "completely")
        {
            var go = ObjectsHelper.FindTarget(game_object_name);
            if (go == null)
                return ObjectsHelper.NotFound("game_object_name", game_object_name);

            if (!PrefabUtility.IsPartOfAnyPrefab(go))
                return Response.Error("NOT_PREFAB_INSTANCE", new { game_object_name });

            var requested = string.IsNullOrWhiteSpace(mode) ? "completely" : mode.Trim().ToLowerInvariant();
            if (requested != "completely" && requested != "outermost")
                return Response.Error("INVALID_PARAM", new { param = "mode", provided = mode, expected = "'completely' or 'outermost'" });

            var unpackMode = requested == "outermost"
                ? PrefabUnpackMode.OutermostRoot
                : PrefabUnpackMode.Completely;

            PrefabUtility.UnpackPrefabInstance(go, unpackMode, InteractionMode.UserAction);
            return Response.Success($"Unpacked prefab '{go.name}' ({requested})");
        }

        [Description("Open a prefab asset in Prefab Mode (an isolated prefab stage) for editing its contents directly, " +
                     "without instantiating it into a scene. While the stage is open, hierarchy/component tools and " +
                     "execute_code operate on the prefab contents. Persist edits with save_prefab_stage, then " +
                     "close_prefab_stage when done. If another prefab stage is already open with unsaved changes, " +
                     "this returns an error instead of silently discarding them.")]
        public static object OpenPrefabStage(
            [ToolParam("Path to the prefab asset (e.g. 'Assets/Prefabs/Item.prefab')")] string prefab_path)
        {
            if (string.IsNullOrEmpty(prefab_path))
                return Response.Error("INVALID_ARGUMENT", new { prefab_path });

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path);
            if (prefab == null)
                return Response.Error("PREFAB_NOT_FOUND", new { prefab_path });

            var current = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (current != null)
            {
                if (current.assetPath == prefab_path)
                    return Response.Success(FormatPrefabStageStatus(current, "already open"));

                if (current.scene.isDirty)
                    return Response.Error("ANOTHER_STAGE_DIRTY", new
                    {
                        open_stage = current.assetPath,
                        hint = "Call save_prefab_stage or close_prefab_stage(save=false) first."
                    });
            }

            var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(prefab_path);
            if (stage == null)
                return Response.Error("PREFAB_STAGE_OPEN_FAILED", new { prefab_path });

            return Response.Success(FormatPrefabStageStatus(stage, "opened"));
        }

        private static string FormatPrefabStageStatus(
            UnityEditor.SceneManagement.PrefabStage stage,
            string status)
        {
            var root = stage.prefabContentsRoot;
            return $"Prefab stage {status}: {stage.assetPath}\n" +
                   $"Root: {root.name} (instanceId={ObjectIdCodec.GetSerializableId(root)}), children: {root.transform.childCount}";
        }

        [Description("Save the currently open prefab stage back to its .prefab asset, without closing the stage. " +
                     "Use after editing prefab contents via component tools or execute_code inside an open prefab stage.")]
        public static object SavePrefabStage()
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return Response.Error("NO_PREFAB_STAGE_OPEN", new { hint = "Call open_prefab_stage first." });

            var saved = PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath, out var success);
            if (!success || saved == null)
                return Response.Error("PREFAB_STAGE_SAVE_FAILED", new { stage.assetPath });

            stage.ClearDirtiness();
            return Response.Success($"Prefab stage saved: {stage.assetPath}");
        }

        [Description("Close the currently open prefab stage and return to the main stage. By default pending edits are " +
                     "saved first; pass save=false to DISCARD unsaved edits. Never shows a blocking save dialog: " +
                     "discarding clears the stage's dirty flag before closing.")]
        public static object ClosePrefabStage(
            [ToolParam("Save pending edits before closing (false discards them)", Required = false)] bool save = true)
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return Response.Error("NO_PREFAB_STAGE_OPEN", new { hint = "Nothing to close." });

            var assetPath = stage.assetPath;
            var wasDirty = stage.scene.isDirty;

            if (save && wasDirty)
            {
                PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, assetPath, out var success);
                if (!success)
                    return Response.Error("PREFAB_STAGE_SAVE_FAILED", new { assetPath });
            }

            // Clear the dirty flag before leaving the stage so Unity never pops a modal
            // "save changes?" dialog (a modal would block the MCP request indefinitely).
            stage.ClearDirtiness();
            UnityEditor.SceneManagement.StageUtility.GoToMainStage();

            var action = !wasDirty ? "no pending edits" : (save ? "edits saved" : "edits discarded");
            return Response.Success($"Prefab stage closed: {assetPath} ({action})");
        }

        [Description("Create a prefab variant from an existing prefab asset. The variant inherits from the base prefab and can override its properties independently. " +
                     "Refuses to replace an existing prefab unless overwrite=true.")]
        public static object CreatePrefabVariant(
            [ToolParam("Path to the base prefab asset (e.g. 'Assets/Prefabs/Enemy.prefab')")] string base_prefab_path,
            [ToolParam("Path to save the variant (e.g. 'Assets/Prefabs/FastEnemy.prefab')")] string variant_path,
            [ToolParam("Replace a prefab that already exists at variant_path", Required = false)] bool overwrite = false)
        {
            if (string.IsNullOrEmpty(base_prefab_path) || string.IsNullOrEmpty(variant_path))
                return Response.Error("INVALID_ARGUMENT", new { base_prefab_path, variant_path });

            string variantAbsolutePath;
            try { variantAbsolutePath = PathSafety.ResolveAssetPath(variant_path); }
            catch (PathOutsideProjectException ex)
            {
                return Response.Error("INVALID_PATH", new { variant_path, message = ex.Message },
                    "variant_path must be under Assets/, and must stay there once '..' segments are resolved");
            }

            string baseAbsolutePath = null;
            try { baseAbsolutePath = PathSafety.ResolveAssetPath(base_prefab_path); }
            catch (PathOutsideProjectException) { }

            if (string.Equals(baseAbsolutePath, variantAbsolutePath, System.StringComparison.OrdinalIgnoreCase))
                return Response.Error("INVALID_PATH", new { base_prefab_path, variant_path },
                    "variant_path is the base prefab itself; a variant needs a path of its own.");

            if (!overwrite && File.Exists(variantAbsolutePath))
                return Response.Error("PREFAB_EXISTS", new { variant_path },
                    "A prefab is already at variant_path. Pass overwrite=true to replace it, or choose another path.");

            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(base_prefab_path);
            if (basePrefab == null)
                return Response.Error("PREFAB_NOT_FOUND", new { base_prefab_path });

            var dir = Path.GetDirectoryName(variant_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            if (instance == null)
                return Response.Error("PREFAB_INSTANTIATE_FAILED", new { base_prefab_path });

            try
            {
                var variant = PrefabUtility.SaveAsPrefabAsset(instance, variant_path, out var success);
                if (!success || variant == null)
                    return Response.Error("VARIANT_CREATE_FAILED", new { variant_path });

                return Response.Success($"Created prefab variant '{variant.name}' at {variant_path} (base: {base_prefab_path})");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        [Description("Apply a prefab instance's overrides back to its source prefab asset, so the changes become part of the prefab itself.")]
        public static object ApplyPrefabOverrides(
            [ToolParam("Prefab instance name, hierarchy path, or instance ID. Finds inactive objects too.")] string game_object_name)
        {
            var go = ObjectsHelper.FindTarget(game_object_name);
            if (go == null)
                return ObjectsHelper.NotFound("game_object_name", game_object_name);

            if (!PrefabUtility.IsPartOfAnyPrefab(go))
                return Response.Error("NOT_PREFAB_INSTANCE", new { game_object_name });

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null)
                return Response.Error("NOT_PREFAB_INSTANCE", new { game_object_name });

            PrefabUtility.ApplyPrefabInstance(root, InteractionMode.UserAction);
            return Response.Success($"Applied overrides from '{root.name}' to its source prefab");
        }

        [Description("Revert a prefab instance's overrides, restoring it to match its source prefab asset.")]
        public static object RevertPrefabOverrides(
            [ToolParam("Prefab instance name, hierarchy path, or instance ID. Finds inactive objects too.")] string game_object_name)
        {
            var go = ObjectsHelper.FindTarget(game_object_name);
            if (go == null)
                return ObjectsHelper.NotFound("game_object_name", game_object_name);

            if (!PrefabUtility.IsPartOfAnyPrefab(go))
                return Response.Error("NOT_PREFAB_INSTANCE", new { game_object_name });

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null)
                return Response.Error("NOT_PREFAB_INSTANCE", new { game_object_name });

            PrefabUtility.RevertPrefabInstance(root, InteractionMode.UserAction);
            return Response.Success($"Reverted overrides on '{root.name}' to its source prefab");
        }

        [Description("Get prefab info for a scene instance or prefab asset: prefab type (Regular/Variant/Model), asset path, whether it is a variant, and its base prefab path.")]
        [ReadOnlyTool]
        public static object GetPrefabVariantInfo(
            [ToolParam("Prefab instance name/path/ID, OR a prefab asset path (e.g. 'Assets/Prefabs/X.prefab')")] string target)
        {
            if (string.IsNullOrEmpty(target))
                return Response.Error("INVALID_ARGUMENT", new { target });

            GameObject asset;
            string assetPath;

            if (target.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                asset = AssetDatabase.LoadAssetAtPath<GameObject>(target);
                if (asset == null)
                    return Response.Error("PREFAB_NOT_FOUND", new { target });
                assetPath = target;
            }
            else
            {
                var go = ObjectsHelper.FindTarget(target);
                if (go == null)
                    return ObjectsHelper.NotFound("target", target);
                if (!PrefabUtility.IsPartOfAnyPrefab(go))
                    return Response.Error("NOT_PREFAB_INSTANCE", new { target });
                asset = PrefabUtility.GetCorrespondingObjectFromSource(go);
                assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            }

            var prefabType = PrefabUtility.GetPrefabAssetType(asset);
            bool isVariant = prefabType == PrefabAssetType.Variant;
            string basePath = null;
            if (isVariant)
            {
                var baseAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                var source = PrefabUtility.GetCorrespondingObjectFromSource(baseAsset);
                if (source != null)
                    basePath = AssetDatabase.GetAssetPath(source);
            }

            return Response.Success($"Prefab info for '{asset.name}'.", new
            {
                name = asset.name,
                assetPath,
                prefabType = prefabType.ToString(),
                isVariant,
                basePrefabPath = basePath
            });
        }
    }
}
