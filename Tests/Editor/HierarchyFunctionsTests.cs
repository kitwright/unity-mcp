// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KitWright.Editor.Tests
{
    public sealed class HierarchyFunctionsTests
    {
        [Test]
        public void FindTarget_ResolvesInactiveObjectsByNamePathAndInstanceId()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            GameObject firstRoot = null;
            GameObject secondRoot = null;
            GameObject inactiveByName = null;

            try
            {
                firstRoot = new GameObject("FirstRoot_" + suffix);
                secondRoot = new GameObject("SecondRoot_" + suffix);
                var duplicateName = "Duplicate_" + suffix;
                var firstChild = new GameObject(duplicateName);
                var secondChild = new GameObject(duplicateName);
                inactiveByName = new GameObject("Inactive_" + suffix);

                firstChild.transform.SetParent(firstRoot.transform);
                secondChild.transform.SetParent(secondRoot.transform);
                firstChild.SetActive(false);
                inactiveByName.SetActive(false);

                Assert.AreSame(inactiveByName, ObjectsHelper.FindTarget(inactiveByName.name));
                Assert.AreSame(firstChild, ObjectsHelper.FindTarget(firstRoot.name + "/" + duplicateName));
                Assert.AreSame(secondChild, ObjectsHelper.FindTarget(secondRoot.name + "/" + duplicateName));
                Assert.AreSame(firstChild, ObjectsHelper.FindTarget(ObjectIdCodec.GetSerializableId(firstChild)));
            }
            finally
            {
                if (firstRoot != null) UnityEngine.Object.DestroyImmediate(firstRoot);
                if (secondRoot != null) UnityEngine.Object.DestroyImmediate(secondRoot);
                if (inactiveByName != null) UnityEngine.Object.DestroyImmediate(inactiveByName);
                if (!wasDirty && scene.IsValid())
                    ClearSceneDirtiness(scene);
            }
        }

        [Test]
        public void HierarchyAndSceneInfo_IncludeLoadedAdditiveScenes()
        {
            var originalSetup = EditorSceneManager.GetSceneManagerSetup();
            bool canRestoreOriginalSetup = CanRestoreSceneSetup(originalSetup);
            if (!Application.isBatchMode && !canRestoreOriginalSetup)
                Assert.Ignore("Skipping additive-scene test because the interactive editor has unsaved untitled scenes.");
            if (!Application.isBatchMode && AnyLoadedSceneIsDirty())
                Assert.Ignore("Skipping additive-scene test: replacing a modified scene opens Unity's save prompt, " +
                              "and that modal blocks the editor loop the MCP server pumps on.");

            Scene additiveScene = default;

            string suffix = Guid.NewGuid().ToString("N");
            string tempFolder = "Assets/__KitWrightMcpSceneHierarchyTests";
            string activeScenePath = tempFolder + "/Active_" + suffix + ".unity";
            string additiveScenePath = tempFolder + "/Additive_" + suffix + ".unity";
            string activeRootName = "KitWrightActiveRoot_" + suffix;
            string additiveRootName = "KitWrightAdditiveRoot_" + suffix;
            string inactiveRootName = "KitWrightInactiveAdditiveRoot_" + suffix;

            SkipIfAnySceneDirty();

            try
            {
                EnsureFolder(tempFolder);

                var activeScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Assert.IsTrue(EditorSceneManager.SaveScene(activeScene, activeScenePath));
                new GameObject(activeRootName);

                additiveScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Assert.IsTrue(additiveScene.IsValid());
                Assert.IsTrue(EditorSceneManager.SaveScene(additiveScene, additiveScenePath));
                var additiveRoot = new GameObject(additiveRootName);
                SceneManager.MoveGameObjectToScene(additiveRoot, additiveScene);
                var inactiveRoot = new GameObject(inactiveRootName);
                SceneManager.MoveGameObjectToScene(inactiveRoot, additiveScene);
                inactiveRoot.SetActive(false);

                Assert.IsTrue(SceneManager.SetActiveScene(activeScene));

                var hierarchy = HierarchyFunctions.GetHierarchy(
                    depth: 1,
                    include_components: false,
                    include_inactive: true);

                Assert.That(hierarchy, Does.Contain("Scene: " + activeScene.name));
                Assert.That(hierarchy, Does.Contain(activeRootName));
                Assert.That(hierarchy, Does.Contain("Scene: " + additiveScene.name + " (additive)"));
                Assert.That(hierarchy, Does.Contain(additiveRootName));
                Assert.That(hierarchy, Does.Match(inactiveRootName + @" #\S+ \[INACTIVE\]"));

                var rootLookup = HierarchyFunctions.GetHierarchy(
                    root_name: inactiveRootName,
                    depth: 1,
                    include_components: false,
                    include_inactive: true);

                Assert.That(rootLookup, Does.Match(inactiveRootName + @" #\S+ \[INACTIVE\]"));
                Assert.That(rootLookup, Does.Not.Contain("GAME_OBJECT_NOT_FOUND"));

                var sceneInfo = SceneFunctions.GetSceneInfo();
                Assert.That(sceneInfo, Does.Contain("Scene: " + activeScene.name + " (active)"));
                Assert.That(sceneInfo, Does.Contain(activeRootName));
                Assert.That(sceneInfo, Does.Contain("Scene: " + additiveScene.name + " (additive)"));
                Assert.That(sceneInfo, Does.Contain(additiveRootName));
                Assert.That(sceneInfo, Does.Contain(inactiveRootName));
            }
            finally
            {
                SettleDirtyScenes(tempFolder);

                if (canRestoreOriginalSetup)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(originalSetup);
                }
                else if (Application.isBatchMode)
                {
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                }

                if (AssetDatabase.IsValidFolder(tempFolder))
                    AssetDatabase.DeleteAsset(tempFolder);
            }
        }

        //  Edge cases: GetHierarchy boundary conditions

        [Test]
        public void GetHierarchy_DepthZero_ClampedToOneStillReturnsHierarchy()
        {
            // depth=0 gets clamped to 1 by Mathf.Clamp(depth, 1, 10)
            var result = HierarchyFunctions.GetHierarchy(depth: 0);

            Assert.IsNotNull(result);
            Assert.IsNotEmpty(result);
            // Should contain Scene header since it's a full hierarchy call
            Assert.That(result, Does.Contain("Scene:"));
        }

        [Test]
        public void GetHierarchy_NegativeDepth_ClampedToOneStillReturnsHierarchy()
        {
            // depth=-1 gets clamped to 1 by Mathf.Clamp(depth, 1, 10)
            var result = HierarchyFunctions.GetHierarchy(depth: -1);

            Assert.IsNotNull(result);
            Assert.IsNotEmpty(result);
            Assert.That(result, Does.Contain("Scene:"));
        }

        [Test]
        public void GetHierarchy_DepthExceedsMax_ClampedToTen()
        {
            // depth=100 gets clamped to 10 by Mathf.Clamp(depth, 1, 10)
            var result = HierarchyFunctions.GetHierarchy(depth: 100);

            Assert.IsNotNull(result);
            Assert.IsNotEmpty(result);
            Assert.That(result, Does.Contain("Scene:"));
        }

        [Test]
        public void GetHierarchy_NonexistentRootName_ReturnsGameObjectNotFoundError()
        {
            var result = HierarchyFunctions.GetHierarchy(
                root_name: "NonExistent_Object_" + Guid.NewGuid().ToString("N"));

            StringAssert.Contains("GAME_OBJECT_NOT_FOUND", result);
            StringAssert.Contains("\"success\":false", result);
        }

        [Test]
        public void NotFound_SuggestsNearMissesAndFlagsStaleInstanceIds()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            GameObject target = null;

            try
            {
                target = new GameObject("SuggestProbe_" + suffix);

                // Wrong case only: name resolution is case-sensitive, so this reaches the suggester.
                var wrongCase = HierarchyFunctions.GetHierarchy(root_name: "suggestprobe_" + suffix);
                StringAssert.Contains("GAME_OBJECT_NOT_FOUND", wrongCase);
                StringAssert.Contains(target.name, wrongCase);
                StringAssert.Contains("case-sensitive", wrongCase);

                // One transposed character still lands inside the edit-distance budget.
                var typo = ObjectsHelper.SuggestTargets("SuggestPorbe_" + suffix);
                CollectionAssert.Contains(typo, target.name);

                // A stale instance id cannot be name-matched, so it gets its own explanation.
                var staleId = HierarchyFunctions.GetHierarchy(root_name: "-999999");
                StringAssert.Contains("GAME_OBJECT_NOT_FOUND", staleId);
                StringAssert.Contains("domain reload", staleId);
                Assert.IsEmpty(ObjectsHelper.SuggestTargets("-999999"),
                    "A numeric query must not be name-matched against the scene.");

                Assert.AreEqual(0, ObjectsHelper.EditDistance("abc", "abc"));
                Assert.AreEqual(1, ObjectsHelper.EditDistance("abc", "abd"));
                Assert.AreEqual(2, ObjectsHelper.EditDistance("abc", "acb"));
                Assert.AreEqual(3, ObjectsHelper.EditDistance("", "abc"));
            }
            finally
            {
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                if (!wasDirty && scene.IsValid())
                    ClearSceneDirtiness(scene);
            }
        }

        [Test]
        public void ResolveById_ReportsTheResolvedIdentityAndRefusesOutOfRangeIds()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            GameObject target = null;

            try
            {
                target = new GameObject("IdOccupant_" + suffix);
                var collider = target.AddComponent<BoxCollider>();
                var goId = ObjectIdCodec.GetSerializableId(target);
                var componentId = ObjectIdCodec.GetSerializableId(collider);

                // A resolve that succeeds names what it resolved, so acting on the wrong object is
                // visible in the response rather than silent.
                Assert.AreSame(target, ObjectsHelper.FindObject(goId, ObjectsHelper.MethodById));
                Assert.That(HierarchyFunctions.GetHierarchy(root_name: goId, depth: 1, include_components: false),
                    Does.Contain(target.name));

                // The id is live but is not the GameObject asked for: report the current occupant
                // instead of a bare not-found, which is what makes a reassigned id diagnosable.
                var byComponentId = HierarchyFunctions.GetHierarchy(root_name: componentId);
                StringAssert.Contains("GAME_OBJECT_NOT_FOUND", byComponentId);
                StringAssert.Contains("BoxCollider", byComponentId);
                StringAssert.Contains(target.name, byComponentId);

#if !UNITY_6000_3_OR_NEWER
                // Only the int-based id path can truncate: a 64-bit id (YAML fileID, or one cached
                // from another Unity version) must not narrow onto a live object. EntityId is 64-bit,
                // so on 6000.3+ this value is a legitimately unrelated id and asserting it resolves
                // to nothing would only be asserting that Unity has not handed it out yet.
                var truncating = ((long)target.GetInstanceID() + 4294967296L)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                Assert.IsNull(ObjectIdCodec.ToObject(truncating),
                    "An out-of-range id must not resolve to whatever its low 32 bits point at.");
                Assert.IsNull(ObjectsHelper.FindObject(truncating, ObjectsHelper.MethodById));
#endif
            }
            finally
            {
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                if (!wasDirty && scene.IsValid())
                    ClearSceneDirtiness(scene);
            }
        }

        [Test]
        public void GetHierarchy_ExcludeInactive_DoesNotShowInactiveObjects()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            GameObject activeObj = null;
            GameObject inactiveObj = null;

            try
            {
                activeObj = new GameObject("ActiveTest_" + suffix);
                inactiveObj = new GameObject("InactiveTest_" + suffix);
                inactiveObj.SetActive(false);

                var result = HierarchyFunctions.GetHierarchy(
                    depth: 1,
                    include_components: false,
                    include_inactive: false);

                Assert.That(result, Does.Contain("ActiveTest_" + suffix));
                Assert.That(result, Does.Not.Contain("InactiveTest_" + suffix));
            }
            finally
            {
                if (activeObj != null) UnityEngine.Object.DestroyImmediate(activeObj);
                if (inactiveObj != null) UnityEngine.Object.DestroyImmediate(inactiveObj);
                if (!wasDirty && scene.IsValid())
                    ClearSceneDirtiness(scene);
            }
        }

        [Test]
        public void GetHierarchy_IncludeInactive_ShowsInactiveObjectsWithMarker()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            GameObject inactiveObj = null;

            try
            {
                inactiveObj = new GameObject("InactiveMarkerTest_" + suffix);
                inactiveObj.SetActive(false);

                var result = HierarchyFunctions.GetHierarchy(
                    depth: 1,
                    include_components: false,
                    include_inactive: true);

                Assert.That(result, Does.Match("InactiveMarkerTest_" + suffix + @" #\S+ \[INACTIVE\]"));
            }
            finally
            {
                if (inactiveObj != null) UnityEngine.Object.DestroyImmediate(inactiveObj);
                if (!wasDirty && scene.IsValid())
                    ClearSceneDirtiness(scene);
            }
        }

        [Test]
        public void GetHierarchy_IncludeComponents_ShowsComponentNames()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            GameObject testObj = null;

            try
            {
                testObj = new GameObject("ComponentTest_" + suffix);
                testObj.AddComponent<BoxCollider>();

                var withComponents = HierarchyFunctions.GetHierarchy(
                    root_name: "ComponentTest_" + suffix,
                    depth: 1,
                    include_components: true);

                var withoutComponents = HierarchyFunctions.GetHierarchy(
                    root_name: "ComponentTest_" + suffix,
                    depth: 1,
                    include_components: false);

                Assert.That(withComponents, Does.Contain("BoxCollider"));
                Assert.That(withoutComponents, Does.Not.Contain("BoxCollider"));
            }
            finally
            {
                if (testObj != null) UnityEngine.Object.DestroyImmediate(testObj);
                if (!wasDirty && scene.IsValid())
                    ClearSceneDirtiness(scene);
            }
        }

        [Test]
        public void GetHierarchy_PrintsInstanceIdThatResolvesBackToTheObject()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            GameObject testObj = null;

            try
            {
                testObj = new GameObject("IdTest_" + suffix);
                var expectedId = ObjectIdCodec.GetSerializableId(testObj);

                var withIds = HierarchyFunctions.GetHierarchy(
                    root_name: testObj.name,
                    depth: 1,
                    include_components: false);

                Assert.That(withIds, Does.Contain(testObj.name + " #" + expectedId));

                Assert.AreSame(testObj, ObjectsHelper.FindTarget(expectedId));

                var withoutIds = HierarchyFunctions.GetHierarchy(
                    root_name: testObj.name,
                    depth: 1,
                    include_components: false,
                    include_ids: false);

                Assert.That(withoutIds, Does.Contain(testObj.name));
                Assert.That(withoutIds, Does.Not.Contain("#" + expectedId));
            }
            finally
            {
                if (testObj != null) UnityEngine.Object.DestroyImmediate(testObj);
                if (!wasDirty && scene.IsValid())
                    ClearSceneDirtiness(scene);
            }
        }

        [Test]
        public void GetHierarchy_RootNameByHierarchyPath_ReturnsSubtreeOnly()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            GameObject parent = null;

            try
            {
                parent = new GameObject("PathParent_" + suffix);
                var child = new GameObject("PathChild_" + suffix);
                child.transform.SetParent(parent.transform);

                var result = HierarchyFunctions.GetHierarchy(
                    root_name: "PathParent_" + suffix + "/PathChild_" + suffix,
                    depth: 1,
                    include_components: false);

                Assert.That(result, Does.Contain("PathChild_" + suffix));
                Assert.That(result, Does.Not.Contain("GAME_OBJECT_NOT_FOUND"));
            }
            finally
            {
                if (parent != null) UnityEngine.Object.DestroyImmediate(parent);
                if (!wasDirty && scene.IsValid())
                    ClearSceneDirtiness(scene);
            }
        }

        [Test]
        public void GetHierarchy_MaxNodes_StopsAtBudgetAndSaysSo()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            GameObject parent = null;

            try
            {
                parent = new GameObject("CapParent_" + suffix);
                for (int i = 0; i < 2; i++)
                    new GameObject("CapChild" + i + "_" + suffix).transform.SetParent(parent.transform);

                // Budget of 2 pays for the parent and the first child only.
                var capped = HierarchyFunctions.GetHierarchy(
                    root_name: parent.name, depth: 2, include_components: false, max_nodes: 2);

                Assert.That(capped, Does.Contain("CapChild0_" + suffix));
                Assert.That(capped, Does.Not.Contain("CapChild1_" + suffix));
                Assert.That(capped, Does.Contain("truncated at max_nodes=2"));

                // A budget that exactly fits must not report a truncation.
                var exact = HierarchyFunctions.GetHierarchy(
                    root_name: parent.name, depth: 2, include_components: false, max_nodes: 3);

                Assert.That(exact, Does.Contain("CapChild1_" + suffix));
                Assert.That(exact, Does.Not.Contain("truncated at max_nodes"));
            }
            finally
            {
                if (parent != null) UnityEngine.Object.DestroyImmediate(parent);
                if (!wasDirty && scene.IsValid())
                    ClearSceneDirtiness(scene);
            }
        }

        [Test]
        public void SingleTargetResolve_TwoObjectsShareAName_ErrorsInsteadOfPickingOne()
        {
            var name = "__KitWrightAmbiguous_" + Guid.NewGuid().ToString("N");
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            GameObject first = null;
            GameObject second = null;

            try
            {
                first = new GameObject(name);
                second = new GameObject(name);

                Assert.AreEqual(2, ObjectsHelper.FindObjects(name, findAll: true).Count,
                    "Both objects must be in the search pool for the ambiguity to be real.");

                var ambiguous = Assert.Throws<AmbiguousTargetException>(
                    () => ObjectsHelper.FindObject(name));
                Assert.AreEqual(2, ambiguous.Candidates.Count,
                    "The error must name every candidate so the caller can re-target by id.");

                var deleted = new FunctionInvoker().Invoke(new FunctionCall
                {
                    FunctionName = "delete_game_object",
                    Parameters = new Dictionary<string, string> { ["target"] = name }
                });

                StringAssert.Contains("\"code\":\"AMBIGUOUS_TARGET\"", deleted);
                Assert.IsFalse(first == null, "Neither match may be destroyed on an ambiguous delete.");
                Assert.IsFalse(second == null, "Neither match may be destroyed on an ambiguous delete.");
            }
            finally
            {
                if (first != null) UnityEngine.Object.DestroyImmediate(first);
                if (second != null) UnityEngine.Object.DestroyImmediate(second);
                if (!wasDirty && scene.IsValid())
                    ClearSceneDirtiness(scene);
            }
        }

        [Test]
        public void FindGameObjects_MaxCap_ReportsThePreCapTotalAndTheShownCount()
        {
            var name = "__KitWrightCapped_" + Guid.NewGuid().ToString("N");
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            GameObject first = null;
            GameObject second = null;

            try
            {
                first = new GameObject(name);
                second = new GameObject(name);

                var capped = GameObjectFunctions.FindGameObjects(name, max: "1").ToString();

                StringAssert.Contains("Found 2 object(s), showing 1", capped);
            }
            finally
            {
                if (first != null) UnityEngine.Object.DestroyImmediate(first);
                if (second != null) UnityEngine.Object.DestroyImmediate(second);
                if (!wasDirty && scene.IsValid())
                    ClearSceneDirtiness(scene);
            }
        }

        // while the run is in flight, and swapping a dirty scene opens Unity's save prompt.
        internal static void SkipIfAnySceneDirty()
        {
            if (!Application.isBatchMode && AnyLoadedSceneIsDirty())
                Assert.Ignore("Skipping: replacing a modified scene opens Unity's save prompt, " +
                              "and that modal blocks the editor loop the MCP server pumps on.");
        }

        internal static bool AnyLoadedSceneIsDirty()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty)
                    return true;
            }
            return false;
        }

        // Restoring over a dirty scene opens Unity's save prompt, which blocks the whole run.
        // Only the scenes these tests created may be written: SkipIfAnySceneDirty proved every
        // other loaded scene was clean before the swap, so anything dirty on one now came from
        // the run itself, and saving it would commit test residue into the user's own project.
        internal static void SettleDirtyScenes(string ownedPathPrefix)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isDirty || string.IsNullOrEmpty(scene.path))
                    continue;

                if (scene.path.StartsWith(ownedPathPrefix, StringComparison.Ordinal))
                    EditorSceneManager.SaveScene(scene);
                else
                    ClearSceneDirtiness(scene);
            }
        }

        internal static void ClearSceneDirtiness(Scene scene)
        {
            var method = typeof(EditorSceneManager).GetMethod(
                "ClearSceneDirtiness",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            method?.Invoke(null, new object[] { scene });
        }

        private static bool CanRestoreSceneSetup(SceneSetup[] setup)
        {
            foreach (var scene in setup)
            {
                if (string.IsNullOrEmpty(scene.path) || !File.Exists(scene.path))
                    return false;
            }

            return setup.Length > 0;
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
