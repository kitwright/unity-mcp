// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Texture")]
    internal static class TextureFunctions
    {
        private const int MaxDimension = 4096;

        [Description("Create a solid-color .png texture asset. Dimensions are clamped to 4096.")]
        public static object CreateTexture(
            [ToolParam("Asset path under Assets/, e.g. 'Assets/Textures/white.png'")] string path,
            [ToolParam("Width in pixels", Required = false)] int width = 256,
            [ToolParam("Height in pixels", Required = false)] int height = 256,
            [ToolParam("Fill color: '#hex' or 'r,g,b[,a]' (0-255)", Required = false)] string color = "#ffffffff",
            [ToolParam("Import as a Sprite (Sprite2D) instead of a default texture", Required = false)] bool as_sprite = false)
        {
            return Generate(path, width, height, tex =>
            {
                var c = ParseColor(color);
                var pixels = Enumerable.Repeat(c, tex.width * tex.height).ToArray();
                tex.SetPixels32(pixels);
            }, as_sprite, $"Solid texture created at '{path}'");
        }

        [Description("Create a procedural pattern .png texture. Patterns: checkerboard, stripes (or stripes_v/stripes_h/stripes_diag), dots, grid, brick.")]
        public static object ApplyPattern(
            [ToolParam("Asset path under Assets/, e.g. 'Assets/Textures/checker.png'")] string path,
            [ToolParam("Pattern: checkerboard, stripes, stripes_v, stripes_h, stripes_diag, dots, grid, brick")] string pattern,
            [ToolParam("Width in pixels", Required = false)] int width = 256,
            [ToolParam("Height in pixels", Required = false)] int height = 256,
            [ToolParam("Pattern cell size in pixels", Required = false)] int pattern_size = 32,
            [ToolParam("Semicolon-separated colors ('#hex' or 'r,g,b[,a]'). Default black/white.", Required = false)] string palette = null,
            [ToolParam("Import as a Sprite", Required = false)] bool as_sprite = false)
        {
            var colors = ParsePalette(palette) ?? new List<Color32> { new Color32(255, 255, 255, 255), new Color32(0, 0, 0, 255) };
            int size = Mathf.Max(1, pattern_size);

            return Generate(path, width, height, tex =>
            {
                for (int y = 0; y < tex.height; y++)
                    for (int x = 0; x < tex.width; x++)
                        tex.SetPixel(x, y, PatternColor(x, y, pattern, colors, size));
            }, as_sprite, $"Pattern '{pattern}' texture created at '{path}'");
        }

        [Description("Create a gradient .png texture. Type 'linear' (with angle in degrees) or 'radial'. Colors interpolate across the palette.")]
        public static object ApplyGradient(
            [ToolParam("Asset path under Assets/, e.g. 'Assets/Textures/grad.png'")] string path,
            [ToolParam("Gradient type: 'linear' or 'radial'", Required = false)] string type = "linear",
            [ToolParam("Width in pixels", Required = false)] int width = 256,
            [ToolParam("Height in pixels", Required = false)] int height = 256,
            [ToolParam("Angle in degrees for linear gradients", Required = false)] float angle = 0f,
            [ToolParam("Semicolon-separated colors ('#hex' or 'r,g,b[,a]'). Default black->white.", Required = false)] string palette = null,
            [ToolParam("Import as a Sprite", Required = false)] bool as_sprite = false)
        {
            var colors = ParsePalette(palette) ?? new List<Color32> { new Color32(0, 0, 0, 255), new Color32(255, 255, 255, 255) };
            bool radial = string.Equals(type, "radial", StringComparison.OrdinalIgnoreCase);

            return Generate(path, width, height, tex =>
            {
                if (radial)
                {
                    float cx = tex.width / 2f, cy = tex.height / 2f;
                    float maxDist = Mathf.Sqrt(cx * cx + cy * cy);
                    for (int y = 0; y < tex.height; y++)
                        for (int x = 0; x < tex.width; x++)
                        {
                            float dist = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                            tex.SetPixel(x, y, LerpPalette(colors, Mathf.Clamp01(dist / maxDist)));
                        }
                }
                else
                {
                    float rad = angle * Mathf.Deg2Rad;
                    var dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
                    float denomX = Mathf.Max(1, tex.width - 1), denomY = Mathf.Max(1, tex.height - 1);
                    for (int y = 0; y < tex.height; y++)
                        for (int x = 0; x < tex.width; x++)
                            tex.SetPixel(x, y,
                                LerpPalette(colors, LinearGradientT(new Vector2(x / denomX, y / denomY), dir)));
                }
            }, as_sprite, $"{(radial ? "Radial" : "Linear")} gradient texture created at '{path}'");
        }

        [Description("Create a Perlin-noise .png texture, mapping the noise value through the palette. Higher octaves add detail.")]
        public static object ApplyNoise(
            [ToolParam("Asset path under Assets/, e.g. 'Assets/Textures/noise.png'")] string path,
            [ToolParam("Width in pixels", Required = false)] int width = 256,
            [ToolParam("Height in pixels", Required = false)] int height = 256,
            [ToolParam("Noise scale (smaller = larger features)", Required = false)] float scale = 0.05f,
            [ToolParam("Number of octaves (1-8)", Required = false)] int octaves = 4,
            [ToolParam("Semicolon-separated colors ('#hex' or 'r,g,b[,a]'). Default black->white.", Required = false)] string palette = null,
            [ToolParam("Import as a Sprite", Required = false)] bool as_sprite = false)
        {
            var colors = ParsePalette(palette) ?? new List<Color32> { new Color32(0, 0, 0, 255), new Color32(255, 255, 255, 255) };
            int oct = Mathf.Clamp(octaves, 1, 8);
            float offX = UnityEngine.Random.Range(0f, 1000f), offY = UnityEngine.Random.Range(0f, 1000f);

            return Generate(path, width, height, tex =>
            {
                for (int y = 0; y < tex.height; y++)
                    for (int x = 0; x < tex.width; x++)
                    {
                        float noise = 0f, amp = 1f, freq = 1f, max = 0f;
                        for (int o = 0; o < oct; o++)
                        {
                            noise += Mathf.PerlinNoise((x + offX) * scale * freq, (y + offY) * scale * freq) * amp;
                            max += amp;
                            amp *= 0.5f;
                            freq *= 2f;
                        }
                        tex.SetPixel(x, y, LerpPalette(colors, Mathf.Clamp01(noise / max)));
                    }
            }, as_sprite, $"Noise texture created at '{path}'");
        }

        private static object Generate(string path, int width, int height, Action<Texture2D> paint, bool asSprite, string successMessage)
        {
            string absolutePath;
            try
            {
                absolutePath = PathSafety.ResolveAssetPath(path);
            }
            catch (PathOutsideProjectException ex)
            {
                return Response.Error("INVALID_PATH", new { path, message = ex.Message },
                    "path must be under Assets/, and must stay there once '..' segments are resolved");
            }

            width = Mathf.Clamp(width, 1, MaxDimension);
            height = Mathf.Clamp(height, 1, MaxDimension);
            path = path.Replace('\\', '/');

            try
            {
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                paint(tex);
                tex.Apply();

                var bytes = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    ? tex.EncodeToJPG()
                    : tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);

                var dir = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllBytes(absolutePath, bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

                if (asSprite && AssetImporter.GetAtPath(path) is TextureImporter importer)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.SaveAndReimport();
                }

                return Response.Success($"{successMessage} ({width}x{height}).", new { path, width, height, sprite = asSprite });
            }
            catch (Exception e)
            {
                return Response.Error("TEXTURE_WRITE_FAILED", new { path, message = e.Message });
            }
        }

        /// Where a point of the unit square falls along the gradient: 0 at the first palette colour,
        /// 1 at the last. The span is the projection of that square onto dir, so the whole palette is
        /// used at every angle. The old (t+1)/2 assumed a [-1,1] square, so at 0 degrees the gradient
        /// started half way through the palette and black -> white came out grey -> white.
        internal static float LinearGradientT(Vector2 uv, Vector2 dir)
        {
            float lo = Mathf.Min(0f, dir.x) + Mathf.Min(0f, dir.y);
            float span = Mathf.Max(1e-5f, Mathf.Max(0f, dir.x) + Mathf.Max(0f, dir.y) - lo);
            return Mathf.Clamp01((Vector2.Dot(uv, dir) - lo) / span);
        }

        internal static Color32 PatternColor(int x, int y, string pattern, List<Color32> palette, int size)
        {
            int idx;
            switch (pattern?.ToLowerInvariant())
            {
                case "checkerboard": idx = ((x / size) + (y / size)) % 2; break;
                case "stripes":
                case "stripes_v": idx = (x / size) % palette.Count; break;
                case "stripes_h": idx = (y / size) % palette.Count; break;
                case "stripes_diag": idx = ((x + y) / size) % palette.Count; break;
                case "dots":
                    int cx = (x % (size * 2)) - size, cy = (y % (size * 2)) - size;
                    idx = (cx * cx + cy * cy) < (size * size / 4) ? 1 : 0;
                    break;
                case "grid": idx = (x % size == 0) || (y % size == 0) ? 1 : 0; break;
                case "brick":
                    int offset = ((y / size) % 2) * (size / 2);
                    idx = ((x + offset) % size == 0) || (y % size == 0) ? 1 : 0;
                    break;
                default: idx = 0; break;
            }
            return palette[Mathf.Clamp(idx, 0, palette.Count - 1)];
        }

        internal static Color32 LerpPalette(List<Color32> palette, float t)
        {
            if (palette.Count == 1 || t <= 0) return palette[0];
            if (t >= 1) return palette[palette.Count - 1];
            float scaled = t * (palette.Count - 1);
            int i = Mathf.FloorToInt(scaled);
            if (i >= palette.Count - 1) return palette[palette.Count - 1];
            return Color.Lerp(palette[i], palette[i + 1], scaled - i);
        }

        internal static List<Color32> ParsePalette(string palette)
        {
            if (string.IsNullOrWhiteSpace(palette)) return null;
            var colors = palette.Split(';')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Select(ParseColor)
                .ToList();
            return colors.Count > 0 ? colors : null;
        }

        internal static Color32 ParseColor(string value)
        {
            value = value.Trim();
            if (value.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(value, out var c)) return c;
                return new Color32(255, 255, 255, 255);
            }
            var parts = value.Trim('(', ')').Split(',');
            byte P(int i, byte def) => i < parts.Length && byte.TryParse(parts[i].Trim(), out var b) ? b : def;
            return new Color32(P(0, 255), P(1, 255), P(2, 255), parts.Length > 3 ? P(3, 255) : (byte)255);
        }
    }
}
