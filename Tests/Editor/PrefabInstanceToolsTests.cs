// Copyright (C) KitWright. All rights reserved.

using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The prefab instance tools: putting one in the scene, pushing its overrides back into the asset,
    /// throwing them away, and cutting the link entirely. All four are about the connection between
    /// instance and asset, so each test reads both sides.
    /// </summary>
    public sealed class PrefabInstanceToolsTests
    {
        private const string FolderName = "__KitWrightPrefabInstanceTests";
        private const string Folder = "Assets/" + FolderName;
        // SaveAsPrefabAsset renames the asset's root to the file name, and the instance is named after
        // the asset - so the file has to be named for what the instance is looked up as.
        private const string Root = "KwPrefabRoot";
        private const string PrefabPath = Folder + "/" + Root + ".prefab";

        [SetUp]
        public void CreatePrefabAsset()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", FolderName);

            var root = new GameObject(Root);
            new GameObject("KwPrefabChild").transform.SetParent(root.transform, false);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
        }

        [TearDown]
        public void RemoveInstancesAndAsset()
        {
            // A loop, not a single Find: a test that instantiated more than once leaves more than one.
            for (var leftover = GameObject.Find(Root); leftover != null; leftover = GameObject.Find(Root))
                Object.DestroyImmediate(leftover);

            AssetDatabase.DeleteAsset(Folder);
        }

        private static GameObject Asset() => AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);

        [Test]
        public void InstantiatePlacesAConnectedInstanceWhereItWasAsked()
        {
            Ok("instantiate_prefab", "prefab_path", PrefabPath, "position", "1,2,3");

            var instance = GameObject.Find(Root);
            Assert.IsNotNull(instance, "The instance should be in the scene under the prefab's root name.");
            Assert.AreEqual(new Vector3(1f, 2f, 3f), instance.transform.position);
            Assert.IsTrue(PrefabUtility.IsPartOfAnyPrefab(instance), "The instance must stay linked to the asset.");
            Assert.IsNotNull(instance.transform.Find("KwPrefabChild"));

            Assert.AreEqual("PREFAB_NOT_FOUND",
                Code("instantiate_prefab", "prefab_path", Folder + "/NoSuch.prefab"));

            // A position that does not parse used to be checked after the instantiate, leaving the
            // object in the scene on top of the refusal.
            Assert.AreEqual("INVALID_PARAM",
                Code("instantiate_prefab", "prefab_path", PrefabPath, "position", "over there"));
            Object.DestroyImmediate(instance);
            Assert.IsNull(GameObject.Find(Root), "A refused instantiate must not leave an instance behind.");
        }

        [Test]
        public void ApplyPushesAnOverrideIntoTheAssetAndRevertThrowsTheNextOneAway()
        {
            Ok("instantiate_prefab", "prefab_path", PrefabPath);
            var instance = GameObject.Find(Root);

            instance.transform.localScale = new Vector3(2f, 2f, 2f);
            Ok("apply_prefab_overrides", "game_object_name", Root);
            Assert.AreEqual(new Vector3(2f, 2f, 2f), Asset().transform.localScale,
                "apply_prefab_overrides should have written the instance's scale into the asset.");

            instance.transform.localScale = new Vector3(3f, 3f, 3f);
            Ok("revert_prefab_overrides", "game_object_name", Root);
            Assert.AreEqual(new Vector3(2f, 2f, 2f), instance.transform.localScale,
                "revert should restore the asset's scale, which is the applied one.");

            Assert.AreEqual("GAME_OBJECT_NOT_FOUND",
                Code("apply_prefab_overrides", "game_object_name", "KwNothingCalledThis"));
        }

        [Test]
        public void UnpackCutsTheLinkSoTheOverrideToolsHaveNothingToTalkTo()
        {
            Ok("instantiate_prefab", "prefab_path", PrefabPath);

            Ok("unpack_prefab", "game_object_name", Root, "mode", "completely");
            Assert.IsFalse(PrefabUtility.IsPartOfAnyPrefab(GameObject.Find(Root)),
                "An unpacked instance is a plain GameObject.");

            Assert.AreEqual("NOT_PREFAB_INSTANCE", Code("unpack_prefab", "game_object_name", Root));
            Assert.AreEqual("NOT_PREFAB_INSTANCE", Code("apply_prefab_overrides", "game_object_name", Root));
            Assert.AreEqual("NOT_PREFAB_INSTANCE", Code("revert_prefab_overrides", "game_object_name", Root));
        }
    }
}
