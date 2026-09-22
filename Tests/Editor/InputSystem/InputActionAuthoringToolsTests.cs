// Copyright (C) KitWright. All rights reserved.

#if KITWRIGHT_INPUTSYSTEM
using System.IO;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The .inputactions writers, against a real asset on disk. Every one of them reloads the file,
    /// edits an in-memory copy and writes it back, so the only honest check is to read the file again
    /// after the call - which is what Reload does here.
    /// </summary>
    public sealed class InputActionAuthoringToolsTests
    {
        private const string FolderName = "__KitWrightInputToolTests";
        private const string Folder = "Assets/" + FolderName;
        private const string AssetPath = Folder + "/KwControls.inputactions";

        private InputActionAsset reloaded;

        [SetUp]
        public void CreateFolder()
        {
            // The importer has opinions about a freshly written .inputactions; the assertions read the
            // asset back rather than the console.
            LogAssert.ignoreFailingMessages = true;

            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", FolderName);
        }

        [TearDown]
        public void DeleteFolder()
        {
            LogAssert.ignoreFailingMessages = false;

            if (reloaded != null)
                Object.DestroyImmediate(reloaded);
            reloaded = null;

            AssetDatabase.DeleteAsset(Folder);
        }

        // This assembly references the input package and our input tools, not the tool plumbing, so
        // the answers are read off the anonymous response object instead of through FunctionInvoker.
        private static bool Succeeded(object result) =>
            result.GetType().GetProperty("success")?.GetValue(result) is bool ok && ok;

        private static string Code(object result) =>
            result.GetType().GetProperty("code")?.GetValue(result) as string;

        private InputActionAsset Reload(string path = AssetPath)
        {
            if (reloaded != null)
                Object.DestroyImmediate(reloaded);

            reloaded = ScriptableObject.CreateInstance<InputActionAsset>();
            reloaded.LoadFromJson(File.ReadAllText(path));
            return reloaded;
        }

        private static void Create(string firstMap = null) =>
            Assert.IsTrue(Succeeded(InputActionAssetFunctions.CreateInputActions(AssetPath, firstMap)),
                "The fixture asset could not be created.");

        [Test]
        public void CreateInputActionsWritesTheAssetOnceAndSeedsItsFirstMap()
        {
            Create("Player");

            Assert.IsTrue(File.Exists(AssetPath));
            Assert.IsNotNull(Reload().FindActionMap("Player"), "first_map should be in the written file.");

            Assert.AreEqual("ALREADY_EXISTS", Code(InputActionAssetFunctions.CreateInputActions(AssetPath)));
            Assert.AreEqual("INVALID_PATH",
                Code(InputActionAssetFunctions.CreateInputActions(Folder + "/KwWrongExtension.asset")));
        }

        // Omitting first_map used to throw out of the input package's own serializer, which counts the
        // maps with LINQ over an array that is still null before the first one is added.
        [Test]
        public void CreateInputActionsWithNoFirstMapWritesAnEmptyAssetInsteadOfThrowing()
        {
            var empty = Folder + "/KwEmpty.inputactions";

            Assert.IsTrue(Succeeded(InputActionAssetFunctions.CreateInputActions(empty)));
            Assert.IsTrue(File.Exists(empty));
            Assert.AreEqual(0, Reload(empty).actionMaps.Count);

            // And the empty document it wrote is one the other tools can still pick up.
            Assert.IsTrue(Succeeded(InputActionAssetFunctions.AddInputMap(empty, "Player")));
            Assert.AreEqual(1, Reload(empty).actionMaps.Count);
        }

        [Test]
        public void MapsAndActionsAreEachAddedOnceAndKnowTheirType()
        {
            Create();

            Assert.IsTrue(Succeeded(InputActionAssetFunctions.AddInputMap(AssetPath, "Player")));
            Assert.AreEqual("MAP_EXISTS", Code(InputActionAssetFunctions.AddInputMap(AssetPath, "Player")));

            Assert.IsTrue(Succeeded(InputActionAssetFunctions.AddInputAction(AssetPath, "Player", "Jump")));
            Assert.IsTrue(Succeeded(InputActionAssetFunctions.AddInputAction(AssetPath, "Player", "Move", "value")));

            var map = Reload().FindActionMap("Player");
            Assert.AreEqual(InputActionType.Button, map.FindAction("Jump").type);
            Assert.AreEqual(InputActionType.Value, map.FindAction("Move").type);

            Assert.AreEqual("ACTION_EXISTS", Code(InputActionAssetFunctions.AddInputAction(AssetPath, "Player", "Jump")));
            Assert.AreEqual("MAP_NOT_FOUND", Code(InputActionAssetFunctions.AddInputAction(AssetPath, "Ghost", "Jump")));
            Assert.AreEqual("ASSET_NOT_FOUND",
                Code(InputActionAssetFunctions.AddInputMap(Folder + "/NoSuch.inputactions", "Player")));
        }

        [Test]
        public void ASimpleBindingAndACompositeBothEndUpOnTheirAction()
        {
            Create("Player");
            InputActionAssetFunctions.AddInputAction(AssetPath, "Player", "Jump");
            InputActionAssetFunctions.AddInputAction(AssetPath, "Player", "Move", "value");

            Assert.IsTrue(Succeeded(
                InputActionAssetFunctions.AddInputBinding(AssetPath, "Player", "Jump", "<Keyboard>/space")));

            var jump = Reload().FindActionMap("Player").FindAction("Jump");
            Assert.AreEqual(1, jump.bindings.Count);
            Assert.AreEqual("<Keyboard>/space", jump.bindings[0].path);

            Assert.IsTrue(Succeeded(InputActionAssetFunctions.AddInputCompositeBinding(
                AssetPath, "Player", "Move", "2DVector",
                "Up=<Keyboard>/w;Down=<Keyboard>/s;Left=<Keyboard>/a;Right=<Keyboard>/d")));

            var move = Reload().FindActionMap("Player").FindAction("Move");
            Assert.AreEqual(5, move.bindings.Count, "A composite is one binding for the head plus one per part.");
            Assert.IsTrue(move.bindings[0].isComposite);

            Assert.AreEqual("INVALID_COMPOSITE_PARTS", Code(InputActionAssetFunctions.AddInputCompositeBinding(
                AssetPath, "Player", "Move", "2DVector", "garbage")));
            Assert.AreEqual("ACTION_NOT_FOUND",
                Code(InputActionAssetFunctions.AddInputBinding(AssetPath, "Player", "Ghost", "<Keyboard>/a")));
        }
    }
}
#endif
