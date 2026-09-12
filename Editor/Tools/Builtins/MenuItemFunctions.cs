// Copyright (C) KitWright. Licensed under MIT.

using System;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Services;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// Invoke arbitrary editor menu items. This is the cheap-and-broad fallback when no
    /// dedicated tool exists — Unity itself, all engine modules, and every third-party
    /// package already publish their commands as menu items, so an agent can drive most
    /// editor functionality through this without us writing wrappers.
    /// </summary>
    [ToolProvider("MenuItem")]
    internal static class MenuItemFunctions
    {
        // Menu items that open a modal, a file picker, or quit. A modal runs its own message loop,
        // so the editor stops pumping and this very call can never return -- the user has to click
        // the dialog before anything recovers.
        // Tool names the MCP tool that does the same job without the dialog; null when none does.
        // Order matters: the first prefix match wins.
        private static readonly (string Path, string Tool)[] BlockingMenuPaths =
        {
            ("File/New Scene", "create_new_scene"),
            ("File/Open Scene", "open_scene"),
            ("File/Open Recent Scene", null),
            ("File/Save As", "save_scene"),
            ("File/Save Scene As", "save_scene"),
            ("File/Build And Run", "build_player"),
            ("File/Build Profiles", "build_player"),
            ("File/Build Settings", "build_player"),
            ("File/Exit", null),
            ("File/Quit", null),
            ("File/New Project", null),
            ("File/Open Project", null),
            ("Edit/Clear All PlayerPrefs", "delete_all_player_prefs"),
            ("Assets/Import New Asset", null),
            ("Assets/Import Package", "install_package"),
            ("Assets/Export Package", null),
            ("Assets/Reimport All", null),
            ("Assets/Delete", "delete_asset"),
            ("Help/About Unity", null),
        };

        // A menu item that opens a modal does not return until a human clicks it, so a long
        // ExecuteMenuItem is the signal. The curated list above only saves the first hang.
        private const int ModalSuspicionMs = 10_000;

        // EditorPrefs is per Unity install, not per project, so the key carries the project pin -
        // a menu item that blocks in one project says nothing about another.
        internal static string LearnedKey =>
            "KitWright.MenuItem.LearnedModal." + ProjectIdentity.PinFromProjectPath(ApplicationPaths.ProjectRoot);

        internal static string[] LearnedModalPaths()
        {
            var raw = EditorPrefs.GetString(LearnedKey, string.Empty);
            return string.IsNullOrEmpty(raw) ? Array.Empty<string>() : raw.Split('\n');
        }

        internal static void ForgetLearnedModalPaths() => EditorPrefs.DeleteKey(LearnedKey);

        private static void LearnModalPath(string menuPath)
        {
            var paths = new System.Collections.Generic.HashSet<string>(LearnedModalPaths(), StringComparer.OrdinalIgnoreCase);
            if (paths.Add(menuPath))
                EditorPrefs.SetString(LearnedKey, string.Join("\n", paths));
        }

        internal static string MatchBlockingMenuPath(string menuPath)
        {
            return MatchBlocking(menuPath).Path;
        }

        private static (string Path, string Tool) MatchBlocking(string menuPath)
        {
            if (string.IsNullOrWhiteSpace(menuPath))
                return default;

            var trimmed = menuPath.Trim().TrimEnd('.', '…');
            foreach (var entry in BlockingMenuPaths)
            {
                if (trimmed.StartsWith(entry.Path, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }

            foreach (var learned in LearnedModalPaths())
            {
                if (string.Equals(trimmed, learned, StringComparison.OrdinalIgnoreCase))
                    return (learned, null);
            }
            return default;
        }

        [Description("Execute an editor menu item by its full path. " +
                     "Examples: 'GameObject/2D Object/Sprites/Square', 'Window/Layouts/Default', 'Edit/Undo'. " +
                     "Returns success/failure based on whether the menu item exists and was triggered. " +
                     "Paths known to open a modal dialog or file picker are refused, because a modal stops the " +
                     "editor loop this call returns on and only a human clicking the dialog can unstick it; " +
                     "pass allow_modal=true to run one anyway when someone is watching the editor.")]
        public static object ExecuteMenuItem(
            [ToolParam("Full menu path, e.g. 'GameObject/2D Object/Sprite'")] string menu_path,
            [ToolParam("Run a menu item that is known to open a modal dialog or file picker. Only with a human at the editor.", Required = false)] bool allow_modal = false)
        {
            if (string.IsNullOrWhiteSpace(menu_path))
                return Response.Error("MENU_PATH_REQUIRED", new { message = "menu_path cannot be empty." });

            var blocking = allow_modal ? default : MatchBlocking(menu_path);
            if (blocking.Path != null)
            {
                return Response.Error("MENU_ITEM_OPENS_MODAL",
                    new { menu_path, matched = blocking.Path, tool_instead = blocking.Tool },
                    $"'{blocking.Path}' opens a modal dialog or file picker. That blocks the editor loop this call " +
                    "returns on, so the call would hang until a human dismissed it. " +
                    (blocking.Tool != null ? $"Use {blocking.Tool} instead, or pass " : "Pass ") +
                    "allow_modal=true if someone is at the editor to click it.");
            }

            try
            {
                var startedAt = DateTime.UtcNow;
                var ok = EditorApplication.ExecuteMenuItem(menu_path);
                var elapsed = DateTime.UtcNow - startedAt;

                if (!ok)
                    return Response.Error("MENU_ITEM_NOT_FOUND",
                        new { menu_path, hint = "Verify the path matches the editor menu hierarchy exactly (case sensitive, '/' separated)." });

                if (elapsed.TotalMilliseconds < ModalSuspicionMs)
                    return Response.Success($"Executed menu item '{menu_path}'.");

                var trimmed = menu_path.Trim().TrimEnd('.', '…');
                LearnModalPath(trimmed);
                return Response.Success(
                    $"Executed menu item '{menu_path}' after {elapsed.TotalSeconds:F0}s.",
                    new { menu_path, seconds = Math.Round(elapsed.TotalSeconds), learned_as_modal = trimmed },
                    "It took long enough that it almost certainly waited on a dialog, so it is now refused by " +
                    "default like the other modal openers. Pass allow_modal=true to run it again, or clear it with " +
                    $"delete_editor_pref '{LearnedKey}' if it was merely slow.");
            }
            catch (Exception ex)
            {
                return Response.Error("MENU_EXECUTION_FAILED", new { message = ex.Message });
            }
        }
    }
}
