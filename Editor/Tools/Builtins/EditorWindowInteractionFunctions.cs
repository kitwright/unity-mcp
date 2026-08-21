// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("EditorWindowInteraction")]
    internal static class EditorWindowInteractionFunctions
    {
        private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        [Description("Click inside any open EditorWindow (Inspector, Console, Project, custom tool windows...) as a real user would. " +
                     "Coordinates are in pixels with 0,0 at the TOP-LEFT of the window, matching what capture_editor_window returns. " +
                     "Dispatches a real mouse-down + mouse-up event into the window's internal GUIView so IMGUI/UI Toolkit controls react.")]
        public static string SimulateEditorWindowClick(
            [ToolParam("Window title (e.g. 'Inspector', 'MCP Server') or window type name (e.g. 'ConsoleWindow'). Case-insensitive.")] string window,
            [ToolParam("X coordinate in pixels from the window's left edge")] int x,
            [ToolParam("Y coordinate in pixels from the window's top edge")] int y,
            [ToolParam("Mouse button: left, right, or middle", Required = false)] string button = "left",
            [ToolParam("Number of clicks (2 for a double-click)", Required = false)] int click_count = 1,
            [ToolParam("Focus the window before clicking", Required = false)] bool focus = true)
        {
            if (!TryResolveView(window, focus, out var target, out var parent, out var error))
                return error;

            try
            {
                var pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
                var point = new Vector2(x / pixelsPerPoint, y / pixelsPerPoint);
                var mouseButton = ParseButton(button);
                var clicks = Mathf.Max(1, click_count);

                SendEvent(parent, MakeMouseEvent(EventType.MouseDown, point, mouseButton, clicks));
                SendEvent(parent, MakeMouseEvent(EventType.MouseUp, point, mouseButton, clicks));
                target.Repaint();

                return $"{button} click x{clicks} at pixel ({x}, {y}) -> point ({point.x:F1}, {point.y:F1}) in '{target.titleContent.text}'";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Type text or send a key into any open EditorWindow as a real user would (e.g. into a focused text field after clicking it). " +
                     "Dispatches real key-down + key-up events into the window's internal GUIView. " +
                     "Either provide 'text' to type a string character-by-character, or 'key' for a single named key (Return, Escape, Tab, Backspace, Delete, LeftArrow...).")]
        public static string SimulateEditorWindowKey(
            [ToolParam("Window title or type name. Case-insensitive.")] string window,
            [ToolParam("Text to type character-by-character", Required = false)] string text = null,
            [ToolParam("A single named key to send (e.g. Return, Escape, Tab, Backspace)", Required = false)] string key = null,
            [ToolParam("Focus the window before typing", Required = false)] bool focus = true)
        {
            if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(key))
                return ToolResultFormatter.ErrorMessage("INVALID_INPUT", "Provide either 'text' or 'key'.");

            if (!TryResolveView(window, focus, out var target, out var parent, out var error))
                return error;

            try
            {
                var sent = 0;
                if (!string.IsNullOrEmpty(text))
                {
                    foreach (var c in text)
                    {
                        SendEvent(parent, MakeCharEvent(EventType.KeyDown, c));
                        SendEvent(parent, MakeCharEvent(EventType.KeyUp, c));
                        sent++;
                    }
                }
                else if (Enum.TryParse<KeyCode>(key, ignoreCase: true, out var keyCode))
                {
                    SendEvent(parent, MakeKeyEvent(EventType.KeyDown, keyCode));
                    SendEvent(parent, MakeKeyEvent(EventType.KeyUp, keyCode));
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

        private static bool TryResolveView(string window, bool focus, out EditorWindow target, out object parent, out string error)
        {
            target = null;
            parent = null;
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

            var guiViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIView");
            var sendEvent = guiViewType?.GetMethod("SendEvent", InstanceFlags, null, new[] { typeof(Event) }, null);
            var parentField = typeof(EditorWindow).GetField("m_Parent", InstanceFlags);
            if (sendEvent == null || parentField == null)
            {
                error = ToolResultFormatter.Error("EDITOR_WINDOW_INPUT_UNSUPPORTED", new
                {
                    hint = "UnityEditor.GUIView.SendEvent is not available in this Unity version."
                });
                return false;
            }

            if (focus)
            {
                target.Focus();
                target.Repaint();
            }

            parent = parentField.GetValue(target);
            if (parent == null || !guiViewType.IsInstanceOfType(parent))
            {
                error = ToolResultFormatter.Error("EDITOR_WINDOW_NOT_RENDERED", new
                {
                    window = target.titleContent.text,
                    hint = "The window has no host GUIView yet. Make sure it is open and visible, then retry."
                });
                return false;
            }

            return true;
        }

        private static void SendEvent(object guiView, Event evt)
        {
            var method = guiView.GetType().GetMethod("SendEvent", InstanceFlags, null, new[] { typeof(Event) }, null);
            method.Invoke(guiView, new object[] { evt });
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
