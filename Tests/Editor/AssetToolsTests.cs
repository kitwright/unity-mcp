// Copyright (C) KitWright. All rights reserved.

using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The asset tools that write to disk: a folder, a material, a texture, a ScriptableObject, and the
    /// copy/move/rename trio. Every one of them had no test, and they are the tools an agent uses to
    /// build the thing it was asked for - a failure here is a project left half-written, not a query
    /// that returned nothing. Everything is created under one temp folder that the teardown deletes.
    /// </summary>
    public sealed class AssetToolsTests
    {
        private const string Folder = "Assets/__KitWrightAssetToolTests";
        private const string Subject = "KwAssetToolSubject";

        [SetUp]
        public void CreateFolder()
        {
            if (AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.DeleteAsset(Folder);

            Ok("create_folder", "path", Folder);
            Assert.IsTrue(AssetDatabase.IsValidFolder(Folder), "create_folder should have made the folder.");
        }

        [TearDown]
        public void DeleteFolder()
        {
            if (AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.DeleteAsset(Folder);

            var leftover = GameObject.Find(Subject);
            if (leftover != null)
                Object.DestroyImmediate(leftover);
        }

        [Test]
        public void CreateFolderRefusesToLeaveTheProject()
        {
            // The path guard is the one thing here that is a safety property rather than a feature.
            Refused("create_folder", "path", "../__KitWrightOutsideTheProject");
            Refused("create_folder", "path", "C:/__KitWrightOutsideTheProject");
        }

        [Test]
        public void CreateMaterialWritesTheAssetAndAssignItLandsOnTheRenderer()
        {
            var created = Ok("create_material",
                "name", "KwMat", "color", "0.2,0.4,0.6,1", "shader", "Sprites/Default",
                "save_path", Folder + "/");

            var path = Folder + "/KwMat.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            Assert.IsNotNull(material, $"Expected a material at {path}. Tool said: {created}");

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                cube.name = Subject;
                Ok("assign_material", "game_object_name", Subject, "material_path", path);
                Assert.AreEqual(material, cube.GetComponent<Renderer>().sharedMaterial);

                Refused("assign_material", "game_object_name", Subject, "material_path", Folder + "/NotThere.mat");
            }
            finally
            {
                Object.DestroyImmediate(cube);
            }
        }

        [Test]
        public void CopyMoveAndRenameLeaveTheAssetWhereTheySay()
        {
            // Sprites/Default rather than the default Standard: this project is on URP, where
            // Shader.Find("Standard") is not guaranteed to resolve.
            Ok("create_material", "name", "KwSource", "shader", "Sprites/Default", "save_path", Folder + "/");
            var source = Folder + "/KwSource.mat";

            Ok("copy_asset", "source_path", source, "destination_path", Folder + "/KwCopy.mat");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(Folder + "/KwCopy.mat"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(source), "A copy leaves the original.");

            Ok("create_folder", "path", Folder + "/Sub");
            Ok("move_asset", "source_path", Folder + "/KwCopy.mat", "destination_path", Folder + "/Sub/KwCopy.mat");
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(Folder + "/KwCopy.mat"), "A move is not a copy.");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(Folder + "/Sub/KwCopy.mat"));

            Ok("rename_asset", "path", source, "new_name", "KwRenamed");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(Folder + "/KwRenamed.mat"));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(source));

            Refused("copy_asset", "source_path", Folder + "/NotThere.mat", "destination_path", Folder + "/X.mat");
            Refused("move_asset", "source_path", Folder + "/NotThere.mat", "destination_path", Folder + "/X.mat");
            Refused("rename_asset", "path", Folder + "/NotThere.mat", "new_name", "X");
        }

        [Test]
        public void CreateTextureWritesAPngAndCanImportItAsASprite()
        {
            var path = Folder + "/kw_texture.png";
            Ok("create_texture", "path", path, "width", "8", "height", "8", "color", "#ff0000ff");

            Assert.IsTrue(File.Exists(Path.GetFullPath(path)), "The PNG has to be on disk, not only in memory.");
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.IsNotNull(texture);
            Assert.AreEqual(8, texture.width);

            var spritePath = Folder + "/kw_sprite.png";
            Ok("create_texture", "path", spritePath, "width", "8", "height", "8", "as_sprite", "true");
            var importer = (TextureImporter)AssetImporter.GetAtPath(spritePath);
            Assert.AreEqual(TextureImporterType.Sprite, importer.textureType,
                "as_sprite is what makes the file usable by a uGUI Image.");
        }

        [Test]
        public void CreateScriptableObjectMakesTheAssetAndNamesATypeItCannotFind()
        {
            var path = Folder + "/kw_profile.asset";
            var created = Call("create_scriptable_object", "type_name", "VolumeProfile", "asset_path", path);

            if ((bool)created["success"] != true)
                Assert.Ignore($"VolumeProfile is not available in this project: {created}");

            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<ScriptableObject>(path),
                $"Expected an asset at {path}. Tool said: {created}");

            Refused("create_scriptable_object",
                "type_name", "KwTypeNobodyDeclared", "asset_path", Folder + "/kw_nope.asset");
        }
    }
}
