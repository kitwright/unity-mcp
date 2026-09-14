// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Sprite")]
    internal static class SpriteFunctions
    {
        [Description("Set a texture asset's type to Sprite. mode 'single' for one sprite, 'multiple' for a sheet you then slice. Sets pixels-per-unit. Reimports the asset.")]
        public static object SetTextureAsSprite(
            [ToolParam("Project-relative texture path, e.g. 'Assets/Art/hero.png'")] string path,
            [ToolParam("Sprite mode: 'single' or 'multiple'", Required = false)] string mode = "single",
            [ToolParam("Pixels per unit", Required = false)] float pixels_per_unit = 100f)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return Response.Error("NOT_A_TEXTURE", new { path });

            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = mode.Equals("multiple", StringComparison.OrdinalIgnoreCase)
                ? SpriteImportMode.Multiple
                : SpriteImportMode.Single;
            ti.spritePixelsPerUnit = pixels_per_unit;
            ti.SaveAndReimport();

            return Response.Success($"'{path}' set to Sprite ({ti.spriteImportMode}).", new
            {
                path,
                mode = ti.spriteImportMode.ToString(),
                pixelsPerUnit = pixels_per_unit
            });
        }

        [Description("Slice a Sprite sheet into a uniform grid of cells. Sets the texture to Multiple mode and generates one sprite per cell of the given pixel size. cell_width/cell_height in pixels; optional padding and offset. Names cells '<texture>_<index>'.")]
        public static object SliceSpriteGrid(
            [ToolParam("Project-relative texture path")] string path,
            [ToolParam("Cell width in pixels")] int cell_width,
            [ToolParam("Cell height in pixels")] int cell_height,
            [ToolParam("Pixel padding between cells", Required = false)] int padding = 0,
            [ToolParam("Pixel offset from top-left as 'x,y'", Required = false)] string offset = "0,0",
            [ToolParam("Pixels per unit", Required = false)] float pixels_per_unit = 100f)
        {
            if (cell_width <= 0 || cell_height <= 0)
                return Response.Error("INVALID_CELL_SIZE", new { cell_width, cell_height });

            // Padding is added to the step on both axes, so anything at or below -cell leaves the step
            // at zero or walking backwards - the loop then never ends and adds a SpriteMetaData per
            // turn until the editor runs out of memory. Overlapping cells (a small negative) are fine.
            var smallestCell = Math.Min(cell_width, cell_height);
            if (padding <= -smallestCell)
                return Response.Error("INVALID_PADDING", new { padding, cell_width, cell_height },
                    $"Padding must be greater than -{smallestCell}: at or below that the grid step " +
                    "stops advancing and the slice never terminates.");

            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return Response.Error("NOT_A_TEXTURE", new { path });

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null) return Response.Error("TEXTURE_LOAD_FAILED", new { path });

            if (!TryParseVector2Int(offset, out var off)) off = Vector2Int.zero;

            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Multiple;
            ti.spritePixelsPerUnit = pixels_per_unit;

            // The source image's size, not the imported Texture2D's: spritesheet rects live in source
            // pixels, while the imported texture is whatever maxTextureSize allowed. A 128x128 png
            // imported at 32 reported a texture too small for its own 64px cells.
            ti.GetSourceTextureWidthAndHeight(out var texW, out var texH);
            var metas = new List<SpriteMetaData>();
            string baseName = System.IO.Path.GetFileNameWithoutExtension(path);
            int index = 0;

            // Unity texture space is bottom-left origin; iterate rows top-down so index 0 is top-left.
            for (int y = texH - off.y - cell_height; y >= 0; y -= cell_height + padding)
            {
                for (int x = off.x; x + cell_width <= texW; x += cell_width + padding)
                {
                    metas.Add(new SpriteMetaData
                    {
                        name = $"{baseName}_{index}",
                        rect = new Rect(x, y, cell_width, cell_height),
                        alignment = (int)SpriteAlignment.Center,
                        pivot = new Vector2(0.5f, 0.5f)
                    });
                    index++;
                }
            }

            if (metas.Count == 0)
                return Response.Error("NO_CELLS_GENERATED", new { path, texW, texH, cell_width, cell_height, hint = "Cell size larger than the source image, or offset too big." });

#pragma warning disable CS0618
            ti.spritesheet = metas.ToArray();
#pragma warning restore CS0618
            ti.SaveAndReimport();

            return Response.Success($"Sliced '{path}' into {metas.Count} sprites.", new
            {
                path,
                spriteCount = metas.Count,
                cell = new { cell_width, cell_height },
                textureSize = new { texW, texH }
            });
        }

        [Description("List the sub-sprites defined on a Multiple-mode sprite sheet: name and pixel rect.")]
        [ReadOnlyTool]
        public static object GetSpriteSheetInfo(
            [ToolParam("Project-relative texture path")] string path)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return Response.Error("NOT_A_TEXTURE", new { path });

#pragma warning disable CS0618
            var sheet = ti.spritesheet ?? new SpriteMetaData[0];
#pragma warning restore CS0618
            var sprites = new List<object>();
            foreach (var s in sheet)
                sprites.Add(new { name = s.name, x = s.rect.x, y = s.rect.y, width = s.rect.width, height = s.rect.height });

            return Response.Success($"'{path}' has {sprites.Count} sub-sprite(s).", new
            {
                path,
                mode = ti.spriteImportMode.ToString(),
                sprites
            });
        }

        internal static bool TryParseVector2Int(string value, out Vector2Int result)
        {
            result = Vector2Int.zero;
            if (string.IsNullOrEmpty(value)) return false;
            var p = value.Trim('(', ')', ' ').Split(',');
            if (p.Length < 2) return false;
            if (!int.TryParse(p[0].Trim(), out var x)) return false;
            if (!int.TryParse(p[1].Trim(), out var y)) return false;
            result = new Vector2Int(x, y);
            return true;
        }
    }
}
