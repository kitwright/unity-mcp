// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Editor")]
    internal static class EditorUtilityFunctions
    {
        private const int MaxCodeFileBytes = 256 * 1024;

#if UNITY_EDITOR_WIN
        private const int SwRestore = 9;

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
#endif

        [Description("Save every open scene that has unsaved changes, then every modified asset. A scene that was never saved " +
                     "(no path) is skipped and listed, since saving it would open a modal Save dialog. Edit Mode only.")]
        public static object SaveAll()
        {
            if (EditorApplication.isPlaying)
                return Response.Error("EDIT_MODE_REQUIRED", hint: "Scenes cannot be saved in Play Mode. Call exit_play_mode first.");

            var saved = new List<string>();
            var untitled = new List<string>();
            var failed = new List<string>();

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !scene.isDirty)
                    continue;

                if (string.IsNullOrEmpty(scene.path))
                    untitled.Add(scene.name);
                else if (EditorSceneManager.SaveScene(scene))
                    saved.Add(scene.path);
                else
                    failed.Add(scene.path);
            }

            AssetDatabase.SaveAssets();

            var data = new { savedScenes = saved, skippedUntitled = untitled, failedScenes = failed };
            return failed.Count > 0
                ? Response.Error("SCENE_SAVE_FAILED", data)
                : Response.Success($"Saved {saved.Count} scene(s) and the modified assets.", data,
                    untitled.Count > 0 ? "Untitled scenes were skipped: save them with save_scene and a path." : null);
        }

        [Description("Bring the Unity Editor window to the foreground. Windows only. The OS may refuse to move focus to a " +
                     "window while another application is receiving input; the reply says so when it does.")]
        public static object EditorFocus()
        {
#if UNITY_EDITOR_WIN
            var handle = Process.GetCurrentProcess().MainWindowHandle;
            if (handle == IntPtr.Zero)
                return Response.Error("NO_MAIN_WINDOW");

            if (IsIconic(handle))
                ShowWindow(handle, SwRestore);

            return SetForegroundWindow(handle)
                ? Response.Success("The Editor window is in the foreground.")
                : Response.Error("FOCUS_REFUSED", hint: "Windows only lets the foreground application hand focus over. Click the Editor window once.");
#else
            return Response.Error("PLATFORM_UNSUPPORTED", hint: "editor_focus is implemented for Windows only.");
#endif
        }

        [Description("Copy an external file (a texture, model, audio clip...) into the project and import it. The source is an " +
                     "absolute path anywhere on disk; the destination is an asset path under Assets/. Refuses to replace an " +
                     "existing asset unless confirm=true.")]
        public static object ImportAsset(
            [ToolParam("Absolute filesystem path of the file to import")] string source,
            [ToolParam("Destination asset path under Assets/, with extension (e.g. Assets/Art/Wall.png)")] string path,
            [ToolParam("Must be true to replace an asset that already exists at the destination", Required = false)] bool confirm = false)
        {
            if (string.IsNullOrWhiteSpace(source) || !Path.IsPathRooted(source))
                return Response.Error("INVALID_PARAM", new { param = "source", provided = source }, "source must be an absolute path.");

            if (!File.Exists(source))
                return Response.Error("SOURCE_NOT_FOUND", new { source }, "source must be an existing file, not a folder.");

            if (!AssetFunctions.TryResolveUnderAssetsOrPackages(path, out var assetPath, out var absolutePath)
                || Path.GetFileName(assetPath).EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                return Response.Error("INVALID_PATH", new { path },
                    "path must be an asset file under Assets/ with no '..' segments.");

            if (File.Exists(absolutePath) && !confirm)
                return Response.Error("ASSET_EXISTS", new { path = assetPath },
                    "An asset is already at this path. Pass confirm=true to replace it.");

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            File.Copy(source, absolutePath, true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var imported = AssetDatabase.LoadMainAssetAtPath(assetPath);
            return Response.Success($"Imported '{Path.GetFileName(source)}' as '{assetPath}'.", new
            {
                path = assetPath,
                guid = AssetDatabase.AssetPathToGUID(assetPath),
                type = imported != null ? imported.GetType().Name : null
            });
        }

        [Description("Edit a prefab asset in isolation, without opening a prefab stage, and save it back: rename one child and/or " +
                     "set one child's active state. Children are named by their path under the prefab root ('Body/Head'). " +
                     "Both children are resolved before anything is written, so a bad name leaves the prefab untouched.")]
        public static object SavePrefabContents(
            [ToolParam("Path to the .prefab asset")] string prefab_path,
            [ToolParam("Path of a child to rename, relative to the prefab root", Required = false)] string rename_child = null,
            [ToolParam("New name for rename_child", Required = false)] string new_name = null,
            [ToolParam("Path of a child whose active state to set, relative to the prefab root", Required = false)] string set_active_child = null,
            [ToolParam("Active state to apply to set_active_child. Default true.", Required = false)] bool active = true)
        {
            var renaming = !string.IsNullOrWhiteSpace(rename_child);
            var activating = !string.IsNullOrWhiteSpace(set_active_child);

            if (!renaming && !activating)
                return Response.Error("NOTHING_TO_DO", hint: "Pass rename_child with new_name, and/or set_active_child.");

            if (renaming && string.IsNullOrWhiteSpace(new_name))
                return Response.Error("INVALID_PARAM", new { param = "new_name", provided = new_name }, "rename_child needs a non-empty new_name.");

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path);
            if (asset == null || !PrefabUtility.IsPartOfPrefabAsset(asset) || PrefabUtility.GetPrefabAssetType(asset) == PrefabAssetType.Model)
                return Response.Error("PREFAB_NOT_FOUND", new { prefab_path }, "prefab_path must be a .prefab asset, not a model.");

            var root = PrefabUtility.LoadPrefabContents(prefab_path);
            try
            {
                Transform renamed = null, toggled = null;
                if (renaming && (renamed = root.transform.Find(rename_child)) == null)
                    return Response.Error("CHILD_NOT_FOUND", new { param = "rename_child", child = rename_child });
                if (activating && (toggled = root.transform.Find(set_active_child)) == null)
                    return Response.Error("CHILD_NOT_FOUND", new { param = "set_active_child", child = set_active_child });

                if (renamed != null) renamed.gameObject.name = new_name;
                if (toggled != null) toggled.gameObject.SetActive(active);

                PrefabUtility.SaveAsPrefabAsset(root, prefab_path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return Response.Success($"Saved '{prefab_path}'.", new
            {
                prefab_path,
                renamed = renaming ? new { from = rename_child, to = new_name } : null,
                setActive = activating ? new { child = set_active_child, active } : null
            });
        }

        [Description("Run the C# in a .cs file inside the project exactly as execute_code would run it (same templates, safety " +
                     "checks and Undo grouping). Useful for a long snippet kept in a file, e.g. under a folder outside Assets/ " +
                     "so writing it triggers no import.")]
        public static async Task<object> ExecuteCodeFile(
            [ToolParam("Path of the .cs file, relative to the project root or absolute inside it")] string file_path,
            [ToolParam("Same as execute_code's safety_checks. If omitted, the MCP Settings window default is used.", Required = false)] bool? safety_checks = null)
        {
            var resolved = PathSafety.ResolveProjectPath(file_path);
            if (!File.Exists(resolved))
                return Response.Error("FILE_NOT_FOUND", new { file_path });

            if (new FileInfo(resolved).Length > MaxCodeFileBytes)
                return Response.Error("FILE_TOO_LARGE", new { file_path, max_bytes = MaxCodeFileBytes });

            return await ScriptExecutionFunctions.ExecuteCode(File.ReadAllText(resolved), safety_checks);
        }
    }
}
