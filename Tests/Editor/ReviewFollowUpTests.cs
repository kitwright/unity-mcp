// Copyright (C) KitWright. All rights reserved.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    public sealed class ReviewFollowUpTests
    {
        private const string FolderName = "__KitWrightReviewFollowUp";
        private const string Folder = "Assets/" + FolderName;
        private const string Sheet = Folder + "/KwSheet.png";
        private const string AtlasPath = Folder + "/KwAtlas.spriteatlas";

        private readonly List<GameObject> _made = new List<GameObject>();

        [SetUp]
        public void CreateFolder()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", FolderName);
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var go in _made)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
            _made.Clear();
            AssetDatabase.DeleteAsset(Folder);
        }

        private GameObject Make(string stem)
        {
            var go = new GameObject(stem + "_" + System.Guid.NewGuid().ToString("N"));
            _made.Add(go);
            return go;
        }

        private static SpriteAtlas Atlas() => AssetDatabase.LoadAssetAtPath<SpriteAtlas>(AtlasPath);

        private static TextureImporter Importer() => (TextureImporter)AssetImporter.GetAtPath(Sheet);

        [Test]
        public void AddToSpriteAtlas_DoesNotListAnAssetTwiceWhetherItIsAlreadyPackedOrNamedTwice()
        {
            Ok("create_texture", "path", Sheet, "width", "16", "height", "16");
            Ok("create_sprite_atlas", "path", AtlasPath);

            var first = Ok("add_to_sprite_atlas", "path", AtlasPath, "asset_paths", Sheet + ", " + Sheet);
            Assert.AreEqual(1, (int)first["data"]["added"]);
            Assert.AreEqual(1, Atlas().GetPackables().Length);

            var again = Ok("add_to_sprite_atlas", "path", AtlasPath, "asset_paths", Sheet + "," + Sheet);
            Assert.AreEqual(0, (int)again["data"]["added"]);
            Assert.AreEqual(1, (int)again["data"]["packableCount"]);
            StringAssert.Contains("already packed", (string)again["message"]);
            Assert.AreEqual(1, Atlas().GetPackables().Length);

            var removed = Ok("remove_from_sprite_atlas", "path", AtlasPath, "asset_paths", Sheet);
            Assert.AreEqual(1, (int)removed["data"]["removed"]);
            Assert.AreEqual(0, Atlas().GetPackables().Length);
        }

        [Test]
        public void SetCameraProjection_RefusesPerspectiveWhileTheNearClipPlaneIsNotPositive()
        {
            var camera = Make("KwCam").AddComponent<Camera>();
            camera.orthographic = true;
            camera.nearClipPlane = -5f;

            var refused = Refused("set_camera_projection", "projection", "perspective", "game_object_name", camera.name);
            Assert.AreEqual("INVALID_PARAM", (string)refused["code"]);
            Assert.AreEqual("near", (string)refused["data"]["param"]);
            StringAssert.Contains("set_camera_settings", (string)refused["hint"]);
            Assert.IsTrue(camera.orthographic, "A refused call must not flip the projection.");
            Assert.AreEqual(-5f, camera.nearClipPlane, 0.0001f);

            Ok("set_camera_settings", "game_object_name", camera.name, "near", "0.3");
            Ok("set_camera_projection", "projection", "perspective", "game_object_name", camera.name);
            Assert.IsFalse(camera.orthographic);
        }

        [Test]
        public void SetTagAndLayer_TrimsBothAndTreatsWhitespaceAsNotProvided()
        {
            var go = Make("KwTagLayer");

            Ok("set_tag_and_layer", "target", go.name, "tag", " EditorOnly ", "layer", " Ignore Raycast ");
            Assert.AreEqual("EditorOnly", go.tag);
            Assert.AreEqual(LayerMask.NameToLayer("Ignore Raycast"), go.layer);

            Assert.AreEqual("NO_PARAMS", Code("set_tag_and_layer", "target", go.name, "tag", "   ", "layer", "  "));
        }

        [Test]
        public void SetAssetImportSettings_MaxTextureSizeNeedsAJsonIntegerNotAFractionOrAString()
        {
            Ok("create_texture", "path", Sheet, "width", "64", "height", "32");
            var before = Importer().maxTextureSize;

            foreach (var json in new[] { "{\"maxTextureSize\":64.5}", "{\"maxTextureSize\":\"64\"}" })
                Assert.AreEqual("INVALID_SETTINGS", Code("set_asset_import_settings", "path", Sheet, "settings_json", json), json);
            Assert.AreEqual(before, Importer().maxTextureSize, "A refused call must not change the importer.");

            Ok("set_asset_import_settings", "path", Sheet, "settings_json", "{\"maxTextureSize\":64}");
            Assert.AreEqual(64, Importer().maxTextureSize);
            Ok("set_asset_import_settings", "path", Sheet, "settings_json", "{\"maxTextureSize\":128.0}");
            Assert.AreEqual(128, Importer().maxTextureSize);
        }
    }
}
