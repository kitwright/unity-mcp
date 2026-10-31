// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Linq;
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
        // Menu items that quit the editor, open or switch the project, or wipe PlayerPrefs. Refused
        // whatever allow_modal says: it only vouches that a human is there to click a dialog.
        private static readonly string[] NeverRunMenuPaths =
        {
            "File/Exit",
            "File/Quit",
            "Unity/Quit",
            "File/New Project",
            "File/Open Project",
            "Edit/Clear All PlayerPrefs",
        };

        // Menu items that open a modal or a file picker. A modal runs its own message loop,
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
            ("Assets/Import New Asset", null),
            ("Assets/Import Package", "install_package"),
            ("Assets/Export Package", null),
            ("Assets/Reimport All", null),
            ("Assets/Delete", "delete_asset"),
            ("Help/About Unity", null),
        };

        // A menu item that opens a modal does not return until a human clicks it, so a long
        // ExecuteMenuItem is the signal. The curated list above only saves the first hang. A slow item
        // is not necessarily modal, so what is learned from duration expires.
        private const int ModalSuspicionMs = 10_000;

        private const long LearnedTtlTicks = TimeSpan.TicksPerHour;

        // EditorPrefs is per Unity install, not per project, so the key carries the project pin -
        // a menu item that blocks in one project says nothing about another. v2: entries written before
        // they could expire were permanent, and abandoning that key drops the ones stuck in it.
        internal static string LearnedKey =>
            "KitWright.MenuItem.LearnedModal.v2." + ProjectIdentity.PinFromProjectPath(ApplicationPaths.ProjectRoot);

        internal static string[] LearnedModalPaths() =>
            LiveLearnedPaths(EditorPrefs.GetString(LearnedKey, string.Empty), DateTime.UtcNow);

        internal static void ForgetLearnedModalPaths() => EditorPrefs.DeleteKey(LearnedKey);

        // An entry is "path" or "path<TAB>utc ticks"; one without a stamp never expires.
        internal static string[] LiveLearnedPaths(string raw, DateTime nowUtc)
        {
            var live = new System.Collections.Generic.List<string>();
            foreach (var entry in (raw ?? string.Empty).Split('\n'))
            {
                if (IsLive(entry, nowUtc))
                    live.Add(entry.Split('\t')[0]);
            }
            return live.ToArray();
        }

        private static bool IsLive(string entry, DateTime nowUtc)
        {
            if (entry.Length == 0)
                return false;
            var parts = entry.Split('\t');
            if (parts.Length == 1)
                return true;
            return parts.Length == 2 && long.TryParse(parts[1], out var stamp) && nowUtc.Ticks - stamp <= LearnedTtlTicks;
        }

        private static void LearnModalPath(string menuPath)
        {
            var now = DateTime.UtcNow;
            var entries = new System.Collections.Generic.List<string>();
            foreach (var entry in EditorPrefs.GetString(LearnedKey, string.Empty).Split('\n'))
            {
                if (IsLive(entry, now) && !string.Equals(entry.Split('\t')[0], menuPath, StringComparison.OrdinalIgnoreCase))
                    entries.Add(entry);
            }
            entries.Add(menuPath + "\t" + now.Ticks);
            EditorPrefs.SetString(LearnedKey, string.Join("\n", entries));
        }

        // Unity resolves '//File/Exit', 'File//Exit' and 'File/Exit/' to the same item, so a path is
        // compared with its empty segments dropped.
        internal static string NormalizeMenuPath(string menuPath) =>
            string.Join("/", (menuPath ?? string.Empty).Split('/').Select(segment => segment.Trim()).Where(segment => segment.Length > 0))
                .TrimEnd('.', '…', ' ');

        internal static string MatchBlockingMenuPath(string menuPath)
        {
            return MatchNeverRun(menuPath) ?? MatchBlocking(menuPath).Path;
        }

        private static string MatchNeverRun(string menuPath)
        {
            if (string.IsNullOrWhiteSpace(menuPath))
                return null;

            var trimmed = NormalizeMenuPath(menuPath);
            foreach (var path in NeverRunMenuPaths)
            {
                if (trimmed.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                    return path;
            }
            return null;
        }

        private static (string Path, string Tool) MatchBlocking(string menuPath)
        {
            if (string.IsNullOrWhiteSpace(menuPath))
                return default;

            var trimmed = NormalizeMenuPath(menuPath);
            foreach (var entry in BlockingMenuPaths)
            {
                if (trimmed.StartsWith(entry.Path, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }

            foreach (var learned in LearnedModalPaths())
            {
                if (string.Equals(trimmed, NormalizeMenuPath(learned), StringComparison.OrdinalIgnoreCase))
                    return (learned, null);
            }
            return default;
        }

        // The caller's profile may hide the tool that replaces a modal item, so it is only named when exposed.
        internal static object Refusal(string menuPath, bool allowModal, Func<string, bool> isExposed)
        {
            var never = MatchNeverRun(menuPath);
            if (never != null)
            {
                return Response.Error("MENU_ITEM_REFUSED",
                    new { menu_path = menuPath, matched = never },
                    $"'{never}' quits the editor, opens or switches the project, or clears PlayerPrefs. " +
                    "This tool will not run it, even with allow_modal=true; do it by hand in the editor.");
            }

            var blocking = allowModal ? default : MatchBlocking(menuPath);
            if (blocking.Path == null)
                return null;

            var tool = blocking.Tool != null && isExposed(blocking.Tool) ? blocking.Tool : null;
            return Response.Error("MENU_ITEM_OPENS_MODAL",
                new { menu_path = menuPath, matched = blocking.Path, tool_instead = tool },
                $"'{blocking.Path}' opens a modal dialog or file picker. That blocks the editor loop this call " +
                "returns on, so the call would hang until a human dismissed it. " +
                (tool != null ? $"Use {tool} instead, or pass " : "Pass ") +
                "allow_modal=true if someone is at the editor to click it.");
        }

        [Description("Execute an editor menu item by its full path. " +
                     "Examples: 'GameObject/2D Object/Sprites/Square', 'Window/Layouts/Default', 'Edit/Undo'. " +
                     "Returns success/failure based on whether the menu item exists and was triggered. " +
                     "Paths known to open a modal dialog or file picker are refused, because a modal stops the " +
                     "editor loop this call returns on and only a human clicking the dialog can unstick it; " +
                     "pass allow_modal=true to run one anyway when someone is watching the editor. " +
                     "Items that quit the editor, open or switch the project, or clear PlayerPrefs are always refused.")]
        public static object ExecuteMenuItem(
            [ToolParam("Full menu path, e.g. 'GameObject/2D Object/Sprite'")] string menu_path,
            [ToolParam("Run a menu item that is known to open a modal dialog or file picker. Only with a human at the editor.", Required = false)] bool allow_modal = false)
        {
            if (string.IsNullOrWhiteSpace(menu_path))
                return Response.Error("MENU_PATH_REQUIRED", new { message = "menu_path cannot be empty." });

            if (menu_path.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0)
                return Response.Error("INVALID_PARAM", new { param = "menu_path", expected = "a menu path on one line, without tabs" });

            var refusal = Refusal(menu_path, allow_modal, MCPToolExportPolicy.IsToolExposed);
            if (refusal != null)
                return refusal;

            try
            {
                var startedAt = DateTime.UtcNow;
                var ok = EditorApplication.ExecuteMenuItem(menu_path);
                var elapsed = DateTime.UtcNow - startedAt;

                if (!ok)
                    return Response.Error("MENU_ITEM_NOT_FOUND",
                        new { menu_path, hint = "Verify the path matches the editor menu hierarchy exactly (case sensitive, '/' separated). An item that exists but is disabled right now (nothing selected, wrong mode) gets the same answer." });

                if (elapsed.TotalMilliseconds < ModalSuspicionMs)
                    return Response.Success($"Executed menu item '{menu_path}'.");

                var trimmed = NormalizeMenuPath(menu_path);
                LearnModalPath(trimmed);
                return Response.Success(
                    $"Executed menu item '{menu_path}' after {elapsed.TotalSeconds:F0}s.",
                    new { menu_path, seconds = Math.Round(elapsed.TotalSeconds), learned_as_modal = trimmed },
                    "It took long enough that it may have waited on a dialog, so for the next hour it is refused " +
                    "like the other modal openers. Pass allow_modal=true to run it again before then.");
            }
            catch (Exception ex)
            {
                return Response.Error("MENU_EXECUTION_FAILED", new { message = ex.Message });
            }
        }
    }
}
