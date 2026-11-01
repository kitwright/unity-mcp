// Copyright (C) KitWright. All rights reserved.

using System.IO;
using System.Linq;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    public sealed class SpriteTextureFixesTests
    {
        private const string FolderName = "__KitWrightSpriteTextureFixes";
        private const string Folder = "Assets/" + FolderName;
        private const string Sheet = Folder + "/KwSheet.png";
        private const string AtlasPath = Folder + "/KwAtlas.spriteatlas";

        private GameObject _go;

        [SetUp]
        public void CreateFolder()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", FolderName);
        }

        [TearDown]
        public void DeleteFolder()
        {
            if (_go != null)
                Object.DestroyImmediate(_go);
            _go = null;
            AssetDatabase.DeleteAsset(Folder);
        }

        private static TextureImporter Importer(string path) => (TextureImporter)AssetImporter.GetAtPath(path);

        private static void MakeSheet(string path = Sheet) =>
            Ok("create_texture", "path", path, "width", "64", "height", "32");

        private static void MakeSlicedSheet()
        {
            MakeSheet();
            Ok("set_texture_as_sprite", "path", Sheet, "mode", "multiple", "pixels_per_unit", "64");
            Ok("slice_sprite_grid", "path", Sheet, "cell_width", "16", "cell_height", "16");
        }

        [Test]
        public void TextureWriters_RefuseAnExistingFileUnlessOverwriteIsGiven()
        {
            var path = Folder + "/KwKeep.png";
            Ok("create_texture", "path", path, "width", "4", "height", "4", "color", "#ff0000ff");
            var absolute = Path.GetFullPath(path);
            var original = File.ReadAllBytes(absolute);

            Assert.AreEqual("TEXTURE_EXISTS", Code("create_texture", "path", path, "color", "#0000ffff"));
            Assert.AreEqual("TEXTURE_EXISTS", Code("apply_pattern", "path", path, "pattern", "grid"));
            Assert.AreEqual("TEXTURE_EXISTS", Code("apply_gradient", "path", path));
            Assert.AreEqual("TEXTURE_EXISTS", Code("apply_noise", "path", path));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(absolute), "a refused call changed the file");

            Ok("create_texture", "path", path, "width", "4", "height", "4", "color", "#0000ffff", "overwrite", "true");
            CollectionAssert.AreNotEqual(original, File.ReadAllBytes(absolute));
        }

        [TestCase("Player.cs")]
        [TestCase("Hero.prefab")]
        [TestCase("Main.unity")]
        [TestCase("KwTex.png.meta")]
        [TestCase("NoExtension")]
        [TestCase("Note.txt")]
        public void TextureWriters_RefuseAPathThatIsNotAnImage(string file)
        {
            var path = Folder + "/" + file;

            Assert.AreEqual("INVALID_EXTENSION", Code("create_texture", "path", path));
            Assert.AreEqual("INVALID_EXTENSION", Code("apply_pattern", "path", path, "pattern", "dots"));
            Assert.IsFalse(File.Exists(Path.GetFullPath(path)), "the refused call still wrote a file");
        }

        [Test]
        public void TextureWriters_RefuseAFolderUnityDoesNotImport()
        {
            var path = Folder + "/Hidden~/x.png";

            Assert.AreEqual("INVALID_PATH", Code("create_texture", "path", path));
            Assert.IsFalse(Directory.Exists(Path.GetFullPath(Folder + "/Hidden~")));
        }

        [TestCase("1.0,0.5,0.0")]
        [TestCase("#zzz")]
        [TestCase("-5,0,0")]
        [TestCase("300,0,0")]
        [TestCase("1,5")]
        [TestCase("1,2,3,4,5")]
        [TestCase("a,b,c")]
        [TestCase("10,20,30,x")]
        public void TryParseColor32_RefusesWhatUsedToBecomeWhite(string text)
        {
            Assert.IsFalse(TextureFunctions.TryParseColor32(text, out _, out var error));
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void TryParseColor32_ReadsTheDocumentedForms()
        {
            Assert.IsTrue(TextureFunctions.TryParseColor32("#ff8000", out var hex, out _));
            AssertChannels(hex, 255, 128, 0, 255);

            Assert.IsTrue(TextureFunctions.TryParseColor32("(10, 20, 30, 40)", out var commas, out _));
            AssertChannels(commas, 10, 20, 30, 40);

            Assert.IsTrue(TextureFunctions.TryParseColor32("red", out var named, out _));
            AssertChannels(named, 255, 0, 0, 255);
        }

        private static void AssertChannels(Color32 c, int r, int g, int b, int a) =>
            CollectionAssert.AreEqual(new[] { r, g, b, a }, new int[] { c.r, c.g, c.b, c.a });

        [Test]
        public void TryParsePalette_NamesTheBadEntryAndTreatsABlankPaletteAsNotGiven()
        {
            Assert.IsFalse(TextureFunctions.TryParsePalette("#ff0000;1.0,0.5,0.0", out _, out var error));
            StringAssert.Contains("entry 2", error);

            Assert.IsTrue(TextureFunctions.TryParsePalette("  ", out var none, out _));
            Assert.IsNull(none);
        }

        [Test]
        public void TextureWriters_RefuseBadColorsPatternsAndTypesBeforeWritingAnything()
        {
            var path = Folder + "/KwNever.png";

            Assert.AreEqual("INVALID_PARAM", Code("create_texture", "path", path, "color", "1.0,0.5,0.0"));
            Assert.AreEqual("INVALID_PARAM", Code("apply_pattern", "path", path, "pattern", "dots", "palette", "#ff0000;nope"));
            Assert.AreEqual("INVALID_PARAM", Code("apply_pattern", "path", path, "pattern", "chekerboard"));
            Assert.AreEqual("INVALID_PARAM", Code("apply_gradient", "path", path, "type", "radia"));
            Assert.AreEqual("INVALID_PARAM", Code("apply_noise", "path", path, "palette", "300,0,0"));
            Assert.IsFalse(File.Exists(Path.GetFullPath(path)));
        }

        [Test]
        public void TextureWriters_AsSpriteImportsANewFileAsASingleSprite()
        {
            var created = Ok("create_texture", "path", Sheet, "width", "16", "height", "16", "as_sprite", "true");

            Assert.AreEqual(TextureImporterType.Sprite, Importer(Sheet).textureType);
            Assert.AreEqual(SpriteImportMode.Single, Importer(Sheet).spriteImportMode);
            Assert.IsTrue((bool)created["data"]["sprite"]);
            Assert.AreEqual("Single", (string)created["data"]["spriteMode"]);

            var pattern = Folder + "/KwPattern.png";
            Ok("apply_pattern", "path", pattern, "pattern", "grid", "width", "16", "height", "16", "as_sprite", "true");
            Assert.AreEqual(SpriteImportMode.Single, Importer(pattern).spriteImportMode);
        }

        [Test]
        public void TextureWriters_OverASlicedSheetOfTheSameSizeKeepTheSlicesAndSaySo()
        {
            MakeSlicedSheet();

            var plain = Ok("create_texture", "path", Sheet, "width", "64", "height", "32", "overwrite", "true");
            Assert.IsTrue((bool)plain["data"]["sprite"], "the reply echoed the as_sprite parameter instead of the importer");
            Assert.AreEqual("Multiple", (string)plain["data"]["spriteMode"]);
            StringAssert.Contains("slice", (string)plain["data"]["warning"]);
            StringAssert.Contains("slice", (string)plain["message"]);

            var asSprite = Ok("create_texture", "path", Sheet, "width", "64", "height", "32", "overwrite", "true", "as_sprite", "true");
            Assert.AreEqual("Multiple", (string)asSprite["data"]["spriteMode"], "an overwritten sheet must keep Multiple");
            Assert.AreEqual(SpriteImportMode.Multiple, Importer(Sheet).spriteImportMode);
            Assert.AreEqual(8, Ok("get_sprite_sheet_info", "path", Sheet)["data"]["sprites"].Count());
        }

        [Test]
        public void TextureWriters_RefuseToOverwriteASlicedSheetWithADifferentSizeBeforeWriting()
        {
            MakeSlicedSheet();
            var absolute = Path.GetFullPath(Sheet);
            var original = File.ReadAllBytes(absolute);

            var refused = Refused("create_texture", "path", Sheet, "width", "16", "height", "16", "overwrite", "true");
            Assert.AreEqual("SPRITESHEET_EXISTS", (string)refused["code"]);
            Assert.AreEqual(8, (int)refused["data"]["existingSprites"]);
            Assert.AreEqual(Sheet, (string)refused["data"]["path"]);
            Assert.AreEqual("SPRITESHEET_EXISTS", Code("apply_noise", "path", Sheet, "width", "64", "height", "16", "overwrite", "true"));

            CollectionAssert.AreEqual(original, File.ReadAllBytes(absolute), "a refused call changed the file");
            Assert.AreEqual(8, Ok("get_sprite_sheet_info", "path", Sheet)["data"]["sprites"].Count(), "a refused call changed the slices");
        }

        [Test]
        public void GridCells_DoesNotWrapAnIntStepWhenPaddingOrOffsetIsHuge()
        {
            var huge = SpriteFunctions.GridCells(64, 32, 1, 1, int.MaxValue, Vector2Int.zero, 10000);
            Assert.AreEqual(1, huge.Count, "padding int.MaxValue leaves one cell, it must not loop");

            var farOffset = SpriteFunctions.GridCells(64, 32, 1, 1, 0, new Vector2Int(int.MaxValue, 0), 10000);
            Assert.AreEqual(0, farOffset.Count);

            var farRows = SpriteFunctions.GridCells(64, 32, 1, 1, 0, new Vector2Int(0, int.MaxValue), 10000);
            Assert.AreEqual(0, farRows.Count);
        }

        [Test]
        public void GridCells_StopsAtTheLimitInsteadOfBuildingMillionsOfCells()
        {
            Assert.IsNull(SpriteFunctions.GridCells(4096, 4096, 1, 1, 0, Vector2Int.zero, 10000));
            Assert.AreEqual(8, SpriteFunctions.GridCells(64, 32, 16, 16, 0, Vector2Int.zero, 10000).Count);
        }

        [TestCase("12;4")]
        [TestCase("abc")]
        [TestCase("1,2,3")]
        public void TryParseVector2Int_RefusesWhatIsNotExactlyTwoWholeNumbers(string text)
        {
            Assert.IsFalse(SpriteFunctions.TryParseVector2Int(text, out _));
        }

        [TestCase(32, true)]
        [TestCase(64, true)]
        [TestCase(16384, true)]
        [TestCase(31, false)]
        [TestCase(1000, false)]
        [TestCase(100000, false)]
        [TestCase(32768, false)]
        [TestCase(0, false)]
        [TestCase(-64, false)]
        public void IsValidMaxTextureSize_OnlyAcceptsPowersOfTwoUnityTakes(int size, bool expected)
        {
            Assert.AreEqual(expected, AssetImportFunctions.IsValidMaxTextureSize(size));
        }

        [Test]
        public void SetAssetImportSettings_RefusesAMaxTextureSizeUnityWouldClamp()
        {
            MakeSheet();

            Assert.AreEqual("INVALID_SETTINGS",
                Code("set_asset_import_settings", "path", Sheet, "settings_json", "{\"maxTextureSize\":1000}"));
            Ok("set_asset_import_settings", "path", Sheet, "settings_json", "{\"maxTextureSize\":512}");
        }

        [Test]
        public void SetTextureAsSprite_ChangesOnlyWhatWasPassed()
        {
            MakeSheet();
            Ok("set_texture_as_sprite", "path", Sheet, "mode", "multiple", "pixels_per_unit", "64");

            Ok("set_texture_as_sprite", "path", Sheet);
            Assert.AreEqual(SpriteImportMode.Multiple, Importer(Sheet).spriteImportMode, "an omitted mode reset a Multiple sheet");
            Assert.AreEqual(64f, Importer(Sheet).spritePixelsPerUnit, 0.001f, "an omitted pixels_per_unit reset the value");

            Ok("set_texture_as_sprite", "path", Sheet, "pixels_per_unit", "32");
            Assert.AreEqual(SpriteImportMode.Multiple, Importer(Sheet).spriteImportMode);
            Assert.AreEqual(32f, Importer(Sheet).spritePixelsPerUnit, 0.001f);
        }

        [Test]
        public void SetTextureAsSprite_RefusesAnUnknownModeAndANonPositivePpuWithoutTouchingTheImporter()
        {
            MakeSheet();

            Assert.AreEqual("INVALID_PARAM", Code("set_texture_as_sprite", "path", Sheet, "mode", "multi"));
            Assert.AreEqual("INVALID_PARAM", Code("set_texture_as_sprite", "path", Sheet, "pixels_per_unit", "0"));
            Assert.AreNotEqual(TextureImporterType.Sprite, Importer(Sheet).textureType);
        }

        [Test]
        public void SliceSpriteGrid_KeepsThePixelsPerUnitItWasNotGiven()
        {
            MakeSheet();
            Ok("set_texture_as_sprite", "path", Sheet, "mode", "multiple", "pixels_per_unit", "64");

            Ok("slice_sprite_grid", "path", Sheet, "cell_width", "16", "cell_height", "16");

            Assert.AreEqual(64f, Importer(Sheet).spritePixelsPerUnit, 0.001f);
        }

        [Test]
        public void SliceSpriteGrid_RefusesToDiscardExistingSlicesUnlessOverwriteIsGiven()
        {
            MakeSlicedSheet();

            Assert.AreEqual("SPRITESHEET_EXISTS",
                Code("slice_sprite_grid", "path", Sheet, "cell_width", "32", "cell_height", "32"));
            Assert.AreEqual(8, Ok("get_sprite_sheet_info", "path", Sheet)["data"]["sprites"].Count(), "a refused call changed the sheet");

            var replaced = Ok("slice_sprite_grid", "path", Sheet, "cell_width", "32", "cell_height", "32", "overwrite", "true");
            Assert.AreEqual(2, (int)replaced["data"]["spriteCount"]);
            Assert.AreEqual(8, (int)replaced["data"]["replacedSprites"]);
        }

        [Test]
        public void SliceSpriteGrid_RefusesABadOffsetAndLeavesTheImporterAlone()
        {
            MakeSheet();

            Assert.AreEqual("INVALID_OFFSET", Code("slice_sprite_grid", "path", Sheet, "cell_width", "16", "cell_height", "16", "offset", "12;4"));
            Assert.AreEqual("INVALID_OFFSET", Code("slice_sprite_grid", "path", Sheet, "cell_width", "16", "cell_height", "16", "offset", "-8,-8"));
            Assert.AreEqual("NO_CELLS_GENERATED", Code("slice_sprite_grid", "path", Sheet, "cell_width", "999", "cell_height", "999"));
            Assert.AreNotEqual(TextureImporterType.Sprite, Importer(Sheet).textureType,
                "a refused slice left Sprite/Multiple on the live importer for the next reimport to persist");
        }

        [Test]
        public void SliceSpriteGrid_RefusesAGridOfMoreThanTheCellLimit()
        {
            Ok("create_texture", "path", Sheet, "width", "128", "height", "128");

            Assert.AreEqual("TOO_MANY_CELLS", Code("slice_sprite_grid", "path", Sheet, "cell_width", "1", "cell_height", "1"));
            Assert.AreNotEqual(TextureImporterType.Sprite, Importer(Sheet).textureType);
        }

        [Test]
        public void SpriteAtlas_RefusesWhatItCannotPackAndCountsWhatActuallyChanged()
        {
            MakeSheet();
            var note = Folder + "/KwNote.txt";
            File.WriteAllText(Path.GetFullPath(note), "x");
            AssetDatabase.ImportAsset(note);
            Ok("create_sprite_atlas", "path", AtlasPath);

            Assert.AreEqual("NOT_PACKABLE", Code("add_to_sprite_atlas", "path", AtlasPath, "asset_paths", note));
            Assert.AreEqual("NOT_PACKABLE", Code("add_to_sprite_atlas", "path", AtlasPath, "asset_paths", AtlasPath));

            Assert.AreEqual("NOT_PACKED", Code("remove_from_sprite_atlas", "path", AtlasPath, "asset_paths", Sheet));

            var added = Ok("add_to_sprite_atlas", "path", AtlasPath, "asset_paths", Sheet);
            Assert.AreEqual(1, (int)added["data"]["added"]);
            var removed = Ok("remove_from_sprite_atlas", "path", AtlasPath, "asset_paths", Sheet);
            Assert.AreEqual(1, (int)removed["data"]["removed"]);
        }

        [Test]
        public void SetSpriteAtlasSettings_RefusesAnEmptyCallAndValuesUnityDoesNotDefine()
        {
            Ok("create_sprite_atlas", "path", AtlasPath);

            Assert.AreEqual("NOTHING_TO_SET", Code("set_sprite_atlas_settings", "path", AtlasPath));
            Assert.AreEqual("INVALID_PARAM", Code("set_sprite_atlas_settings", "path", AtlasPath, "padding", "-1"));
            Assert.AreEqual("INVALID_PARAM", Code("set_sprite_atlas_settings", "path", AtlasPath, "padding", "2000000000"));
            Assert.AreEqual("INVALID_FILTER_MODE", Code("set_sprite_atlas_settings", "path", AtlasPath, "filter_mode", "99"));
            Assert.AreEqual("INVALID_FILTER_MODE", Code("set_sprite_atlas_settings", "path", AtlasPath, "filter_mode", "Bilinear,Trilinear"));
            Ok("set_sprite_atlas_settings", "path", AtlasPath, "filter_mode", "point");
            Assert.AreEqual(FilterMode.Point, AssetDatabase.LoadAssetAtPath<SpriteAtlas>(AtlasPath).GetTextureSettings().filterMode);
        }

        [Test]
        public void CreateSpriteAtlas_RefusesAFolderUnityDoesNotImportAndAnEscapingPath()
        {
            Assert.AreEqual("INVALID_PATH", Code("create_sprite_atlas", "path", Folder + "/Hidden~/a.spriteatlas"));
            Assert.AreEqual("INVALID_PATH", Code("create_sprite_atlas", "path", Folder + "/../../Escape.spriteatlas"));
            Assert.IsFalse(Directory.Exists(Path.GetFullPath(Folder + "/Hidden~")));

            var created = Ok("create_sprite_atlas", "path", "assets//" + FolderName + "//KwNormalized");
            Assert.AreEqual(Folder + "/KwNormalized.spriteatlas", (string)created["data"]["path"]);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SpriteAtlas>(Folder + "/KwNormalized.spriteatlas"));
        }

        [Test]
        public void SpriteField_OfASlicedSheet_NeedsASubAssetNameInsteadOfGuessing()
        {
            MakeSlicedSheet();
            _go = new GameObject("__KitWrightSpriteSubAssetProbe");
            var renderer = _go.AddComponent<SpriteRenderer>();

            foreach (var field in new[] { "sprite", "m_Sprite" })
            {
                renderer.sprite = null;

                var ambiguous = ComponentSerializer.WriteProperties(renderer,
                    JObject.Parse($@"{{""{field}"": {{""assetPath"": ""{Sheet}""}}}}"))[0];
                Assert.IsFalse(ambiguous.Success, field);
                StringAssert.Contains("subAsset", ambiguous.Error);
                Assert.IsNull(renderer.sprite, field + ": a sprite was guessed");

                var named = ComponentSerializer.WriteProperties(renderer,
                    JObject.Parse($@"{{""{field}"": {{""assetPath"": ""{Sheet}"", ""subAsset"": ""KwSheet_3""}}}}"))[0];
                Assert.IsTrue(named.Success, field + ": " + named.Error);
                Assert.AreEqual("KwSheet_3", renderer.sprite.name, field);

                var missing = ComponentSerializer.WriteProperties(renderer,
                    JObject.Parse($@"{{""{field}"": {{""assetPath"": ""{Sheet}"", ""subAsset"": ""NoSuchSprite""}}}}"))[0];
                Assert.IsFalse(missing.Success, field);
                StringAssert.Contains("KwSheet_0", missing.Error);
            }
        }
    }
}
