// Copyright (C) KitWright. All rights reserved.
using System;
using System.IO;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    internal static partial class ScreenshotFunctions
    {
        [Description("Compare two PNG screenshots pixel by pixel and report how much of the image changed. " +
                     "Closes the visual-regression loop the capture tools leave open: capture a baseline, act, " +
                     "capture again, and get a number instead of two images to eyeball.\n" +
                     "tolerance is per channel, 0-1, and exists because a re-render is never bit-identical — " +
                     "anti-aliasing and lossy sprite compression move single channels by a step or two. Pixels are " +
                     "counted as different only when a channel moves further than that. A run that reports 0% at " +
                     "tolerance 0 means the two files are identical, not merely similar.\n" +
                     "Pass diff_output_path to also write a diff image: changed pixels in red over a dimmed copy of " +
                     "the first image, so the report says where as well as how much.")]
        public static string CompareScreenshots(
            [ToolParam("Path to the first (baseline) .png, absolute or relative to the project root")] string path_a,
            [ToolParam("Path to the second (current) .png, absolute or relative to the project root")] string path_b,
            [ToolParam("Per-channel tolerance 0-1 below which a pixel counts as unchanged. Default 0.02.", Required = false)] float tolerance = 0.02f,
            [ToolParam("Optional .png path to write a diff image to, under the project root.", Required = false)] string diff_output_path = null)
        {
            Texture2D a = null;
            Texture2D b = null;
            try
            {
                if (!TryLoadPng(path_a, "path_a", out a, out var loadError) ||
                    !TryLoadPng(path_b, "path_b", out b, out loadError))
                    return loadError;

                if (a.width != b.width || a.height != b.height)
                {
                    return ToolResultFormatter.Error("SIZE_MISMATCH", new
                    {
                        path_a = new { a.width, a.height },
                        path_b = new { b.width, b.height },
                        hint = "Capture both images at the same resolution — a Game View resize between captures is the usual cause."
                    });
                }

                var pixelsA = a.GetPixels32();
                var pixelsB = b.GetPixels32();
                var cutoff = Mathf.RoundToInt(Mathf.Clamp01(tolerance) * 255f);

                var differing = 0;
                var maxDelta = 0;
                var diff = string.IsNullOrWhiteSpace(diff_output_path) ? null : new Color32[pixelsA.Length];

                for (var i = 0; i < pixelsA.Length; i++)
                {
                    var delta = ChannelDelta(pixelsA[i], pixelsB[i]);
                    if (delta > maxDelta) maxDelta = delta;

                    var changed = delta > cutoff;
                    if (changed) differing++;

                    if (diff != null)
                        diff[i] = changed
                            ? new Color32(255, 0, 0, 255)
                            : new Color32((byte)(pixelsA[i].r / 4), (byte)(pixelsA[i].g / 4), (byte)(pixelsA[i].b / 4), 255);
                }

                string diffPath = null;
                if (diff != null && !TryWriteDiffImage(diff, a.width, a.height, diff_output_path, out diffPath, out var writeError))
                    return writeError;

                var percent = pixelsA.Length == 0 ? 0f : (float)differing * 100f / pixelsA.Length;
                return JsonConvert.SerializeObject(Response.Success(
                    differing == 0
                        ? $"Identical within tolerance {tolerance} (largest channel move was {maxDelta}/255)."
                        : $"{percent:0.###}% of pixels differ ({differing} of {pixelsA.Length}), largest channel move {maxDelta}/255.",
                    new
                    {
                        width = a.width,
                        height = a.height,
                        total_pixels = pixelsA.Length,
                        differing_pixels = differing,
                        percent_different = percent,
                        max_channel_delta = maxDelta,
                        tolerance,
                        diff_path = diffPath
                    }));
            }
            finally
            {
                if (a != null) UnityEngine.Object.DestroyImmediate(a);
                if (b != null) UnityEngine.Object.DestroyImmediate(b);
            }
        }

        private static int ChannelDelta(Color32 x, Color32 y) =>
            Mathf.Max(Mathf.Max(Mathf.Abs(x.r - y.r), Mathf.Abs(x.g - y.g)),
                      Mathf.Max(Mathf.Abs(x.b - y.b), Mathf.Abs(x.a - y.a)));

        private static bool TryLoadPng(string path, string paramName, out Texture2D texture, out string error)
        {
            texture = null;
            error = null;

            string resolved;
            try { resolved = PathSafety.ResolveProjectPath(path); }
            catch (Exception ex)
            {
                error = ToolResultFormatter.Error("PATH_OUTSIDE_PROJECT", new { param = paramName, path, message = ex.Message });
                return false;
            }

            if (!File.Exists(resolved))
            {
                error = ToolResultFormatter.Error("SCREENSHOT_NOT_FOUND", new { param = paramName, path, resolved });
                return false;
            }

            // Loading into a throwaway texture keeps this independent of import settings: a PNG under
            // Assets/ would otherwise come back non-readable or resized by its TextureImporter.
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(File.ReadAllBytes(resolved)))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                texture = null;
                error = ToolResultFormatter.Error("SCREENSHOT_DECODE_FAILED", new { param = paramName, path, resolved });
                return false;
            }

            return true;
        }

        private static bool TryWriteDiffImage(Color32[] pixels, int width, int height, string outputPath, out string savedPath, out string error)
        {
            savedPath = null;
            error = null;

            var diffTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                diffTexture.SetPixels32(pixels);
                diffTexture.Apply();

                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
                if (!TryResolveScreenshotOutputPath(outputPath, "diff", projectRoot, out var resolved, out var pathError))
                {
                    error = ToolResultFormatter.Error("DIFF_SAVE_FAILED", pathError);
                    return false;
                }

                var directory = Path.GetDirectoryName(resolved);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllBytes(resolved, diffTexture.EncodeToPNG());
                savedPath = resolved;
                return true;
            }
            catch (Exception ex)
            {
                error = ToolResultFormatter.Error("DIFF_SAVE_FAILED", new { outputPath, message = ex.Message });
                return false;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(diffTexture);
            }
        }
    }
}
