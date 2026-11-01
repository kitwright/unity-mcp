// Copyright (C) KitWright. All rights reserved.

#if KITWRIGHT_INPUTSYSTEM
using System.IO;
using System.Text;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The .inputactions tools take a path and read or write it. Without a project check, any file
    /// on disk could be read through get_input_actions_info and rewritten by the others.
    /// </summary>
    public sealed class InputActionPathSafetyTests
    {
        private const string FolderName = "__KitWrightInputPathSafetyTests";
        private const string Folder = "Assets/" + FolderName;
        private const string EmptyDocument = "{\n    \"name\": \"x\",\n    \"maps\": [],\n    \"controlSchemes\": []\n}";

        private string outsideDirectory;

        [SetUp]
        public void CreateFolders()
        {
            LogAssert.ignoreFailingMessages = true;

            if (AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.CreateFolder("Assets", FolderName);

            outsideDirectory = Path.Combine(Path.GetTempPath(), "kw_input_safety_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outsideDirectory);
        }

        [TearDown]
        public void DeleteFolders()
        {
            LogAssert.ignoreFailingMessages = false;
            AssetDatabase.DeleteAsset(Folder);

            if (Directory.Exists(outsideDirectory))
                Directory.Delete(outsideDirectory, true);
        }

        private static string Code(object result) =>
            result.GetType().GetProperty("code")?.GetValue(result) as string;

        private string OutsideFile()
        {
            var path = Path.Combine(outsideDirectory, "Outside.inputactions");
            File.WriteAllText(path, EmptyDocument);
            return path;
        }

        [Test]
        public void EveryToolRefusesAFileOutsideAssets_AndLeavesItAlone()
        {
            var outside = OutsideFile();

            Assert.AreEqual("INVALID_PATH", Code(InputActionAssetFunctions.GetInputActionsInfo(outside)));
            Assert.AreEqual("INVALID_PATH", Code(InputActionAssetFunctions.AddInputMap(outside, "Player")));
            Assert.AreEqual("INVALID_PATH", Code(InputActionAssetFunctions.AddInputAction(outside, "Player", "Jump")));
            Assert.AreEqual("INVALID_PATH", Code(InputActionAssetFunctions.AddInputBinding(outside, "Player", "Jump", "<Keyboard>/space")));
            Assert.AreEqual("INVALID_PATH", Code(InputActionAssetFunctions.AddInputCompositeBinding(
                outside, "Player", "Move", "2DVector", "Up=<Keyboard>/w")));

            Assert.AreEqual(EmptyDocument, File.ReadAllText(outside));
        }

        [Test]
        public void CreateInputActionsRefusesToCreateOutsideAssets()
        {
            var outside = Path.Combine(outsideDirectory, "Created.inputactions");

            Assert.AreEqual("INVALID_PATH", Code(InputActionAssetFunctions.CreateInputActions(outside)));
            Assert.IsFalse(File.Exists(outside));

            // Resolves to the project root, one level above Assets.
            var escape = "Assets/../__KwInputEscape.inputactions";
            var escapeOnDisk = Path.Combine(Path.GetDirectoryName(UnityEngine.Application.dataPath), "__KwInputEscape.inputactions");
            try
            {
                Assert.AreEqual("INVALID_PATH", Code(InputActionAssetFunctions.CreateInputActions(escape)));
                Assert.IsFalse(File.Exists(escapeOnDisk));
            }
            finally
            {
                if (File.Exists(escapeOnDisk))
                    File.Delete(escapeOnDisk);
            }
        }

        [Test]
        public void AWriteKeepsTheByteOrderMarkTheFileAlreadyHad()
        {
            var path = Folder + "/KwBom.inputactions";
            Assert.IsNull(Code(InputActionAssetFunctions.CreateInputActions(path)), "Setup failed.");

            var onDisk = Path.Combine(Path.GetDirectoryName(UnityEngine.Application.dataPath), path);
            File.WriteAllText(onDisk, File.ReadAllText(onDisk), new UTF8Encoding(true));

            Assert.IsNull(Code(InputActionAssetFunctions.AddInputMap(path, "Player")));

            var head = new byte[3];
            using (var stream = File.OpenRead(onDisk))
                stream.Read(head, 0, 3);
            CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, head,
                "A plain File.WriteAllText rewrite dropped the BOM; AtomicFile keeps it.");
        }
    }
}
#endif
