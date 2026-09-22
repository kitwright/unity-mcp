// Copyright (C) KitWright. All rights reserved.

using System;
using System.ComponentModel;
using System.Linq;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("EditorWindowInteraction")]
    internal static class EditorWindowInteractionFunctions
    {
        [Description("Click inside any open EditorWindow (Inspector, Console, Project, custom tool windows...) as a real user would. " +
                     "Coordinates are in pixels with 0,0 at the TOP-LEFT of the window, matching what capture_editor_window returns. " +
                     "Dispatches pointer-down + pointer-up into the window's UI Toolkit panel, so UI Toolkit controls react. " +
                     "A point that lands on legacy IMGUI is refused with IMGUI_CLICK_UNSUPPORTED rather than reported as a click that did nothing.")]
        public static string SimulateEditorWindowClick(
            [ToolParam("Window title (e.g. 'Inspector', 'MCP Server') or window type name (e.g. 'ConsoleWindow'). Case-insensitive.")] string window,
            [ToolParam("X coordinate in pixels from the window's left edge")] int x,
            [ToolParam("Y coordinate in pixels from the window's top edge")] int y,
            [ToolParam("Mouse button: left, right, or middle", Required = false)] string button = "left",
            [ToolParam("Number of clicks (2 for a double-click)", Required = false)] int click_count = 1,
            [ToolParam("Focus the window before clicking", Required = false)] bool focus = true)
        {
            if (!TryResolvePanel(window, focus, out var target, out var root, out var error))
                return error;

            try
            {
                var pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
                var point = new Vector2(x / pixelsPerPoint, y / pixelsPerPoint);
                var mouseButton = ParseButton(button);
                var clicks = Mathf.Max(1, click_count);

                // Panel coordinates belong to the whole host view, so a docked window's own origin sits
                // below the tab bar: (0,0) in the window is not (0,0) in the panel.
                var world = root.LocalToWorld(point);
                var tree = root.panel.visualTree;

                var picked = root.panel.Pick(world);

                // ponytail: IMGUI is refused, not driven. Driving it needs the panel's own pointer
                // position to follow the synthesized event (PointerDeviceState, internal).
                var imgui = FindIMGUIContainer(picked, root);
                if (imgui != null)
                {
                    return ToolResultFormatter.Error("IMGUI_CLICK_UNSUPPORTED", new
                    {
                        window = target.titleContent.text,
                        element = string.IsNullOrEmpty(imgui.name) ? nameof(IMGUIContainer) : imgui.name,
                        hint = "That point lands on legacy IMGUI, which reads the real OS cursor rather than the " +
                               "position of a synthesized event, so the control would not react. Drive this window " +
                               "through its UI Toolkit controls, or through a menu item."
                    });
                }

                // A real pointer-down moves focus on its way through the panel; a synthesized one does not,
                // so a click followed by simulate_editor_window_key would type into nothing.
                FocusPicked(picked);

                for (var i = 0; i < clicks; i++)
                {
                    using (var down = PointerDownEvent.GetPooled(MakeMouseEvent(EventType.MouseDown, world, mouseButton, i + 1)))
                        tree.SendEvent(down);
                    using (var up = PointerUpEvent.GetPooled(MakeMouseEvent(EventType.MouseUp, world, mouseButton, i + 1)))
                        tree.SendEvent(up);
                }
                target.Repaint();

                return $"{button} click x{clicks} at pixel ({x}, {y}) -> panel point ({world.x:F1}, {world.y:F1}) in '{target.titleContent.text}'";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Type text or send a key into any open EditorWindow as a real user would (e.g. into a focused text field after clicking it). " +
                     "Dispatches real key-down + key-up events to the panel's focused element. " +
                     "Either provide 'text' to type a string character-by-character, or 'key' for a single named key (Return, Escape, Tab, Backspace, Delete, LeftArrow...).")]
        public static string SimulateEditorWindowKey(
            [ToolParam("Window title or type name. Case-insensitive.")] string window,
            [ToolParam("Text to type character-by-character", Required = false)] string text = null,
            [ToolParam("A single named key to send (e.g. Return, Escape, Tab, Backspace)", Required = false)] string key = null,
            [ToolParam("Focus the window before typing", Required = false)] bool focus = true)
        {
            if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(key))
                return ToolResultFormatter.ErrorMessage("INVALID_INPUT", "Provide either 'text' or 'key'.");

            if (!TryResolvePanel(window, focus, out var target, out var root, out var error))
                return error;

            try
            {
                // Keys go to whatever holds focus, and only fall back to the tree when nothing does.
                var keyTarget = root.panel.focusController?.focusedElement as VisualElement ?? root.panel.visualTree;

                var sent = 0;
                if (!string.IsNullOrEmpty(text))
                {
                    foreach (var c in text)
                    {
                        SendKey(keyTarget, MakeCharEvent(EventType.KeyDown, c));
                        SendKey(keyTarget, MakeCharEvent(EventType.KeyUp, c));
                        sent++;
                    }
                }
                else if (Enum.TryParse<KeyCode>(key, ignoreCase: true, out var keyCode))
                {
                    SendKey(keyTarget, MakeKeyEvent(EventType.KeyDown, keyCode));
                    SendKey(keyTarget, MakeKeyEvent(EventType.KeyUp, keyCode));
                    sent = 1;
                }
                else
                {
                    return ToolResultFormatter.ErrorMessage("KEY_NOT_RECOGNIZED", $"Key '{key}' is not a valid KeyCode. Examples: Return, Escape, Tab, Backspace, Delete, LeftArrow.");
                }

                target.Repaint();
                return $"Sent {sent} key event(s) to '{target.titleContent.text}'";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        private static bool TryResolvePanel(string window, bool focus, out EditorWindow target, out VisualElement root, out string error)
        {
            target = null;
            root = null;
            error = null;

            if (string.IsNullOrWhiteSpace(window))
            {
                error = ToolResultFormatter.ErrorMessage("INVALID_WINDOW", "Provide a window title or type name.");
                return false;
            }

            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w => w != null).ToArray();
            target = ResolveEditorWindow(allWindows, window.Trim());
            if (target == null)
            {
                error = ToolResultFormatter.Error("WINDOW_NOT_FOUND", new
                {
                    requested = window,
                    available = allWindows.Select(w => new { title = w.titleContent.text, type = w.GetType().Name }).ToArray()
                });
                return false;
            }

            if (focus)
            {
                target.Focus();
                target.Repaint();
            }

            root = target.rootVisualElement;
            if (root?.panel == null)
            {
                error = ToolResultFormatter.Error("EDITOR_WINDOW_NOT_RENDERED", new
                {
                    window = target.titleContent.text,
                    hint = "The window has no UI Toolkit panel yet. Make sure it is open and visible, then retry."
                });
                return false;
            }

            return true;
        }

        // Stops at the window's own root: a docked window hangs under the DockArea's tab-bar
        // IMGUIContainer, and walking past the root would call every window IMGUI.
        private static IMGUIContainer FindIMGUIContainer(VisualElement element, VisualElement root)
        {
            for (var e = element; e != null; e = e.parent)
            {
                if (e is IMGUIContainer imgui)
                    return imgui;
                if (e == root)
                    break;
            }
            return null;
        }

        private static void FocusPicked(VisualElement element)
        {
            for (var e = element; e != null; e = e.parent)
            {
                if (e.focusable)
                {
                    e.Focus();
                    return;
                }
            }
        }

        private static void SendKey(VisualElement element, Event evt)
        {
            if (evt.type == EventType.KeyDown)
            {
                using (var e = KeyDownEvent.GetPooled(evt))
                    element.SendEvent(e);
            }
            else
            {
                using (var e = KeyUpEvent.GetPooled(evt))
                    element.SendEvent(e);
            }
        }

        private static Event MakeMouseEvent(EventType type, Vector2 point, int button, int clickCount)
        {
            return new Event
            {
                type = type,
                mousePosition = point,
                button = button,
                clickCount = clickCount
            };
        }

        private static Event MakeCharEvent(EventType type, char character)
        {
            var evt = new Event { type = type, character = character };
            if (Enum.TryParse<KeyCode>(character.ToString(), ignoreCase: true, out var code))
                evt.keyCode = code;
            return evt;
        }

        private static Event MakeKeyEvent(EventType type, KeyCode keyCode)
        {
            return new Event { type = type, keyCode = keyCode };
        }

        private static int ParseButton(string button)
        {
            switch ((button ?? "left").Trim().ToLowerInvariant())
            {
                case "right":
                    return 1;
                case "middle":
                    return 2;
                default:
                    return 0;
            }
        }

        private static EditorWindow ResolveEditorWindow(EditorWindow[] windows, string trimmed)
        {
            EditorWindow PickPreferFocused(System.Collections.Generic.IEnumerable<EditorWindow> candidates)
            {
                EditorWindow first = null;
                foreach (var candidate in candidates)
                {
                    if (candidate.hasFocus)
                        return candidate;
                    if (first == null)
                        first = candidate;
                }
                return first;
            }

            var exactTitle = PickPreferFocused(windows.Where(w =>
                string.Equals(w.titleContent.text, trimmed, StringComparison.OrdinalIgnoreCase)));
            if (exactTitle != null)
                return exactTitle;

            var containsTitle = PickPreferFocused(windows.Where(w =>
                w.titleContent.text != null &&
                w.titleContent.text.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0));
            if (containsTitle != null)
                return containsTitle;

            return PickPreferFocused(windows.Where(w =>
                string.Equals(w.GetType().Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
                w.GetType().Name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0));
        }
    }
}
