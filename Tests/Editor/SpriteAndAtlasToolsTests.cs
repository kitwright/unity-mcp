// Copyright (C) KitWright. Licensed under MIT.

using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
// The atlas getters this reads (GetPackables, GetPackingSettings, IsIncludeInBuild) are editor-only
// extension methods, like the setters the tool itself uses.
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The generated-texture writers and the sprite/atlas importer tools, in the order an agent uses
    /// them: paint an image, tell the importer it is a sprite sheet, slice it, then pack it. Each step
    /// is read back off the importer or the atlas asset, because all of them report success from the
    /// call and only the reimport decides what the project actually holds.
    /// </summary>
    public sealed class SpriteAndAtlasToolsTests
    {
        private const string FolderName = "__KitWrightPixelToolTests";
        private const string Folder = "Assets/" + FolderName;
        private const string Sheet = Folder + "/KwSheet.png";
        private const string AtlasPath = Folder + "/KwAtlas.spriteatlas";

        [SetUp]
        public void CreateFolder()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", FolderName);
        }

        [TearDown]
        public void DeleteFolder()
        {
            AssetDatabase.DeleteAsset(Folder);
        }

        private static TextureImporter Importer(string path) => (TextureImporter)AssetImporter.GetAtPath(path);

        // Reloaded per assertion: every one of these tools reimports the asset, which invalidates the
        // instance the previous call handed out.
        private static SpriteAtlas Atlas() => AssetDatabase.LoadAssetAtPath<SpriteAtlas>(AtlasPath);

        [Test]
        public void ThePatternGradientAndNoiseWritersEachLeaveAnImportedImageOnDisk()
        {
            var checker = Folder + "/KwChecker.png";
            var gradient = Folder + "/KwGradient.png";
            var noise = Folder + "/KwNoise.png";

            Ok("apply_pattern", "path", checker,
                "pattern", "checkerboard", "width", "32", "height", "32", "pattern_size", "8");
            Ok("apply_gradient", "path", gradient,
                "type", "radial", "width", "16", "height", "16", "palette", "#ff0000ff;#0000ffff");
            Ok("apply_noise", "path", noise,
                "width", "16", "height", "16", "octaves", "2", "as_sprite", "true");

            Assert.AreEqual(32, AssetDatabase.LoadAssetAtPath<Texture2D>(checker).width,
                "The imported texture should be the size that was asked for.");
            Assert.AreEqual(16, AssetDatabase.LoadAssetAtPath<Texture2D>(gradient).height);
            Assert.IsTrue(File.Exists(noise));
            Assert.AreEqual(TextureImporterType.Sprite, Importer(noise).textureType,
                "as_sprite has to reach the importer, not just the answer.");

            // Writing outside the project is the one thing these must never do.
            Assert.AreEqual("INVALID_PATH", Code("apply_pattern", "path", "C:/KwOutside.png", "pattern", "dots"));
            Assert.AreEqual("INVALID_PATH", Code("apply_gradient", "path", "../KwOutside.png"));
        }

        [Test]
        public void ASheetIsMarkedMultipleThenSlicedIntoOneSpritePerCell()
        {
            Ok("apply_pattern", "path", Sheet, "pattern", "grid", "width", "64", "height", "32", "pattern_size", "16");

            Ok("set_texture_as_sprite", "path", Sheet, "mode", "multiple", "pixels_per_unit", "64");
            Assert.AreEqual(TextureImporterType.Sprite, Importer(Sheet).textureType);
            Assert.AreEqual(SpriteImportMode.Multiple, Importer(Sheet).spriteImportMode);
            Assert.AreEqual(64f, Importer(Sheet).spritePixelsPerUnit, 0.001f);

            var sliced = Ok("slice_sprite_grid", "path", Sheet, "cell_width", "16", "cell_height", "16");
            Assert.AreEqual(8, (int)sliced["data"]["spriteCount"], "A 64x32 sheet holds 4x2 cells of 16px.");

            var info = Ok("get_sprite_sheet_info", "path", Sheet);
            Assert.AreEqual(8, info["data"]["sprites"].Count(), "The slices have to survive the reimport.");

            Assert.AreEqual("INVALID_CELL_SIZE",
                Code("slice_sprite_grid", "path", Sheet, "cell_width", "0", "cell_height", "16"));
            Assert.AreEqual("NO_CELLS_GENERATED",
                Code("slice_sprite_grid", "path", Sheet, "cell_width", "999", "cell_height", "999"));
            Assert.AreEqual("NOT_A_TEXTURE",
                Code("set_texture_as_sprite", "path", Folder + "/NoSuch.png"));
        }

        [Test]
        public void APaddingThatStopsTheGridAdvancingIsRefused()
        {
            Ok("apply_pattern", "path", Sheet, "pattern", "grid", "width", "64", "height", "32", "pattern_size", "16");

            // Padding is added to the step, so -cell leaves it at zero and anything below walks
            // backwards. Either way the loop never ends and adds a sprite per turn until the editor
            // runs out of memory - an input the tool must refuse rather than accept and hang on.
            Assert.AreEqual("INVALID_PADDING",
                Code("slice_sprite_grid", "path", Sheet, "cell_width", "16", "cell_height", "16", "padding", "-16"));
            Assert.AreEqual("INVALID_PADDING",
                Code("slice_sprite_grid", "path", Sheet, "cell_width", "16", "cell_height", "16", "padding", "-40"));

            // Overlapping cells are a legitimate sheet layout and must still slice.
            var overlapped = Ok("slice_sprite_grid", "path", Sheet,
                "cell_width", "16", "cell_height", "16", "padding", "-8");
            Assert.AreEqual(21, (int)overlapped["data"]["spriteCount"],
                "A step of 8 over 64x32 gives 7 columns and 3 rows.");
        }

        [Test]
        public void SlicingMeasuresTheSourceImageNotTheImportedTexture()
        {
            Ok("apply_pattern", "path", Sheet, "pattern", "grid", "width", "128", "height", "128", "pattern_size", "16");

            var importer = Importer(Sheet);
            importer.maxTextureSize = 32;
            importer.SaveAndReimport();
            Assert.AreEqual(32, AssetDatabase.LoadAssetAtPath<Texture2D>(Sheet).width,
                "Setup: the import has to be downscaled for this to mean anything.");

            // Sprite rects are in source pixels. Measuring the imported Texture2D instead reported a
            // 128px sheet as too small to hold its own 64px cells.
            var sliced = Ok("slice_sprite_grid", "path", Sheet, "cell_width", "64", "cell_height", "64");

            Assert.AreEqual(4, (int)sliced["data"]["spriteCount"]);
        }

        [Test]
        public void AtlasPackablesGoInAndComeOutAndTheSettingsSurviveTheReimport()
        {
            Ok("apply_pattern", "path", Sheet, "pattern", "dots", "width", "32", "height", "32");
            Ok("create_sprite_atlas", "path", AtlasPath);

            Ok("add_to_sprite_atlas", "path", AtlasPath, "asset_paths", Sheet);
            Assert.AreEqual(1, Atlas().GetPackables().Length);

            Assert.AreEqual("ASSET_NOT_FOUND",
                Code("add_to_sprite_atlas", "path", AtlasPath, "asset_paths", Folder + "/NoSuch.png"));
            Assert.AreEqual(1, Atlas().GetPackables().Length, "A refused add must not change the atlas.");
            Assert.AreEqual("ATLAS_NOT_FOUND",
                Code("add_to_sprite_atlas", "path", Folder + "/NoSuch.spriteatlas", "asset_paths", Sheet));

            Ok("set_sprite_atlas_settings", "path", AtlasPath,
                "padding", "8", "filter_mode", "Point", "include_in_build", "false", "enable_tight_packing", "true");
            Assert.AreEqual(8, Atlas().GetPackingSettings().padding);
            Assert.IsTrue(Atlas().GetPackingSettings().enableTightPacking);
            Assert.AreEqual(FilterMode.Point, Atlas().GetTextureSettings().filterMode);
            Assert.IsFalse(Atlas().IsIncludeInBuild());

            Assert.AreEqual("INVALID_FILTER_MODE",
                Code("set_sprite_atlas_settings", "path", AtlasPath, "filter_mode", "Wobble"));

            // A refused call must change nothing. filter_mode used to be parsed last, so the
            // settings named before it were already applied to the asset by the time the answer
            // said the call had failed - and they reach disk on whatever saves the atlas next.
            Assert.AreEqual("INVALID_FILTER_MODE",
                Code("set_sprite_atlas_settings", "path", AtlasPath,
                    "include_in_build", "true", "padding", "16", "filter_mode", "Wobble"));
            Assert.IsFalse(Atlas().IsIncludeInBuild(), "include_in_build was applied before the refusal.");
            Assert.AreEqual(8, Atlas().GetPackingSettings().padding, "padding was applied before the refusal.");

            Ok("remove_from_sprite_atlas", "path", AtlasPath, "asset_paths", Sheet);
            Assert.AreEqual(0, Atlas().GetPackables().Length);
        }
    }
}
