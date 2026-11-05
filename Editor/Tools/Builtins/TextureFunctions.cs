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
    [ToolProvider("Sprites & Textures")]
    internal static class TextureFunctions
    {
        private const int MaxDimension = 4096;

        [Description("Create a solid-color .png texture asset. Dimensions are clamped to 4096. The path must end in .png, .jpg or .jpeg; an existing file is never replaced unless overwrite=true.")]
        public static object CreateTexture(
            [ToolParam("Asset path under Assets/, e.g. 'Assets/Textures/white.png'")] string path,
            [ToolParam("Width in pixels", Required = false)] int width = 256,
            [ToolParam("Height in pixels", Required = false)] int height = 256,
            [ToolParam("Fill color: '#hex', a color name, or 'r,g,b[,a]' with whole-number channels 0-255 (not 0-1)", Required = false)] string color = "#ffffffff",
            [ToolParam("Import as a Sprite (Sprite2D) instead of a default texture", Required = false)] bool as_sprite = false,
            [ToolParam("Replace the file already at that path instead of refusing", Required = false)] bool overwrite = false)
        {
            if (!TryParseColor32(color, out var c, out var colorError))
                return InvalidColor("color", color, colorError);

            return Generate(path, width, height, tex =>
            {
                var pixels = Enumerable.Repeat(c, tex.width * tex.height).ToArray();
                tex.SetPixels32(pixels);
            }, as_sprite, overwrite, "Solid texture");
        }

        [Description("Create a procedural pattern .png texture. Patterns: checkerboard, stripes (or stripes_v/stripes_h/stripes_diag), dots, grid, brick. An existing file is never replaced unless overwrite=true.")]
        public static object ApplyPattern(
            [ToolParam("Asset path under Assets/, e.g. 'Assets/Textures/checker.png'")] string path,
            [ToolParam("Pattern: checkerboard, stripes, stripes_v, stripes_h, stripes_diag, dots, grid, brick")] string pattern,
            [ToolParam("Width in pixels", Required = false)] int width = 256,
            [ToolParam("Height in pixels", Required = false)] int height = 256,
            [ToolParam("Pattern cell size in pixels", Required = false)] int pattern_size = 32,
            [ToolParam("Semicolon-separated colors ('#hex', a color name, or 'r,g,b[,a]' with whole-number channels 0-255). Default black/white.", Required = false)] string palette = null,
            [ToolParam("Import as a Sprite", Required = false)] bool as_sprite = false,
            [ToolParam("Replace the file already at that path instead of refusing", Required = false)] bool overwrite = false)
        {
            if (!Array.Exists(Patterns, p => string.Equals(p, pattern?.Trim(), StringComparison.OrdinalIgnoreCase)))
                return Response.Error("INVALID_PARAM", new { param = "pattern", provided = pattern, valid = Patterns });

            if (!TryParsePalette(palette, out var parsed, out var paletteError))
                return InvalidColor("palette", palette, paletteError);

            var colors = parsed ?? new List<Color32> { new Color32(255, 255, 255, 255), new Color32(0, 0, 0, 255) };
            int size = Mathf.Max(1, pattern_size);
            var name = pattern.Trim();

            return Generate(path, width, height, tex =>
            {
                for (int y = 0; y < tex.height; y++)
                    for (int x = 0; x < tex.width; x++)
                        tex.SetPixel(x, y, PatternColor(x, y, name, colors, size));
            }, as_sprite, overwrite, $"Pattern '{name}' texture");
        }

        [Description("Create a gradient .png texture. Type 'linear' (with angle in degrees) or 'radial'. Colors interpolate across the palette. An existing file is never replaced unless overwrite=true.")]
        public static object ApplyGradient(
            [ToolParam("Asset path under Assets/, e.g. 'Assets/Textures/grad.png'")] string path,
            [ToolParam("Gradient type: 'linear' or 'radial'", Required = false)] string type = "linear",
            [ToolParam("Width in pixels", Required = false)] int width = 256,
            [ToolParam("Height in pixels", Required = false)] int height = 256,
            [ToolParam("Angle in degrees for linear gradients", Required = false)] float angle = 0f,
            [ToolParam("Semicolon-separated colors ('#hex', a color name, or 'r,g,b[,a]' with whole-number channels 0-255). Default black->white.", Required = false)] string palette = null,
            [ToolParam("Import as a Sprite", Required = false)] bool as_sprite = false,
            [ToolParam("Replace the file already at that path instead of refusing", Required = false)] bool overwrite = false)
        {
            bool radial = string.Equals(type?.Trim(), "radial", StringComparison.OrdinalIgnoreCase);
            if (!radial && !string.Equals(type?.Trim(), "linear", StringComparison.OrdinalIgnoreCase))
                return Response.Error("INVALID_PARAM", new { param = "type", provided = type, valid = new[] { "linear", "radial" } });

            if (!TryParsePalette(palette, out var parsed, out var paletteError))
                return InvalidColor("palette", palette, paletteError);

            var colors = parsed ?? new List<Color32> { new Color32(0, 0, 0, 255), new Color32(255, 255, 255, 255) };

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
            }, as_sprite, overwrite, $"{(radial ? "Radial" : "Linear")} gradient texture");
        }

        [Description("Create a Perlin-noise .png texture, mapping the noise value through the palette. Higher octaves add detail. An existing file is never replaced unless overwrite=true.")]
        public static object ApplyNoise(
            [ToolParam("Asset path under Assets/, e.g. 'Assets/Textures/noise.png'")] string path,
            [ToolParam("Width in pixels", Required = false)] int width = 256,
            [ToolParam("Height in pixels", Required = false)] int height = 256,
            [ToolParam("Noise scale (smaller = larger features)", Required = false)] float scale = 0.05f,
            [ToolParam("Number of octaves (1-8)", Required = false)] int octaves = 4,
            [ToolParam("Semicolon-separated colors ('#hex', a color name, or 'r,g,b[,a]' with whole-number channels 0-255). Default black->white.", Required = false)] string palette = null,
            [ToolParam("Import as a Sprite", Required = false)] bool as_sprite = false,
            [ToolParam("Replace the file already at that path instead of refusing", Required = false)] bool overwrite = false)
        {
            if (!TryParsePalette(palette, out var parsed, out var paletteError))
                return InvalidColor("palette", palette, paletteError);

            var colors = parsed ?? new List<Color32> { new Color32(0, 0, 0, 255), new Color32(255, 255, 255, 255) };
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
            }, as_sprite, overwrite, "Noise texture");
        }

        private static readonly string[] Patterns =
            { "checkerboard", "stripes", "stripes_v", "stripes_h", "stripes_diag", "dots", "grid", "brick" };

        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };

        private static object InvalidColor(string param, string provided, string detail) =>
            Response.Error("INVALID_PARAM", new { param, provided, detail },
                "Use '#hex', a color name such as 'red', or 'r,g,b[,a]' with whole-number channels 0-255 (not 0-1). Separate palette entries with ';'.");

        private static object Generate(string path, int width, int height, Action<Texture2D> paint, bool asSprite, bool overwrite, string what)
        {
            if (!AssetFunctions.TryNormalizeAssetPath(path, out var assetPath))
                return Response.Error("INVALID_PATH", new { path },
                    "path must be a file under Assets/ with no '..' segments, and no folder or file name that starts or ends " +
                    "with a dot or space, ends with '~', or holds a character a file name cannot");

            // Only PNG and JPG bytes are produced, so any other extension would put an image into a
            // .cs, .prefab or .meta, or into a format it is not.
            var extension = Path.GetExtension(assetPath);
            if (!Array.Exists(ImageExtensions, ext => string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase)))
                return Response.Error("INVALID_EXTENSION", new { path = assetPath, extension },
                    "Texture writers produce .png or .jpg/.jpeg files; end the path in one of those.");

            string absolutePath;
            try
            {
                absolutePath = PathSafety.ResolveAssetPath(assetPath);
            }
            catch (PathOutsideProjectException ex)
            {
                return Response.Error("INVALID_PATH", new { path = assetPath, message = ex.Message },
                    "path must be under Assets/, and must stay there once '..' segments are resolved");
            }

            if (!overwrite && (File.Exists(absolutePath) || Directory.Exists(absolutePath)))
                return Response.Error("TEXTURE_EXISTS", new { path = assetPath },
                    "Something already lives there and replacing it loses its content. Pass overwrite=true, or pick another path.");

            path = assetPath;
            width = Mathf.Clamp(width, 1, MaxDimension);
            height = Mathf.Clamp(height, 1, MaxDimension);

            var keptSlices = 0;
            if (AssetImporter.GetAtPath(path) is TextureImporter sheet &&
                sheet.textureType == TextureImporterType.Sprite && sheet.spriteImportMode == SpriteImportMode.Multiple)
            {
                keptSlices = SpriteFunctions.SlicedSpriteCount(sheet);
                if (keptSlices > 0)
                {
                    sheet.GetSourceTextureWidthAndHeight(out var oldWidth, out var oldHeight);
                    if (oldWidth != width || oldHeight != height)
                        return Response.Error("SPRITESHEET_EXISTS",
                            new { path, existingSprites = keptSlices, existingSize = new { width = oldWidth, height = oldHeight }, requestedSize = new { width, height } },
                            "The sheet at that path is sliced, and its sprite rects are in pixels of its current size, so they would not fit the new one. " +
                            "Write a texture of the same size (the slices are kept; re-cut them with slice_sprite_grid overwrite=true), or use another path.");
                }
            }

            Texture2D tex = null;
            try
            {
                tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                paint(tex);
                tex.Apply();

                var bytes = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? tex.EncodeToPNG() : tex.EncodeToJPG();

                var dir = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                AtomicFile.WriteAllBytes(absolutePath, bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

                if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
                    return Response.Error("TEXTURE_NOT_IMPORTED", new { path },
                        "The file was written but Unity did not import it as a texture.");

                if (asSprite && (importer.textureType != TextureImporterType.Sprite || importer.spriteImportMode == SpriteImportMode.None))
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.SaveAndReimport();
                }

                var isSprite = importer.textureType == TextureImporterType.Sprite;
                var warning = keptSlices > 0
                    ? $"The sheet's {keptSlices} existing sprite slice(s) were kept and still cut the new pixels at their old rects; re-cut with slice_sprite_grid overwrite=true if the layout changed."
                    : null;
                return Response.Success($"{what} created at '{path}' ({width}x{height})." + (warning != null ? " " + warning : ""),
                    new { path, width, height, sprite = isSprite, spriteMode = isSprite ? importer.spriteImportMode.ToString() : null, warning });
            }
            catch (Exception e)
            {
                return Response.Error("TEXTURE_WRITE_FAILED", new { path, message = e.Message });
            }
            finally
            {
                if (tex != null)
                    UnityEngine.Object.DestroyImmediate(tex);
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

        // A blank palette is "not given": colors stays null and the caller uses its default.
        internal static bool TryParsePalette(string palette, out List<Color32> colors, out string error)
        {
            colors = null;
            error = null;
            if (string.IsNullOrWhiteSpace(palette)) return true;

            var parsed = new List<Color32>();
            foreach (var entry in palette.Split(';'))
            {
                var text = entry.Trim();
                if (text.Length == 0) continue;
                if (!TryParseColor32(text, out var color, out var detail))
                {
                    error = $"palette entry {parsed.Count + 1}: {detail}";
                    return false;
                }
                parsed.Add(color);
            }

            if (parsed.Count > 0) colors = parsed;
            return true;
        }

        internal static bool TryParseColor32(string value, out Color32 color, out string error)
        {
            color = default;
            error = null;
            var text = value?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                error = "the color is empty";
                return false;
            }

            if (text.IndexOf(',') < 0)
            {
                if (ColorUtility.TryParseHtmlString(text, out var named))
                {
                    color = named;
                    return true;
                }
                error = $"'{value}' is not a #hex color, a color name, or 'r,g,b[,a]' numbers";
                return false;
            }

            var parts = text.Trim('(', ')', ' ').Split(',');
            if (parts.Length < 3 || parts.Length > 4)
            {
                error = $"'{value}' needs 3 or 4 comma-separated channels, got {parts.Length}";
                return false;
            }

            var channels = new byte[] { 0, 0, 0, 255 };
            for (var i = 0; i < parts.Length; i++)
            {
                if (!ValueConverter.TryParseInt(parts[i].Trim(), out var n) || n < 0 || n > 255)
                {
                    error = $"'{value}': channel {i + 1} ('{parts[i].Trim()}') is not a whole number from 0 to 255";
                    return false;
                }
                channels[i] = (byte)n;
            }

            color = new Color32(channels[0], channels[1], channels[2], channels[3]);
            return true;
        }

        internal static Color32 ParseColor(string value) =>
            TryParseColor32(value, out var color, out var error) ? color : throw new FormatException(error);

        internal static List<Color32> ParsePalette(string palette) =>
            TryParsePalette(palette, out var colors, out var error) ? colors : throw new FormatException(error);
    }
}
