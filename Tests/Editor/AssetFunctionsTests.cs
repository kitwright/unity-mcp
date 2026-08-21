// Copyright (C) KitWright. Licensed under MIT.

using System;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class AssetFunctionsTests
    {
        private const string FolderName = "__KitWrightAssetFunctionsTests";
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = "Assets/" + FolderName;
            if (!AssetDatabase.IsValidFolder(_folder))
                AssetDatabase.CreateFolder("Assets", FolderName);
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(_folder))
                AssetDatabase.DeleteAsset(_folder);
        }

        [Test]
        public void DeleteAsset_MovesTheAssetToTheTrashInsteadOfUnlinkingIt()
        {
            var path = _folder + "/Doomed_" + Guid.NewGuid().ToString("N") + ".mat";
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            Assert.IsNotNull(shader, "A test shader is required.");
            AssetDatabase.CreateAsset(new Material(shader), path);
            AssetDatabase.SaveAssets();
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(path), "Setup failed: asset was not created.");

            var result = AssetFunctions.DeleteAsset(path);

            StringAssert.Contains("trash", result);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(path), "Asset is still loadable after delete.");
        }

        [Test]
        public void DeleteSpriteAtlas_MovesTheAtlasToTheTrashInsteadOfUnlinkingIt()
        {
            var path = _folder + "/Doomed_" + Guid.NewGuid().ToString("N") + ".spriteatlas";
            var created = SpriteAtlasFunctions.CreateSpriteAtlas(path).ToString();
            Assert.IsNotNull(AssetDatabase.LoadMainAssetAtPath(path), "Setup failed: " + created);

            var result = SpriteAtlasFunctions.DeleteSpriteAtlas(path).ToString();

            StringAssert.Contains("trash", result);
            Assert.IsNull(AssetDatabase.LoadMainAssetAtPath(path), "Atlas is still loadable after delete.");
        }

        [Test]
        public void DeleteAsset_MissingPathReportsAssetNotFound()
        {
            var result = AssetFunctions.DeleteAsset(_folder + "/NoSuchAsset.mat");

            StringAssert.Contains("ASSET_NOT_FOUND", result);
        }
    }
}
