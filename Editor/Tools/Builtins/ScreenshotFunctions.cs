// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.IO;

using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Screenshot")]
    internal static partial class ScreenshotFunctions
    {
        private const string ImagePrefix = "data:image/png;base64,";
        private const string ScreenshotDirRelative = "Library/KitWrightMcp/Screenshots";

        private const string SaveToFileParamDescription =
            "Save the PNG to disk and return its file path instead of base64 image data. " +
            "Use for high-resolution captures whose base64 payload would be too large for the transport.";

        private const string OutputPathParamDescription =
            "Optional output .png path under the Unity project root (absolute, or relative to the project root). " +
            "Default: " + ScreenshotDirRelative + "/<name>-<timestamp>.png. Only used when save_to_file=true.";

        // Threshold on RAW PNG bytes for spilling a capture to disk instead of inlining it.
        // The transmitted payload is base64 (~1.33x the raw bytes) and a base64 payload around
        // ~1MB reliably drops the client-side MCP socket, so this raw limit is set well below
        // that: 512KB raw -> ~683KB base64, leaving headroom under the drop point.
        internal const int MaxInlineScreenshotBytes = 512 * 1024;

        internal static bool ShouldSpillScreenshotToFile(long pngBytes, bool saveToFile)
        {
            return saveToFile || pngBytes > MaxInlineScreenshotBytes;
        }

        /// <summary>
        /// Single exit point for all capture tools: base64 data URI by default, or
        /// write-to-disk + JSON path result when the caller asked for a file (or when the
        /// payload is too large to send inline, in which case it auto-falls back to a file).
        /// </summary>
        private static string FinishCapture(byte[] pngBytes, bool saveToFile, string outputPath, string defaultBaseName)
        {
            var spillToFile = ShouldSpillScreenshotToFile(pngBytes.Length, saveToFile);
            var autoFallback = spillToFile && !saveToFile;

            if (!spillToFile)
                return ImagePrefix + Convert.ToBase64String(pngBytes);

            if (!TrySaveScreenshotBytes(pngBytes, outputPath, defaultBaseName, out var savedPath, out var error))
                return ToolResultFormatter.Error("SCREENSHOT_SAVE_FAILED", error);

            return JsonConvert.SerializeObject(Response.Success(
                autoFallback
                    ? $"Screenshot ({pngBytes.Length} bytes) exceeded the inline transport limit and was saved to a file instead. Read the file to view it."
                    : "Screenshot saved to file.",
                new { path = savedPath, bytes = pngBytes.Length, fell_back_to_file = autoFallback }));
        }

        private static bool TrySaveScreenshotBytes(byte[] pngBytes, string outputPath, string baseName, out string savedPath, out object error)
        {
            savedPath = null;
            error = null;

            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
                if (!TryResolveScreenshotOutputPath(outputPath, baseName, projectRoot, out var path, out error))
                    return false;

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllBytes(path, pngBytes);
                savedPath = path;
                return true;
            }
            catch (Exception ex)
            {
                error = new { message = ex.Message };
                return false;
            }
        }

        internal static bool TryResolveScreenshotOutputPath(
            string outputPath,
            string baseName,
            string projectRoot,
            out string path,
            out object error)
        {
            path = null;
            error = null;

            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                error = new { hint = "Unity project root could not be resolved." };
                return false;
            }

            var normalizedRoot = Path.GetFullPath(projectRoot);
            string candidatePath;
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                var fileName = baseName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png";
                candidatePath = Path.Combine(normalizedRoot, ScreenshotDirRelative, fileName);
            }
            else
            {
                var trimmed = outputPath.Trim();
                if (!trimmed.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    error = new { provided = outputPath, hint = "output_path must end with .png" };
                    return false;
                }

                candidatePath = Path.IsPathRooted(trimmed)
                    ? trimmed
                    : Path.Combine(normalizedRoot, trimmed);
            }

            var normalizedPath = Path.GetFullPath(candidatePath);
            if (!PathSafety.IsInsideDirectory(normalizedPath, normalizedRoot))
            {
                error = new
                {
                    provided = outputPath,
                    project_root = normalizedRoot,
                    hint = "output_path must resolve inside the Unity project root."
                };
                return false;
            }

            path = normalizedPath;
            return true;
        }

        private static string CaptureTexture(Texture sourceTexture, int width, int height, Rect? safeAreaOverlay,
            bool flipVertically = false, bool saveToFile = false, string outputPath = null, string defaultBaseName = "capture")
        {
            Texture2D screenshot = null;

            try
            {
                var sourceWidth = Mathf.Max(sourceTexture.width, 1);
                var sourceHeight = Mathf.Max(sourceTexture.height, 1);
                ResolveCaptureSize(ref width, ref height, sourceWidth, sourceHeight);

                screenshot = ReadTextureToTexture2D(sourceTexture, width, height, flipVertically);

                if (safeAreaOverlay.HasValue)
                    DrawSafeAreaOverlay(screenshot, safeAreaOverlay.Value, sourceWidth, sourceHeight);

                return FinishCapture(screenshot.EncodeToPNG(), saveToFile, outputPath, defaultBaseName);
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SCREENSHOT_CAPTURE_FAILED", new { message = ex.Message });
            }
            finally
            {
                if (screenshot != null)
                    UnityEngine.Object.DestroyImmediate(screenshot);
            }
        }

        internal static void ResolveCaptureSize(ref int width, ref int height, int sourceWidth, int sourceHeight)
        {
            sourceWidth = Mathf.Max(sourceWidth, 1);
            sourceHeight = Mathf.Max(sourceHeight, 1);

            if (width <= 0 && height <= 0)
            {
                width = sourceWidth;
                height = sourceHeight;
            }
            else if (width > 0 && height <= 0)
            {
                height = Mathf.RoundToInt(width * (sourceHeight / (float)sourceWidth));
            }
            else if (height > 0 && width <= 0)
            {
                width = Mathf.RoundToInt(height * (sourceWidth / (float)sourceHeight));
            }
            else
            {
                // Both dims = a bounding box; fit inside it preserving the source aspect
                // so the capture is never stretched.
                var aspect = sourceWidth / (float)sourceHeight;
                if (aspect > width / (float)height)
                    height = Mathf.RoundToInt(width / aspect);
                else
                    width = Mathf.RoundToInt(height * aspect);
            }

            width = Mathf.Clamp(width, 64, 4096);
            height = Mathf.Clamp(height, 64, 4096);
        }

        internal static void FlipTextureVertically(Texture2D texture)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 1)
                return;

            var width = texture.width;
            var height = texture.height;
            var pixels = texture.GetPixels32();
            for (var y = 0; y < height / 2; y++)
            {
                var oppositeY = height - y - 1;
                var row = y * width;
                var oppositeRow = oppositeY * width;
                for (var x = 0; x < width; x++)
                {
                    var index = row + x;
                    var oppositeIndex = oppositeRow + x;
                    var temp = pixels[index];
                    pixels[index] = pixels[oppositeIndex];
                    pixels[oppositeIndex] = temp;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
        }

        /// <summary>
        /// Headless (-nographics, CI): there is no device to allocate a render target on, so
        /// RenderTexture.Create logs "RenderTexture.Create failed" -- an engine error nobody asked for,
        /// which the test framework then charges to whichever test is running -- and the readback comes
        /// back blank. A blank PNG is a worse answer than saying there is no frame.
        /// </summary>
        internal static void RequireAGraphicsDevice()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new InvalidOperationException(
                    "No graphics device (-nographics): there is no rendered frame to capture.");
        }

        internal static Texture2D ReadTextureToTexture2D(Texture sourceTexture, int width, int height, bool flipVertically)
        {
            RequireAGraphicsDevice();

            RenderTexture readableRenderTexture = null;
            RenderTexture previousActive = null;
            Texture2D screenshot = null;

            try
            {
                readableRenderTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                readableRenderTexture.Create();
                Graphics.Blit(sourceTexture, readableRenderTexture);

                previousActive = RenderTexture.active;
                RenderTexture.active = readableRenderTexture;

                screenshot = ReadActiveRenderTextureToTexture2D(width, height, flipVertically);

                var result = screenshot;
                screenshot = null;
                return result;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (readableRenderTexture != null)
                {
                    readableRenderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(readableRenderTexture);
                }
                if (screenshot != null)
                    UnityEngine.Object.DestroyImmediate(screenshot);
            }
        }

        internal static Texture2D ReadActiveRenderTextureToTexture2D(int width, int height, bool flipVertically)
        {
            var screenshot = new Texture2D(width, height, TextureFormat.RGB24, false);
            screenshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            screenshot.Apply();

            if (flipVertically)
                FlipTextureVertically(screenshot);

            return screenshot;
        }

        internal static void DrawSafeAreaOverlay(Texture2D texture, Rect safeArea, int sourceWidth, int sourceHeight)
        {
            if (texture == null || sourceWidth <= 0 || sourceHeight <= 0)
                return;

            var xScale = texture.width / (float)sourceWidth;
            var yScale = texture.height / (float)sourceHeight;
            var xMin = Mathf.Clamp(Mathf.RoundToInt(safeArea.xMin * xScale), 0, texture.width - 1);
            var yMin = Mathf.Clamp(Mathf.RoundToInt(safeArea.yMin * yScale), 0, texture.height - 1);
            var xMax = Mathf.Clamp(Mathf.RoundToInt(safeArea.xMax * xScale), 0, texture.width - 1);
            var yMax = Mathf.Clamp(Mathf.RoundToInt(safeArea.yMax * yScale), 0, texture.height - 1);

            if (xMax < xMin || yMax < yMin)
                return;

            var color = new Color32(80, 255, 90, 255);
            var thickness = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(texture.width, texture.height) / 180f), 2, 8);
            for (var i = 0; i < thickness; i++)
            {
                DrawHorizontalLine(texture, xMin, xMax, Mathf.Clamp(yMin + i, 0, texture.height - 1), color);
                DrawHorizontalLine(texture, xMin, xMax, Mathf.Clamp(yMax - i, 0, texture.height - 1), color);
                DrawVerticalLine(texture, yMin, yMax, Mathf.Clamp(xMin + i, 0, texture.width - 1), color);
                DrawVerticalLine(texture, yMin, yMax, Mathf.Clamp(xMax - i, 0, texture.width - 1), color);
            }

            texture.Apply();
        }

        private static void DrawHorizontalLine(Texture2D texture, int xMin, int xMax, int y, Color32 color)
        {
            for (var x = xMin; x <= xMax; x++)
                texture.SetPixel(x, y, color);
        }

        private static void DrawVerticalLine(Texture2D texture, int yMin, int yMax, int x, Color32 color)
        {
            for (var y = yMin; y <= yMax; y++)
                texture.SetPixel(x, y, color);
        }
    }
}
