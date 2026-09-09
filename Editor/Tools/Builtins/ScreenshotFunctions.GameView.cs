// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Reflection;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    internal static partial class ScreenshotFunctions
    {
        [Description("Capture a screenshot of the Game View (what the main camera sees). Returns a base64-encoded PNG image, " +
                     "or a saved file path when save_to_file=true.")]
        [ReadOnlyTool]
        public static string CaptureGameView(
            [ToolParam("Width of the screenshot in pixels", Required = false)] int width = 0,
            [ToolParam("Height of the screenshot in pixels", Required = false)] int height = 0,
            [ToolParam(SaveToFileParamDescription, Required = false)] bool save_to_file = false,
            [ToolParam(OutputPathParamDescription, Required = false)] string output_path = null)
        {
            var autoCap = ResolveDefaultScreenshotSize();
            if (!TryResolveGameViewSize(ref width, ref height, autoCap))
            {
                width = Mathf.Clamp(width > 0 ? width : autoCap, 64, 4096);
                height = Mathf.Clamp(height > 0 ? height : autoCap, 64, 4096);
            }

            var maxSide = Mathf.Max(width, height);
            if (maxSide > autoCap)
            {
                var scale = autoCap / (float)maxSide;
                width = Mathf.Max(64, Mathf.RoundToInt(width * scale));
                height = Mathf.Max(64, Mathf.RoundToInt(height * scale));
            }

            var playModePng = TryCapturePlayModeViewPngBytes(width, height);
            if (playModePng != null)
                return FinishCapture(playModePng, save_to_file, output_path, "game-view");

            var camera = Camera.main;
            if (camera == null)
                camera = UnityEngine.Object.FindAnyObjectByType<Camera>();

            if (camera == null)
                return ToolResultFormatter.Error("CAMERA_NOT_FOUND", new { hint = "Add a Camera component to capture the Game View." });

            try
            {
                return FinishCapture(CaptureWithUIPngBytes(camera, width, height), save_to_file, output_path, "game-view");
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SCREENSHOT_CAPTURE_FAILED", new { message = ex.Message });
            }
        }

        private static int ResolveDefaultScreenshotSize()
        {
            var settings = KitWright.Editor.DI.RootScopeServices.Services?.GetService(typeof(KitWright.Editor.Settings.SettingsController))
                as KitWright.Editor.Settings.SettingsController;
            return settings?.ScreenshotDefaultSize ?? 512;
        }

        internal static bool TryResolveGameViewSize(ref int width, ref int height, int autoMaxDimension = 1024)
        {
            var requestedWidth = width;
            var requestedHeight = height;

            try
            {
                var playModeViewType = Type.GetType("UnityEditor.PlayModeView,UnityEditor");
                var getMainPlayModeView = playModeViewType?.GetMethod(
                    "GetMainPlayModeView",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var playModeView = getMainPlayModeView?.Invoke(null, null);
                if (playModeView == null)
                    return false;

                var targetRenderSizeProperty = playModeView.GetType().GetProperty(
                    "targetRenderSize",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var targetSizeProperty = playModeView.GetType().GetProperty(
                    "targetSize",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                var value = targetRenderSizeProperty?.GetValue(playModeView, null)
                           ?? targetSizeProperty?.GetValue(playModeView, null);

                if (value is Vector2 vector2 && vector2.x > 0f && vector2.y > 0f)
                {
                    var aspect = vector2.x / vector2.y;
                    if (requestedWidth > 0 && requestedHeight > 0)
                    {
                        // Both dims = a bounding box; fit inside it preserving the source aspect
                        // so the capture is never stretched.
                        width = Mathf.Clamp(requestedWidth, 64, 4096);
                        height = Mathf.Clamp(requestedHeight, 64, 4096);
                        if (aspect > width / (float)height)
                            height = Mathf.Clamp(Mathf.RoundToInt(width / aspect), 64, 4096);
                        else
                            width = Mathf.Clamp(Mathf.RoundToInt(height * aspect), 64, 4096);
                    }
                    else if (requestedWidth > 0)
                    {
                        width = Mathf.Clamp(requestedWidth, 64, 4096);
                        height = Mathf.Clamp(Mathf.RoundToInt(width / aspect), 64, 4096);
                    }
                    else if (requestedHeight > 0)
                    {
                        height = Mathf.Clamp(requestedHeight, 64, 4096);
                        width = Mathf.Clamp(Mathf.RoundToInt(height * aspect), 64, 4096);
                    }
                    else
                    {
                        var srcW = Mathf.RoundToInt(vector2.x);
                        var srcH = Mathf.RoundToInt(vector2.y);
                        var scale = Mathf.Min(1f, autoMaxDimension / (float)Mathf.Max(srcW, srcH));
                        width = Mathf.Clamp(Mathf.RoundToInt(srcW * scale), 64, 4096);
                        height = Mathf.Clamp(Mathf.RoundToInt(srcH * scale), 64, 4096);
                    }
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static byte[] TryCapturePlayModeViewPngBytes(int width, int height)
        {
            Texture2D screenshot = null;

            try
            {
                var playModeViewType = Type.GetType("UnityEditor.PlayModeView,UnityEditor");
                var getMainPlayModeView = playModeViewType?.GetMethod(
                    "GetMainPlayModeView",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var playModeView = getMainPlayModeView?.Invoke(null, null);
                if (playModeView == null)
                    return null;

                var renderTextureField = playModeView.GetType().GetField(
                    "m_RenderTexture",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var sourceRenderTexture = renderTextureField?.GetValue(playModeView) as RenderTexture;
                if (sourceRenderTexture == null || !sourceRenderTexture.IsCreated() ||
                    sourceRenderTexture.width <= 0 || sourceRenderTexture.height <= 0)
                {
                    return null;
                }

                // Read the already-rendered Game View frame. This avoids camera.Render(),
                // which can bypass SRP cameras and produce black frames in URP/HDRP.
                // PlayModeView's internal RenderTexture is vertically inverted when read
                // back through ReadPixels for PNG output.
                screenshot = ReadTextureToTexture2D(
                    sourceRenderTexture,
                    width,
                    height,
                    flipVertically: ShouldFlipPlayModeViewRenderTexture());

                return screenshot.EncodeToPNG();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (screenshot != null)
                    UnityEngine.Object.DestroyImmediate(screenshot);
            }
        }

        internal static bool ShouldFlipPlayModeViewRenderTexture()
        {
            return SystemInfo.graphicsUVStartsAtTop;
        }

        internal static bool ShouldFlipCameraRenderTexture()
        {
            // Camera.Render() into a RenderTexture already compensates for the platform UV origin
            // (Unity flips the projection on top-left-origin APIs), so ReadPixels output is upright
            // on every platform. Flipping by graphicsUVStartsAtTop here re-inverted D3D captures.
            return false;
        }

        /// <summary>
        /// Captures the game view including ScreenSpaceOverlay UI by temporarily
        /// switching overlay canvases to ScreenSpaceCamera during render.
        /// </summary>
        private static byte[] CaptureWithUIPngBytes(Camera camera, int width, int height)
        {
            RenderTexture renderTexture = null;
            RenderTexture previousTarget = null;
            RenderTexture previousActive = null;
            Texture2D screenshot = null;
            var overlayCanvases = new List<Canvas>();

            RequireAGraphicsDevice();

            try
            {
                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                renderTexture.Create();

                // Find all ScreenSpaceOverlay canvases and temporarily switch to ScreenSpaceCamera
                var allCanvases = ObjectsHelper.FindObjectsByTypeUnsorted<Canvas>(FindObjectsInactive.Exclude);
                foreach (var canvas in allCanvases)
                {
                    if (canvas.renderMode == RenderMode.ScreenSpaceOverlay && canvas.gameObject.activeInHierarchy)
                    {
                        overlayCanvases.Add(canvas);
                        canvas.renderMode = RenderMode.ScreenSpaceCamera;
                        canvas.worldCamera = camera;
                        canvas.planeDistance = camera.nearClipPlane + 0.1f;
                    }
                }

                previousTarget = camera.targetTexture;
                previousActive = RenderTexture.active;

                camera.targetTexture = renderTexture;
                camera.Render();

                RenderTexture.active = renderTexture;
                screenshot = ReadActiveRenderTextureToTexture2D(width, height, ShouldFlipCameraRenderTexture());

                return screenshot.EncodeToPNG();
            }
            finally
            {
                // Restore overlay canvases
                foreach (var canvas in overlayCanvases)
                {
                    if (canvas != null)
                    {
                        canvas.worldCamera = null;
                        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    }
                }

                if (camera != null)
                    camera.targetTexture = previousTarget;

                RenderTexture.active = previousActive;

                if (renderTexture != null)
                {
                    renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                }
                if (screenshot != null)
                    UnityEngine.Object.DestroyImmediate(screenshot);
            }
        }

        private static byte[] CaptureFromCameraPngBytes(Camera camera, int width, int height)
        {
            RenderTexture renderTexture = null;
            RenderTexture previousTarget = null;
            RenderTexture previousActive = null;
            Texture2D screenshot = null;

            RequireAGraphicsDevice();

            try
            {
                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                renderTexture.Create();

                previousTarget = camera.targetTexture;
                previousActive = RenderTexture.active;

                camera.targetTexture = renderTexture;
                camera.Render();

                RenderTexture.active = renderTexture;
                screenshot = ReadActiveRenderTextureToTexture2D(width, height, ShouldFlipCameraRenderTexture());

                return screenshot.EncodeToPNG();
            }
            finally
            {
                if (camera != null)
                    camera.targetTexture = previousTarget;

                RenderTexture.active = previousActive;

                if (renderTexture != null)
                {
                    renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                }
                if (screenshot != null)
                    UnityEngine.Object.DestroyImmediate(screenshot);
            }
        }
    }
}
