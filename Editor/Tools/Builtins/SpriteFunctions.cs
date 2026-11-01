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
        private const int MaxSliceCells = 10000;

        // Unity 6000.6 reports one entry for a sheet that was only switched to Multiple: "<name>_0" over the
        // whole texture, with no border. Nobody cut that, so it is not a slice to protect.
        internal static int SlicedSpriteCount(TextureImporter importer)
        {
#pragma warning disable CS0618
            var sheet = importer.spritesheet;
#pragma warning restore CS0618
            if (sheet == null || sheet.Length == 0)
                return 0;

            if (sheet.Length == 1)
            {
                importer.GetSourceTextureWidthAndHeight(out var width, out var height);
                var only = sheet[0];
                if (only.name == System.IO.Path.GetFileNameWithoutExtension(importer.assetPath) + "_0" &&
                    only.border == Vector4.zero && only.rect == new Rect(0f, 0f, width, height))
                {
                    return 0;
                }
            }

            return sheet.Length;
        }

        [Description("Set a texture asset's type to Sprite. mode 'single' for one sprite, 'multiple' for a sheet you then slice. Reimports the asset. Only what you pass is changed: without mode a texture that is already a Sprite keeps its mode (anything else becomes 'single'), without pixels_per_unit the current value stays.")]
        public static object SetTextureAsSprite(
            [ToolParam("Project-relative texture path, e.g. 'Assets/Art/hero.png'")] string path,
            [ToolParam("Sprite mode: 'single' or 'multiple'", Required = false)] string mode = null,
            [ToolParam("Pixels per unit (greater than 0)", Required = false)] float? pixels_per_unit = null)
        {
            SpriteImportMode? requested = null;
            if (mode != null)
            {
                var text = mode.Trim();
                if (text.Equals("single", StringComparison.OrdinalIgnoreCase)) requested = SpriteImportMode.Single;
                else if (text.Equals("multiple", StringComparison.OrdinalIgnoreCase)) requested = SpriteImportMode.Multiple;
                else return Response.Error("INVALID_PARAM", new { param = "mode", provided = mode, valid = new[] { "single", "multiple" } });
            }

            if (pixels_per_unit.HasValue && pixels_per_unit.Value <= 0f)
                return Response.Error("INVALID_PARAM", new { param = "pixels_per_unit", provided = pixels_per_unit.Value },
                    "pixels_per_unit must be greater than 0.");

            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return Response.Error("NOT_A_TEXTURE", new { path });

            var wasSprite = ti.textureType == TextureImporterType.Sprite;
            ti.textureType = TextureImporterType.Sprite;
            if (requested.HasValue)
                ti.spriteImportMode = requested.Value;
            else if (!wasSprite || ti.spriteImportMode == SpriteImportMode.None)
                ti.spriteImportMode = SpriteImportMode.Single;
            if (pixels_per_unit.HasValue)
                ti.spritePixelsPerUnit = pixels_per_unit.Value;
            ti.SaveAndReimport();

            return Response.Success($"'{path}' set to Sprite ({ti.spriteImportMode}).", new
            {
                path,
                mode = ti.spriteImportMode.ToString(),
                pixelsPerUnit = ti.spritePixelsPerUnit
            });
        }

        [Description("Slice a Sprite sheet into a uniform grid of cells. Sets the texture to Multiple mode and generates one sprite per cell of the given pixel size. cell_width/cell_height in pixels; optional padding and offset ('x,y', both 0 or more); at most 10000 cells. Names cells '<texture>_<index>'. A sheet that already has sprites is refused unless overwrite=true: the old slices, with their names, pivots and borders, are replaced and anything referencing them loses its sprite. pixels_per_unit changes only when passed.")]
        public static object SliceSpriteGrid(
            [ToolParam("Project-relative texture path")] string path,
            [ToolParam("Cell width in pixels")] int cell_width,
            [ToolParam("Cell height in pixels")] int cell_height,
            [ToolParam("Pixel padding between cells", Required = false)] int padding = 0,
            [ToolParam("Pixel offset from top-left as 'x,y'", Required = false)] string offset = "0,0",
            [ToolParam("Pixels per unit (greater than 0); omitted keeps the current value", Required = false)] float? pixels_per_unit = null,
            [ToolParam("Replace the sprites the sheet already has instead of refusing", Required = false)] bool overwrite = false)
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

            var off = Vector2Int.zero;
            if (!string.IsNullOrWhiteSpace(offset) && (!TryParseVector2Int(offset, out off) || off.x < 0 || off.y < 0))
                return Response.Error("INVALID_OFFSET", new { offset },
                    "offset must be exactly two whole numbers 'x,y', both 0 or more.");

            if (pixels_per_unit.HasValue && pixels_per_unit.Value <= 0f)
                return Response.Error("INVALID_PARAM", new { param = "pixels_per_unit", provided = pixels_per_unit.Value },
                    "pixels_per_unit must be greater than 0.");

            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return Response.Error("NOT_A_TEXTURE", new { path });

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null) return Response.Error("TEXTURE_LOAD_FAILED", new { path });

            // The source image's size, not the imported Texture2D's: spritesheet rects live in source
            // pixels, while the imported texture is whatever maxTextureSize allowed. A 128x128 png
            // imported at 32 reported a texture too small for its own 64px cells.
            ti.GetSourceTextureWidthAndHeight(out var texW, out var texH);

            var cells = GridCells(texW, texH, cell_width, cell_height, padding, off, MaxSliceCells);
            if (cells == null)
                return Response.Error("TOO_MANY_CELLS", new { path, texW, texH, cell_width, cell_height, padding, max = MaxSliceCells },
                    $"The grid holds more than {MaxSliceCells} cells. Use a larger cell size.");

            string baseName = System.IO.Path.GetFileNameWithoutExtension(path);
            var metas = new List<SpriteMetaData>(cells.Count);
            for (var i = 0; i < cells.Count; i++)
            {
                metas.Add(new SpriteMetaData
                {
                    name = $"{baseName}_{i}",
                    rect = cells[i],
                    alignment = (int)SpriteAlignment.Center,
                    pivot = new Vector2(0.5f, 0.5f)
                });
            }

            if (metas.Count == 0)
                return Response.Error("NO_CELLS_GENERATED", new { path, texW, texH, cell_width, cell_height, hint = "Cell size larger than the source image, or offset too big." });

            var existing = SlicedSpriteCount(ti);
#pragma warning disable CS0618
            if (existing > 0 && !overwrite)
                return Response.Error("SPRITESHEET_EXISTS", new { path, existingSprites = existing, wouldCreate = metas.Count },
                    "The sheet already has sprites, possibly cut by hand with their own names, pivots and borders. " +
                    "Slicing replaces all of them and anything referencing them loses its sprite. Pass overwrite=true to replace them.");

            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Multiple;
            if (pixels_per_unit.HasValue)
                ti.spritePixelsPerUnit = pixels_per_unit.Value;
            ti.spritesheet = metas.ToArray();
#pragma warning restore CS0618
            // The setter does not mark the importer dirty; with nothing else changed SaveAndReimport writes and imports nothing.
            EditorUtility.SetDirty(ti);
            ti.SaveAndReimport();

            return Response.Success($"Sliced '{path}' into {metas.Count} sprites.", new
            {
                path,
                spriteCount = metas.Count,
                replacedSprites = existing,
                pixelsPerUnit = ti.spritePixelsPerUnit,
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

        // Null when the grid holds more than limit cells. long: padding or offset near int.MaxValue
        // wrapped an int step negative, so x never left the texture and the inner loop ran until
        // memory ran out. Padding must already be above -min(cell), or the step does not advance.
        internal static List<Rect> GridCells(int texW, int texH, int cellW, int cellH, int padding, Vector2Int offset, int limit)
        {
            var cells = new List<Rect>();
            long stepX = (long)cellW + padding, stepY = (long)cellH + padding;

            // Unity texture space is bottom-left origin; iterate rows top-down so index 0 is top-left.
            for (long y = (long)texH - offset.y - cellH; y >= 0; y -= stepY)
            {
                for (long x = offset.x; x + cellW <= texW; x += stepX)
                {
                    if (cells.Count >= limit)
                        return null;

                    cells.Add(new Rect(x, y, cellW, cellH));
                }
            }

            return cells;
        }

        internal static bool TryParseVector2Int(string value, out Vector2Int result)
        {
            result = Vector2Int.zero;
            if (string.IsNullOrEmpty(value)) return false;
            var p = value.Trim('(', ')', ' ').Split(',');
            if (p.Length != 2) return false;
            if (!ValueConverter.TryParseInt(p[0].Trim(), out var x)) return false;
            if (!ValueConverter.TryParseInt(p[1].Trim(), out var y)) return false;
            result = new Vector2Int(x, y);
            return true;
        }
    }
}
