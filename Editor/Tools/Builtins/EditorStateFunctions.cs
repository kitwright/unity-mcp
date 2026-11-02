// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Services;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// Read and mutate editor-level state: selection, prefab stage, tags, layers, windows,
    /// active tool, and build settings. These tools fill the gap that previously forced agents
    /// to write <c>execute_code</c> snippets just to inspect the editor.
    /// </summary>
    [ToolProvider("EditorState")]
    internal static class EditorStateFunctions
    {
        [Description("Get high-level editor runtime state: which project this editor has open, play mode, paused, compiling, updating, time-since-startup, time scale. " +
                     "Call this first when several editors may be running to confirm which project the server is attached to.")]
        [ReadOnlyTool]
        [ReplaySafeTool]
        public static object GetEditorState()
        {
            // Name alone does not identify an editor -- two clones of the same project share it,
            // which is why the transport's project pin is a hash of the path rather than the name.
            var projectPath = ApplicationPaths.ProjectRoot.Replace('\\', '/');

            return Response.Success("Editor state", new
            {
                projectName = Application.productName,
                projectPath,
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                isCompiling = CompilationService.IsActuallyCompiling,
                isUpdating = EditorApplication.isUpdating,
                isPlayingOrWillChange = EditorApplication.isPlayingOrWillChangePlaymode,
                applicationPath = EditorApplication.applicationPath,
                timeSinceStartup = EditorApplication.timeSinceStartup,
                timeScale = Time.timeScale,
                unityVersion = Application.unityVersion
            });
        }

        // ----- Selection -----

        [Description("Get the current Hierarchy selection: instance ids, names, and paths.")]
        [ReadOnlyTool]
        public static object GetSelection()
        {
            var gos = Selection.gameObjects ?? Array.Empty<GameObject>();
            var items = gos.Select(go => new
            {
                instanceId = ObjectIdCodec.GetSerializableId(go),
                name = go.name,
                path = ObjectsHelper.GetGameObjectPath(go)
            }).ToList();

            return Response.Success($"Selection contains {items.Count} object(s).", new
            {
                count = items.Count,
                activeInstanceId = ObjectIdCodec.GetSerializableId(Selection.activeGameObject),
                items
            });
        }

        [Description("Replace the current selection. Pass either a comma-separated list of instance ids, names, or paths. " +
                     "Use find_method to control how each token is resolved (by_id, by_name, by_path). " +
                     "Duplicate tokens count once; tokens that match nothing are listed in notFound. " +
                     "If no token resolves the call fails and the current selection is left untouched; an empty targets clears the selection. " +
                     "An inactive object is not found by name: give its hierarchy path or instance id.")]
        public static object SetSelection(
            [ToolParam("Comma-separated instance ids, names, or paths")] string targets,
            [ToolParam("How to resolve each token: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null)
        {
            if (string.IsNullOrWhiteSpace(targets))
            {
                Selection.objects = Array.Empty<UnityEngine.Object>();
                return Response.Success("Cleared selection.");
            }

            var tokens = targets.Split(',');
            var picked = new List<GameObject>();
            var missing = new List<string>();

            foreach (var raw in tokens)
            {
                var token = raw.Trim();
                if (string.IsNullOrEmpty(token)) continue;
                var go = ObjectsHelper.FindObject(token, find_method);
                if (go == null)
                {
                    if (!missing.Contains(token)) missing.Add(token);
                }
                else if (!picked.Contains(go)) picked.Add(go);
            }

            if (picked.Count == 0)
                return Response.Error("NO_TARGETS_RESOLVED", new { targets, find_method, notFound = missing },
                    "Nothing matched, so the current selection was left as it was.");

            Selection.objects = picked.Cast<UnityEngine.Object>().ToArray();
            Selection.activeGameObject = picked[0];

            return Response.Success(
                $"Selected {picked.Count} object(s){(missing.Count > 0 ? $", {missing.Count} not found" : string.Empty)}.",
                new
                {
                    selected = picked.Select(g => new { instanceId = ObjectIdCodec.GetSerializableId(g), name = g.name }).ToList(),
                    notFound = missing
                });
        }

        // ----- Prefab Stage -----

        [Description("Get info about the currently open prefab stage (if any). Returns prefab asset path, root instance id, and dirty state.")]
        [ReadOnlyTool]
        public static object GetPrefabStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return Response.Success("No prefab stage open.", new { open = false });

            var root = stage.prefabContentsRoot;
            return Response.Success("Prefab stage open.", new
            {
                open = true,
                assetPath = stage.assetPath,
                rootInstanceId = ObjectIdCodec.GetSerializableId(root),
                rootName = root != null ? root.name : null,
                mode = stage.mode.ToString(),
                scene = stage.scene.IsValid() ? stage.scene.name : null
            });
        }

        // ----- Active Tool -----

        [Description("Get the currently active editor manipulation tool (Move/Rotate/Scale/Rect/Transform/View/None/Custom).")]
        [ReadOnlyTool]
        public static object GetActiveTool()
        {
            return Response.Success("Active tool.", new
            {
                tool = UnityEditor.Tools.current.ToString(),
                pivotMode = UnityEditor.Tools.pivotMode.ToString(),
                pivotRotation = UnityEditor.Tools.pivotRotation.ToString()
            });
        }

        [Description("Set the active editor manipulation tool. Accepts: View, Move, Rotate, Scale, Rect, Transform, None.")]
        public static object SetActiveTool(
            [ToolParam("Tool name: View, Move, Rotate, Scale, Rect, Transform, None")] string tool)
        {
            if (!ValueConverter.TryParseEnum<UnityEditor.Tool>(tool, out var parsed) || parsed == UnityEditor.Tool.Custom)
                return Response.Error("INVALID_TOOL",
                    new
                    {
                        tool,
                        accepted = Enum.GetNames(typeof(UnityEditor.Tool)).Where(n => n != nameof(UnityEditor.Tool.Custom)).ToArray()
                    });

            UnityEditor.Tools.current = parsed;
            return Response.Success($"Active tool set to {parsed}.");
        }

        // ----- Windows -----

        [Description("List all currently open EditorWindows: title, type, and focused state.")]
        [ReadOnlyTool]
        public static object GetWindows()
        {
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var focused = EditorWindow.focusedWindow;
            var items = windows
                .Where(w => w != null)
                .Select(w => new
                {
                    title = w.titleContent != null ? w.titleContent.text : w.GetType().Name,
                    type = w.GetType().FullName,
                    focused = w == focused,
                    docked = w.docked
                })
                .ToList();
            return Response.Success($"{items.Count} window(s) open.", items);
        }

        // ----- Tags -----

        [Description("List every defined tag in TagManager.")]
        [ReadOnlyTool]
        public static object GetTags()
        {
            var tags = UnityEditorInternal.InternalEditorUtility.tags;
            return Response.Success($"{tags.Length} tag(s).", tags);
        }

        [Description("Add a new tag to TagManager (no-op if it already exists).")]
        public static object AddTag(
            [ToolParam("Tag name to add")] string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return Response.Error("INVALID_TAG", new { message = "Tag name cannot be empty." });
            tag = tag.Trim();

            if (UnityEditorInternal.InternalEditorUtility.tags.Contains(tag))
                return Response.Success($"Tag '{tag}' already exists.");

            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var tagsProp = tagManager.FindProperty("tags");
            tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
            tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = tag;
            tagManager.ApplyModifiedProperties();
            FlushTagManager();
            return Response.Success($"Added tag '{tag}'.");
        }

        [Description("Remove a tag from TagManager. Warning: does not check whether any GameObject is still using the tag.")]
        public static object RemoveTag(
            [ToolParam("Tag name to remove")] string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return Response.Error("INVALID_TAG", new { message = "Tag name cannot be empty." });
            tag = tag.Trim();

            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var tagsProp = tagManager.FindProperty("tags");
            for (int i = 0; i < tagsProp.arraySize; i++)
            {
                if (tagsProp.GetArrayElementAtIndex(i).stringValue == tag)
                {
                    tagsProp.DeleteArrayElementAtIndex(i);
                    tagManager.ApplyModifiedProperties();
                    FlushTagManager();
                    return Response.Success($"Removed tag '{tag}'.");
                }
            }

            // get_tags lists the built-in tags but TagManager holds only the custom ones, so a built-in
            // tag is not "not found": it exists and cannot be removed.
            if (UnityEditorInternal.InternalEditorUtility.tags.Contains(tag))
                return Response.Error("CANNOT_REMOVE_BUILTIN_TAG", new { tag }, "Built-in tags cannot be removed.");
            return Response.Error("TAG_NOT_FOUND", new { tag });
        }

        // ApplyModifiedProperties only marks TagManager dirty; without a save the change sits in
        // memory and a batch run that never saves loses it. The same flush the sorting layers do.
        private static void FlushTagManager()
        {
            AssetDatabase.SaveAssets();
        }

        // ----- Layers -----

        [Description("List all 32 layer slots with their names (empty for unused slots).")]
        [ReadOnlyTool]
        public static object GetLayers()
        {
            var layers = new List<object>(32);
            for (int i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                layers.Add(new { index = i, name = string.IsNullOrEmpty(name) ? null : name });
            }
            return Response.Success("Layer table.", layers);
        }

        [Description("Add a new layer to the first empty user slot (8-31). Errors if no slot is free; a no-op success if the layer already exists.")]
        public static object AddLayer(
            [ToolParam("Layer name to add")] string layer)
        {
            if (string.IsNullOrWhiteSpace(layer))
                return Response.Error("INVALID_LAYER", new { message = "Layer name cannot be empty." });
            layer = layer.Trim();

            if (LayerMask.NameToLayer(layer) != -1)
                return Response.Success($"Layer '{layer}' already exists at index {LayerMask.NameToLayer(layer)}.");

            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layersProp = tagManager.FindProperty("layers");
            for (int i = 8; i < layersProp.arraySize; i++)
            {
                var slot = layersProp.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(slot.stringValue))
                {
                    slot.stringValue = layer;
                    tagManager.ApplyModifiedProperties();
                    FlushTagManager();
                    return Response.Success($"Added layer '{layer}' at index {i}.");
                }
            }
            return Response.Error("NO_FREE_LAYER", new { message = "All user layer slots (8-31) are taken." });
        }

        [Description("Remove a user layer (slot 8-31) by name. Built-in layers (0-7) cannot be removed.")]
        public static object RemoveLayer(
            [ToolParam("Layer name to remove")] string layer)
        {
            if (string.IsNullOrWhiteSpace(layer))
                return Response.Error("INVALID_LAYER", new { message = "Layer name cannot be empty." });
            layer = layer.Trim();

            int idx = LayerMask.NameToLayer(layer);
            if (idx == -1)
                return Response.Error("LAYER_NOT_FOUND", new { layer });
            if (idx < 8)
                return Response.Error("CANNOT_REMOVE_BUILTIN_LAYER", new { layer, index = idx });

            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layersProp = tagManager.FindProperty("layers");
            layersProp.GetArrayElementAtIndex(idx).stringValue = string.Empty;
            tagManager.ApplyModifiedProperties();
            FlushTagManager();
            return Response.Success($"Removed layer '{layer}' (was index {idx}).",
                hint: "GameObjects on this layer, camera culling masks and the collision matrix keep slot " + idx + "; they are not cleaned up.");
        }

        // ----- Sorting Layers -----

        [Description("List all sorting layers (2D render order) with their names.")]
        [ReadOnlyTool]
        public static object GetSortingLayers()
        {
            // Read from TagManager, not from SortingLayer.layers. That property is a native cache which
            // a SerializedObject write does not refresh, so a layer add_sorting_layer had just written
            // was missing from this list for the rest of the session - and the duplicate check below
            // read the same stale cache, so adding the name again passed and wrote it twice.
            var layers = new List<object>();
            var layersProp = SortingLayersProperty(out _);
            for (int i = 0; i < layersProp.arraySize; i++)
            {
                var element = layersProp.GetArrayElementAtIndex(i);
                var name = element.FindPropertyRelative("name").stringValue;
                var uniqueId = element.FindPropertyRelative("uniqueID").uintValue;
                var layer = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["id"] = unchecked((int)uniqueId),
                    ["value"] = i
                };
                if (uniqueId == 0 && name != "Default")
                    layer["warning"] = "id 0 belongs to Default, so Unity treats this layer as Default. Remove it and add it again to give it its own id.";
                layers.Add(layer);
            }

            return Response.Success($"{layers.Count} sorting layer(s).", layers);
        }

        private static SerializedProperty SortingLayersProperty(out SerializedObject tagManager)
        {
            tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            return tagManager.FindProperty("m_SortingLayers");
        }

        private static bool HasSortingLayer(string name)
        {
            if (name == "Default")
                return true;

            var layersProp = SortingLayersProperty(out _);
            for (int i = 0; i < layersProp.arraySize; i++)
            {
                if (layersProp.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue == name)
                    return true;
            }

            return false;
        }

        [Description("Add a new sorting layer (used to order 2D sprites/renderers). Errors if the name already exists.")]
        public static object AddSortingLayer(
            [ToolParam("Sorting layer name to add")] string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Response.Error("INVALID_NAME", new { message = "Sorting layer name cannot be empty." });
            if (HasSortingLayer(name))
                return Response.Error("SORTING_LAYER_EXISTS", new { name });

            var layersProp = SortingLayersProperty(out var tagManager);
            int idx = layersProp.arraySize;
            var usedIds = new HashSet<uint>();
            for (int i = 0; i < idx; i++)
                usedIds.Add(layersProp.GetArrayElementAtIndex(i).FindPropertyRelative("uniqueID").uintValue);

            var id = NextSortingLayerId(name, usedIds);
            layersProp.InsertArrayElementAtIndex(idx);
            var element = layersProp.GetArrayElementAtIndex(idx);
            element.FindPropertyRelative("name").stringValue = name;
            element.FindPropertyRelative("uniqueID").uintValue = id;
            tagManager.ApplyModifiedProperties();
            FlushSortingLayers();

            return Response.Success($"Added sorting layer '{name}'.", new { name, index = idx, id = unchecked((int)id) });
        }

        // uniqueID is unsigned and 0 belongs to Default, so a raw (possibly negative) string hash
        // is no id. Stays within int.MaxValue so a signed reader sees the same number.
        internal static uint NextSortingLayerId(string name, ICollection<uint> usedIds)
        {
            var id = (uint)(name.GetHashCode() & int.MaxValue);
            while (id == 0 || usedIds.Contains(id))
                id = id % int.MaxValue + 1;
            return id;
        }

        // ApplyModifiedProperties marks the settings object dirty; nothing writes it out until Unity
        // decides to, and a batch run that never saves loses the change. The same flush the settings
        // writer does.
        private static void FlushSortingLayers()
        {
            AssetDatabase.SaveAssets();
        }

        [Description("Rename an existing sorting layer. The built-in 'Default' layer cannot be renamed, and the new name must not already be a sorting layer.")]
        public static object RenameSortingLayer(
            [ToolParam("Current sorting layer name")] string old_name,
            [ToolParam("New name")] string new_name)
        {
            if (old_name == "Default")
                return Response.Error("CANNOT_RENAME_DEFAULT", new { old_name });
            if (string.IsNullOrWhiteSpace(new_name))
                return Response.Error("INVALID_NAME", new { message = "New name cannot be empty." });

            var layersProp = SortingLayersProperty(out var tagManager);
            SerializedProperty oldNameProp = null;
            for (int i = 0; i < layersProp.arraySize && oldNameProp == null; i++)
            {
                var nameProp = layersProp.GetArrayElementAtIndex(i).FindPropertyRelative("name");
                if (nameProp.stringValue == old_name)
                    oldNameProp = nameProp;
            }

            if (oldNameProp == null)
                return Response.Error("SORTING_LAYER_NOT_FOUND", new { old_name });
            if (old_name == new_name)
                return Response.Success($"Sorting layer '{old_name}' already has that name; nothing changed.");
            if (HasSortingLayer(new_name))
                return Response.Error("SORTING_LAYER_EXISTS", new { name = new_name });

            oldNameProp.stringValue = new_name;
            tagManager.ApplyModifiedProperties();
            FlushSortingLayers();
            return Response.Success($"Renamed sorting layer '{old_name}' to '{new_name}'.");
        }

        [Description("Remove a sorting layer by name. The built-in 'Default' layer cannot be removed.")]
        public static object RemoveSortingLayer(
            [ToolParam("Sorting layer name to remove")] string name)
        {
            if (name == "Default")
                return Response.Error("CANNOT_REMOVE_DEFAULT", new { name });

            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layersProp = tagManager.FindProperty("m_SortingLayers");
            for (int i = 0; i < layersProp.arraySize; i++)
            {
                if (layersProp.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue == name)
                {
                    layersProp.DeleteArrayElementAtIndex(i);
                    tagManager.ApplyModifiedProperties();
                    FlushSortingLayers();
                    return Response.Success($"Removed sorting layer '{name}'.");
                }
            }
            return Response.Error("SORTING_LAYER_NOT_FOUND", new { name });
        }

        // ----- Build Settings -----

        [Description("Get build settings: enabled scenes (in build order), active build target, scripting backend.")]
        [ReadOnlyTool]
        public static object GetBuildSettings()
        {
            var scenes = EditorBuildSettings.scenes
                .Select((s, i) => new { index = i, path = s.path, enabled = s.enabled, guid = s.guid.ToString() })
                .ToList();

            return Response.Success("Build settings.", new
            {
                activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                selectedBuildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup.ToString(),
                scenes,
                sceneCount = scenes.Count,
                enabledSceneCount = scenes.Count(s => s.enabled)
            });
        }
    }
}
