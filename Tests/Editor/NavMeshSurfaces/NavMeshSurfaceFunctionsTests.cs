// Copyright (C) KitWright. Licensed under MIT.

#if KITWRIGHT_AI_NAVIGATION && KITWRIGHT_AI
using System.Collections;
using System.IO;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    public sealed class NavMeshSurfaceFunctionsTests
    {
        private const string Folder = "Assets/__KwNavTests";
        private const string ScenePath = Folder + "/Nav.unity";

        [SetUp]
        public void SetUp()
        {
            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.Refresh();
        }

        private static JObject Json(object result) => JObject.FromObject(result);

        [Test]
        public void BakeNavMeshSurfaces_RefusesAnEmptyScene()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var task = NavMeshSurfaceFunctions.BakeNavMeshSurfaces();

            Assert.AreEqual("NO_NAVMESH_SURFACES", Json(task.Result).Value<string>("code"));
        }

        [Test]
        public void BakeNavmeshSurfaces_IsTheCliSpellingOfTheSameTool()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var task = UnityCliNavMeshSurfaceFunctions.BakeNavmeshSurfaces();

            Assert.AreEqual("NO_NAVMESH_SURFACES", Json(task.Result).Value<string>("code"));
        }

        [Test]
        public void BakeNavMeshSurfaces_RefusesAnUnsavedSceneBeforeBaking()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Surface").AddComponent<NavMeshSurface>();

            var task = NavMeshSurfaceFunctions.BakeNavMeshSurfaces();

            Assert.AreEqual("SCENE_NOT_SAVED", Json(task.Result).Value<string>("code"));
        }

        [UnityTest]
        public IEnumerator BakeNavMeshSurfaces_SavesTheNavMeshDataAsAnAssetNextToTheScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.isStatic = true;
            var surface = new GameObject("Surface").AddComponent<NavMeshSurface>();
            Assert.IsTrue(EditorSceneManager.SaveScene(scene, ScenePath));

            var task = NavMeshSurfaceFunctions.BakeNavMeshSurfaces(true, 120);
            while (!task.IsCompleted)
                yield return null;

            var result = Json(task.Result);
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsNotNull(surface.navMeshData);
            Assert.IsNotEmpty(AssetDatabase.GetAssetPath(surface.navMeshData));
            Assert.AreEqual(1, ((JArray)result["data"]["baked"]).Count);
        }
    }
}
#endif
