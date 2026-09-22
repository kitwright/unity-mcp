// Copyright (C) KitWright. All rights reserved.

using System;
using System.IO;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KitWright.Editor.Tests
{
    public sealed class PrefabFunctionsTests
    {
        [Test]
        public void PrefabStageTools_SaveDiscardAndProtectDirtyStage()
        {
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                Assert.Ignore("Skipping prefab-stage tool test because a Prefab Stage is already open in the interactive editor.");

            var suffix = Guid.NewGuid().ToString("N");
            var tempFolder = "Assets/__KitWrightMcpPrefabStageTests";
            var prefabPath = tempFolder + "/Primary_" + suffix + ".prefab";
            var otherPrefabPath = tempFolder + "/Other_" + suffix + ".prefab";
            var savedChildName = "SavedChild_" + suffix;
            var discardedChildName = "DiscardedChild_" + suffix;

            try
            {
                EnsureFolder(tempFolder);
                CreatePrefabAsset(prefabPath, "PrimaryRoot_" + suffix);
                CreatePrefabAsset(otherPrefabPath, "OtherRoot_" + suffix);

                Assert.That(Json(PrefabFunctions.OpenPrefabStage(tempFolder + "/Missing.prefab")), Does.Contain("PREFAB_NOT_FOUND"));

                Assert.That(Json(PrefabFunctions.OpenPrefabStage(prefabPath)), Does.Contain("Prefab stage opened"));
                AddChildToCurrentStage(savedChildName);

                Assert.That(Json(PrefabFunctions.OpenPrefabStage(prefabPath)), Does.Contain("Prefab stage already open"));
                Assert.That(CurrentStageRoot().transform.Find(savedChildName), Is.Not.Null);

                Assert.That(Json(PrefabFunctions.OpenPrefabStage(otherPrefabPath)), Does.Contain("ANOTHER_STAGE_DIRTY"));

                Assert.That(Json(PrefabFunctions.SavePrefabStage()), Does.Contain("Prefab stage saved"));
                Assert.That(Json(PrefabFunctions.ClosePrefabStage(save: true)), Does.Contain("Prefab stage closed"));

                var savedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Assert.That(savedPrefab.transform.Find(savedChildName), Is.Not.Null);

                Assert.That(Json(PrefabFunctions.OpenPrefabStage(prefabPath)), Does.Contain("Prefab stage opened"));
                AddChildToCurrentStage(discardedChildName);
                Assert.That(Json(PrefabFunctions.ClosePrefabStage(save: false)), Does.Contain("edits discarded"));

                savedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Assert.That(savedPrefab.transform.Find(savedChildName), Is.Not.Null);
                Assert.That(savedPrefab.transform.Find(discardedChildName), Is.Null);
                Assert.That(Json(PrefabFunctions.SavePrefabStage()), Does.Contain("NO_PREFAB_STAGE_OPEN"));
            }
            finally
            {
                CloseAnyPrefabStageDiscardingChanges();
                if (AssetDatabase.IsValidFolder(tempFolder))
                    AssetDatabase.DeleteAsset(tempFolder);
            }
        }

        [Test]
        public void CreatePrefabVariant_ProducesVariantOfBase()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var tempFolder = "Assets/__KitWrightMcpPrefabVariantTests";
            var basePath = tempFolder + "/Base_" + suffix + ".prefab";
            var variantPath = tempFolder + "/Variant_" + suffix + ".prefab";

            try
            {
                EnsureFolder(tempFolder);
                CreatePrefabAsset(basePath, "BaseRoot_" + suffix);

                var result = PrefabFunctions.CreatePrefabVariant(basePath, variantPath);
                Assert.That(Json(result), Does.Contain("Created prefab variant"));

                var variant = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
                Assert.That(variant, Is.Not.Null);
                Assert.AreEqual(PrefabAssetType.Variant, PrefabUtility.GetPrefabAssetType(variant));
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(tempFolder))
                    AssetDatabase.DeleteAsset(tempFolder);
            }
        }

        [Test]
        public void CreatePrefabVariant_InvalidPathRejected()
        {
            Assert.That(Json(PrefabFunctions.CreatePrefabVariant(null, "Assets/x.prefab")), Does.Contain("INVALID_ARGUMENT"));
        }

        [Test]
        public void CreatePrefabVariant_MissingBaseRejected()
        {
            Assert.That(
                Json(PrefabFunctions.CreatePrefabVariant("Assets/__nope__/Missing.prefab", "Assets/__nope__/V.prefab")),
                Does.Contain("PREFAB_NOT_FOUND"));
        }

        [Test]
        public void GetPrefabVariantInfo_ReportsVariant()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var tempFolder = "Assets/__KitWrightMcpPrefabVariantInfoTests";
            var basePath = tempFolder + "/Base_" + suffix + ".prefab";
            var variantPath = tempFolder + "/Variant_" + suffix + ".prefab";

            try
            {
                EnsureFolder(tempFolder);
                CreatePrefabAsset(basePath, "BaseRoot_" + suffix);
                PrefabFunctions.CreatePrefabVariant(basePath, variantPath);

                var info = PrefabFunctions.GetPrefabVariantInfo(variantPath);
                var success = info.GetType().GetProperty("success")?.GetValue(info);
                Assert.AreEqual(true, success);
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(tempFolder))
                    AssetDatabase.DeleteAsset(tempFolder);
            }
        }

        private static string Json(object result) => JsonConvert.SerializeObject(result);

        private static void CreatePrefabAsset(string path, string rootName)
        {
            var root = new GameObject(rootName);
            try
            {
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                Assert.That(prefab, Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static GameObject CurrentStageRoot()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            Assert.That(stage, Is.Not.Null);
            return stage.prefabContentsRoot;
        }

        private static void AddChildToCurrentStage(string childName)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            Assert.That(stage, Is.Not.Null);

            var child = new GameObject(childName);
            SceneManager.MoveGameObjectToScene(child, stage.scene);
            child.transform.SetParent(stage.prefabContentsRoot.transform, false);
            EditorSceneManager.MarkSceneDirty(stage.scene);
        }

        private static void CloseAnyPrefabStageDiscardingChanges()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return;

            stage.ClearDirtiness();
            StageUtility.GoToMainStage();
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            var name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent))
                throw new InvalidOperationException("Temporary test folder must be under Assets.");

            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);

            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
