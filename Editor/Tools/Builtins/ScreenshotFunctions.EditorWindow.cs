// Copyright (C) KitWright. All rights reserved.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    internal static partial class ScreenshotFunctions
    {
        [Description("Capture a screenshot of any open EditorWindow (Inspector, Console, Project, custom tool windows...) " +
                     "identified by its title or type name. Captures directly from the window's render surface via the editor's " +
                     "internal GUIView, so the window does not need to be unoccluded on screen (it does need to be open). " +
                     "Returns a base64-encoded PNG image, or a saved file path when save_to_file=true.")]
        [ReadOnlyTool]
        public static string CaptureEditorWindow(
            [ToolParam("Window title (e.g. 'Inspector', 'MCP Server') or window type name (e.g. 'ConsoleWindow'). " +
                       "Case-insensitive. Exact title match wins, then title contains, then type name.")] string window,
            [ToolParam("Width of the screenshot in pixels. 0 keeps the window's native size.", Required = false)] int width = 0,
            [ToolParam("Height of the screenshot in pixels. 0 keeps the window's native size.", Required = false)] int height = 0,
            [ToolParam(SaveToFileParamDescription, Required = false)] bool save_to_file = false,
            [ToolParam(OutputPathParamDescription, Required = false)] string output_path = null,
            [ToolParam("Focus the window before capturing (brings its tab to the front of its dock area). Default true.", Required = false)] bool focus = true)
        {
            if (string.IsNullOrWhiteSpace(window))
                return ToolResultFormatter.Error("INVALID_WINDOW", new { hint = "Provide a window title or type name." });

            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(w => w != null)
                .ToArray();

            var target = ResolveEditorWindow(allWindows, window);
            if (target == null)
            {
                return ToolResultFormatter.Error("WINDOW_NOT_FOUND", new
                {
                    requested = window,
                    available = allWindows
                        .Select(w => new { title = w.titleContent.text, type = w.GetType().Name })
                        .ToArray()
                });
            }

            var guiViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIView");
            var grabPixels = guiViewType?.GetMethod("GrabPixels", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.NonPublic | BindingFlags.Instance);
            if (grabPixels == null || parentField == null)
            {
                return ToolResultFormatter.Error("EDITOR_WINDOW_CAPTURE_UNSUPPORTED", new
                {
                    hint = "UnityEditor.GUIView.GrabPixels is not available in this Unity version."
                });
            }

            if (focus)
            {
                target.Focus();
                target.Repaint();
            }

            var parent = parentField.GetValue(target);
            if (parent == null || !guiViewType.IsInstanceOfType(parent))
            {
                return ToolResultFormatter.Error("EDITOR_WINDOW_NOT_RENDERED", new
                {
                    window = target.titleContent.text,
                    hint = "The window has no host GUIView yet. Make sure it is open and visible, then retry."
                });
            }

            var pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            var nativeWidth = Mathf.Clamp(Mathf.RoundToInt(target.position.width * pixelsPerPoint), 16, 8192);
            var nativeHeight = Mathf.Clamp(Mathf.RoundToInt(target.position.height * pixelsPerPoint), 16, 8192);

            RenderTexture grabTexture = null;
            Texture2D screenshot = null;
            try
            {
                // Linear RT: GrabPixels bytes are already sRGB-final; a Default/sRGB RT double-encodes
                // them in a Linear-colorspace project and washes the capture out.
                grabTexture = new RenderTexture(nativeWidth, nativeHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                grabTexture.Create();
                grabPixels.Invoke(parent, new object[] { grabTexture, new Rect(0, 0, nativeWidth, nativeHeight) });

                var previousActive = RenderTexture.active;
                RenderTexture.active = grabTexture;
                screenshot = ReadActiveRenderTextureToTexture2D(nativeWidth, nativeHeight, flipVertically: SystemInfo.graphicsUVStartsAtTop);
                RenderTexture.active = previousActive;

                if (width <= 0 && height <= 0)
                {
                    var cap = ResolveEditorWindowScreenshotSize();
                    var longest = Mathf.Max(nativeWidth, nativeHeight);
                    if (cap > 0 && longest > cap)
                        width = nativeWidth >= nativeHeight ? cap : Mathf.RoundToInt(cap * (nativeWidth / (float)nativeHeight));
                }

                ResolveCaptureSize(ref width, ref height, nativeWidth, nativeHeight);
                if (width != nativeWidth || height != nativeHeight)
                {
                    // ponytail: CPU-side resize; ReadTextureToTexture2D's Blit would re-encode sRGB and wash out the capture.
                    var resized = new Texture2D(width, height, TextureFormat.RGB24, false);
                    var scaled = new Color[width * height];
                    for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                        scaled[y * width + x] = screenshot.GetPixelBilinear((x + 0.5f) / width, (y + 0.5f) / height);
                    resized.SetPixels(scaled);
                    resized.Apply();
                    UnityEngine.Object.DestroyImmediate(screenshot);
                    screenshot = resized;
                }

                var baseName = "window-" + SanitizeFileNameFragment(target.titleContent.text);
                return FinishCapture(screenshot.EncodeToPNG(), save_to_file, output_path, baseName);
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SCREENSHOT_CAPTURE_FAILED", new { window = target.titleContent.text, message = ex.Message });
            }
            finally
            {
                if (grabTexture != null)
                {
                    grabTexture.Release();
                    UnityEngine.Object.DestroyImmediate(grabTexture);
                }
                if (screenshot != null)
                    UnityEngine.Object.DestroyImmediate(screenshot);
            }
        }

        private static EditorWindow ResolveEditorWindow(EditorWindow[] windows, string requested)
        {
            var trimmed = requested.Trim();

            EditorWindow PickPreferFocused(IEnumerable<EditorWindow> candidates)
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

        private static string SanitizeFileNameFragment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "window";

            var chars = value.Trim().Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
            var sanitized = new string(chars).Trim('-');
            return string.IsNullOrEmpty(sanitized) ? "window" : sanitized;
        }

        private static int ResolveEditorWindowScreenshotSize()
        {
            var settings = KitWright.Editor.DI.RootScopeServices.Services?.GetService(typeof(KitWright.Editor.Settings.SettingsController))
                as KitWright.Editor.Settings.SettingsController;
            return settings?.EditorWindowScreenshotSize ?? 512;
        }
    }
}
