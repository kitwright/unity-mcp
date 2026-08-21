// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Reflection;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Scene")]
    internal static class SceneFunctions
    {
        [Description("Save the current scene. A scene that has never been saved needs an explicit path, " +
                     "otherwise Unity would open a file picker.")]
        public static string SaveScene(
            [ToolParam("Where to save (e.g. 'Assets/Scenes/Main.unity'). Only for a scene that has no path yet; a different path for an already-saved scene is refused.", Required = false)] string path = null)
        {
            var scene = EditorSceneManager.GetActiveScene();

            if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(scene.path))
                return ToolResultFormatter.Error("SCENE_HAS_NO_PATH", new
                {
                    scene = scene.name,
                    hint = "This scene was never saved. Pass path (e.g. 'Assets/Scenes/Main.unity')."
                });

            // SaveScene(scene, otherPath) is Save As, not a copy: it repoints the open scene at a
            // new asset with a new GUID and leaves the original holding the pre-edit content, while
            // every SceneAsset reference and the build list still names the original.
            if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(scene.path) &&
                !string.Equals(path, scene.path, StringComparison.OrdinalIgnoreCase))
                return ToolResultFormatter.Error("SCENE_ALREADY_HAS_PATH", new
                {
                    scene = scene.name,
                    scene_path = scene.path,
                    requested = path,
                    hint = "Call save_scene with no path to save it where it already lives."
                });

            if (string.IsNullOrEmpty(path))
                path = scene.path;

            return EditorSceneManager.SaveScene(scene, path)
                ? $"Saved scene '{scene.name}' to {path}"
                : ToolResultFormatter.Error("SCENE_SAVE_FAILED", new { scene = scene.name, path });
        }

        // Clearing the flag is what stops Unity raising its own save prompt. The API is internal.
        private static readonly MethodInfo ClearSceneDirtiness = typeof(EditorSceneManager)
            .GetMethod("ClearSceneDirtiness", BindingFlags.Static | BindingFlags.NonPublic);

        private static string DiscardUnavailableError()
        {
            return ToolResultFormatter.Error("DISCARD_UNAVAILABLE", new
            {
                hint = "This Unity version does not expose EditorSceneManager.ClearSceneDirtiness, so discarding " +
                       "would leave Unity to raise its own save dialog and block the editor. Use save_first=true instead."
            });
        }

        private const string PathlessSceneHint =
            "A scene that has never been saved has no path, so Unity would raise its save file picker - " +
            "a modal that blocks the editor loop. Save it by hand in Unity, or pass discard_unsaved=true.";

        private const string DirtySceneHint =
            "Pass discard_unsaved=true to drop the changes, or save_first=true to save them.";

        // Never call SaveCurrentModifiedScenesIfUserWantsTo: its modal dialog blocks the editor
        // main loop, which stalls the MCP request pump until a human clicks a button.
        internal static string UnsavedChangesError(bool discardUnsaved, bool saveFirst)
        {
            var failed = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isDirty)
                    continue;

                // Asked for explicitly, so it beats the save-by-default.
                if (discardUnsaved)
                {
                    if (ClearSceneDirtiness == null)
                        return DiscardUnavailableError();
                    ClearSceneDirtiness.Invoke(null, new object[] { s });
                }
                else if (!saveFirst || string.IsNullOrEmpty(s.path) || !EditorSceneManager.SaveScene(s))
                    failed.Add(DisplayName(s));
            }

            if (failed.Count == 0)
                return null;

            return ToolResultFormatter.Error(saveFirst ? "SCENE_SAVE_FAILED" : "SCENE_HAS_UNSAVED_CHANGES", new
            {
                scenes = failed.ToArray(),
                hint = saveFirst ? PathlessSceneHint : DirtySceneHint
            });
        }

        private static string DisplayName(Scene s) => string.IsNullOrEmpty(s.path) ? s.name : s.path;

        [Description("Open an existing scene by path. Modified scenes are saved first by default; pass discard_unsaved to drop them instead, or save_first=false to fail rather than write.")]
        public static string OpenScene(
            [ToolParam("Path to the scene asset (e.g. 'Assets/Scenes/Main.unity')")] string path,
            [ToolParam("Drop unsaved changes in the currently open scenes. Takes precedence over save_first.", Required = false)] bool discard_unsaved = false,
            [ToolParam("Save modified open scenes before switching (default). A scene never saved to disk still fails, because saving it needs a modal file picker.", Required = false)] bool save_first = true)
        {
            if (!System.IO.File.Exists(path))
                return ToolResultFormatter.Error("SCENE_FILE_NOT_FOUND", new { path });

            var blocked = UnsavedChangesError(discard_unsaved, save_first);
            if (blocked != null)
                return blocked;

            EditorSceneManager.OpenScene(path);
            return $"Opened scene: {path}";
        }

        [Description("Open a scene additively (keeps currently open scenes loaded), for multi-scene editing. Use set_active_scene to make it the active scene afterward.")]
        public static string LoadSceneAdditive(
            [ToolParam("Path to the scene asset (e.g. 'Assets/Scenes/Enemies.unity')")] string path)
        {
            if (!System.IO.File.Exists(path))
                return ToolResultFormatter.Error("SCENE_FILE_NOT_FOUND", new { path });

            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            return $"Loaded scene additively: {scene.name} ({SceneManager.sceneCount} scene(s) open)";
        }

        [Description("Set which open scene is the active scene (new objects go here; lighting/nav settings follow it). The scene must already be open. Identify by name or path.")]
        public static string SetActiveScene(
            [ToolParam("Open scene name or path")] string scene)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.name == scene || s.path == scene)
                {
                    if (!s.isLoaded)
                        return ToolResultFormatter.Error("SCENE_NOT_LOADED", new { scene });
                    SceneManager.SetActiveScene(s);
                    return $"Active scene: {s.name}";
                }
            }
            return ToolResultFormatter.Error("SCENE_NOT_OPEN", new { scene, hint = "Open it first (open_scene or load_scene_additive)." });
        }

        [Description("Close/unload an open scene (used in multi-scene editing). Cannot close the only open scene. Identify by name or path; optionally remove it from the Hierarchy entirely. A modified scene is saved first by default; pass discard_unsaved to drop it instead, or save_first=false to fail rather than write.")]
        public static string CloseScene(
            [ToolParam("Open scene name or path")] string scene,
            [ToolParam("Remove the scene from the Hierarchy (true) or just unload it (false)", Required = false)] bool remove = true,
            [ToolParam("Drop unsaved changes in that scene. Takes precedence over save_first.", Required = false)] bool discard_unsaved = false,
            [ToolParam("Save that scene before closing (default). A scene never saved to disk still fails, because saving it needs a modal file picker.", Required = false)] bool save_first = true)
        {
            if (SceneManager.sceneCount <= 1)
                return ToolResultFormatter.Error("CANNOT_CLOSE_LAST_SCENE", new { hint = "At least one scene must stay open." });

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.name == scene || s.path == scene)
                {
                    if (s.isDirty && discard_unsaved)
                    {
                        if (ClearSceneDirtiness == null)
                            return DiscardUnavailableError();
                        ClearSceneDirtiness.Invoke(null, new object[] { s });
                    }
                    else if (s.isDirty && (!save_first || string.IsNullOrEmpty(s.path) || !EditorSceneManager.SaveScene(s)))
                        return ToolResultFormatter.Error(save_first ? "SCENE_SAVE_FAILED" : "SCENE_HAS_UNSAVED_CHANGES", new
                        {
                            scene = DisplayName(s),
                            hint = save_first ? PathlessSceneHint : DirtySceneHint
                        });

                    EditorSceneManager.CloseScene(s, remove);
                    return $"Closed scene: {scene}";
                }
            }
            return ToolResultFormatter.Error("SCENE_NOT_OPEN", new { scene });
        }

        [Description("Create a new empty scene. Modified scenes are saved first by default; pass discard_unsaved to drop them instead, or save_first=false to fail rather than write.")]
        public static string CreateNewScene(
            [ToolParam("Name for the new scene")] string name,
            [ToolParam("Path to save (e.g. 'Assets/Scenes/')", Required = false)] string save_path = "Assets/Scenes/",
            [ToolParam("Drop unsaved changes in the currently open scenes. Takes precedence over save_first.", Required = false)] bool discard_unsaved = false,
            [ToolParam("Save modified open scenes before switching (default). A scene never saved to disk still fails, because saving it needs a modal file picker.", Required = false)] bool save_first = true)
        {
            var fullPath = System.IO.Path.Combine(save_path, name + ".unity").Replace('\\', '/');
            PathSafety.ResolveProjectPath(fullPath);

            // Saving over an existing scene asset keeps its GUID, so the build list and every
            // SceneAsset reference would silently resolve to the new empty scene. Refuse before
            // NewScene runs, so a refusal leaves the open scene alone.
            if (System.IO.File.Exists(fullPath))
                return ToolResultFormatter.Error("SCENE_EXISTS", new { path = fullPath },
                    "create_new_scene never overwrites. Open it with open_scene, or pick another name.");

            var blocked = UnsavedChangesError(discard_unsaved, save_first);
            if (blocked != null)
                return blocked;

            var folder = System.IO.Path.GetDirectoryName(fullPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder))
                AssetFunctions.CreateFolderRecursive(folder);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            return EditorSceneManager.SaveScene(scene, fullPath)
                ? $"Created and saved new scene: {fullPath}"
                : ToolResultFormatter.Error("SCENE_SAVE_FAILED", new { path = fullPath });
        }

        [Description("Get information about every loaded scene (the active scene plus any additively loaded ones), " +
                     "including path, dirty state, and a shallow root-object hierarchy per scene.")]
        [ReadOnlyTool]
        public static string GetSceneInfo()
        {
            var activeScene = EditorSceneManager.GetActiveScene();
            var sb = new System.Text.StringBuilder();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                var rootObjects = scene.GetRootGameObjects();
                sb.AppendLine(scene == activeScene ? $"Scene: {scene.name} (active)" : $"Scene: {scene.name} (additive)");
                sb.AppendLine($"Path: {scene.path}");
                sb.AppendLine($"Is Dirty: {scene.isDirty}");
                sb.AppendLine($"Root Objects ({rootObjects.Length}):");

                foreach (var go in rootObjects)
                {
                    AppendHierarchy(sb, go.transform, 1, 3);
                }
            }

            return sb.ToString();
        }

        [Description("List all scenes in the project")]
        [ReadOnlyTool]
        public static string ListScenes()
        {
            var guids = AssetDatabase.FindAssets("t:Scene");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Found {guids.Length} scenes:");

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                sb.AppendLine($"  - {path}");
            }

            return sb.ToString();
        }

        [Description("Enter play mode in the editor")]
        public static string EnterPlayMode()
        {
            if (EditorApplication.isPlaying)
                return "Already in play mode";

            EditorApplication.isPlaying = true;
            return "Entering play mode";
        }

        [Description("Exit play mode in the editor")]
        public static string ExitPlayMode()
        {
            if (!EditorApplication.isPlaying)
                return "Not in play mode";

            EditorApplication.isPlaying = false;
            return "Exiting play mode";
        }

        [Description("Pause or resume play mode. Requires being in play mode. Use step_frame to advance one frame while paused.")]
        public static string SetPaused(
            [ToolParam("true to pause, false to resume")] bool paused)
        {
            if (!EditorApplication.isPlaying)
                return ToolResultFormatter.Error("NOT_IN_PLAY_MODE", new { hint = "Enter play mode first." });

            EditorApplication.isPaused = paused;
            return paused ? "Paused play mode" : "Resumed play mode";
        }

        [Description("Advance play mode by exactly one frame. Auto-pauses if running. Requires being in play mode. Useful for frame-by-frame debugging.")]
        public static string StepFrame()
        {
            if (!EditorApplication.isPlaying)
                return ToolResultFormatter.Error("NOT_IN_PLAY_MODE", new { hint = "Enter play mode first." });

            EditorApplication.isPaused = true;
            EditorApplication.Step();
            return "Stepped one frame";
        }

        [Description("Set the game time scale. Use 0 to pause, 1 for normal speed, " +
                     "2 for double speed, etc. Useful for testing or slow-motion debugging.")]
        public static string SetTimeScale(
            [ToolParam("Time scale value (0=paused, 1=normal, 2=double speed, etc.)")] float scale)
        {
            if (scale < 0f)
                return ToolResultFormatter.Error("INVALID_TIME_SCALE", new { scale, min = 0f });
            if (scale > 100f)
                return ToolResultFormatter.Error("INVALID_TIME_SCALE", new { scale, max = 100f });

            float previousScale = UnityEngine.Time.timeScale;
            UnityEngine.Time.timeScale = scale;
            return $"Time.timeScale changed from {previousScale:F2} to {scale:F2}";
        }

        private static void AppendHierarchy(System.Text.StringBuilder sb, Transform t, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            var indent = new string(' ', depth * 2);
            var components = t.GetComponents<Component>();
            var compNames = new System.Collections.Generic.List<string>();
            foreach (var c in components)
            {
                if (c != null && !(c is Transform))
                    compNames.Add(c.GetType().Name);
            }
            var compStr = compNames.Count > 0 ? $" [{string.Join(", ", compNames)}]" : "";
            sb.AppendLine($"{indent}- {t.name}{compStr}");

            for (int i = 0; i < t.childCount; i++)
            {
                AppendHierarchy(sb, t.GetChild(i), depth + 1, maxDepth);
            }
        }
    }
}
