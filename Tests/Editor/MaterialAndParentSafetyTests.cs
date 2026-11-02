// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using static KitWright.Editor.Tests.ToolCall;
using Object = UnityEngine.Object;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// set_material_property used to edit and save a package's shared material and a forced type that
    /// disagreed with the shader, and to store Infinity for 1e40; set_parent answered success for a
    /// reparent Unity refuses. Each test names a call that must now refuse or say what it did, and checks
    /// that nothing changed.
    /// </summary>
    public sealed class MaterialAndParentSafetyTests
    {
        private const string FolderName = "__KitWrightMaterialAndParentSafetyTests";
        private const string Folder = "Assets/" + FolderName;

        private readonly List<GameObject> made = new List<GameObject>();

        [SetUp]
        public void CreateFolder()
        {
            if (AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.CreateFolder("Assets", FolderName);
        }

        [TearDown]
        public void DestroyEverythingCreated()
        {
            foreach (var go in made)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }

            made.Clear();
            AssetDatabase.DeleteAsset(Folder);
        }

        private static string Unique(string stem) => "Kw" + stem + "_" + Guid.NewGuid().ToString("N");

        private static string Id(GameObject go) => ObjectIdCodec.GetSerializableId(go);

        private static string Id(Transform transform) => ObjectIdCodec.GetSerializableId(transform);

        private GameObject Make(GameObject go)
        {
            made.Add(go);
            return go;
        }

        private static string NewMaterial(string stem)
        {
            var path = $"{Folder}/{Unique(stem)}.mat";
            var shader = Shader.Find("Sprites/Default");
            Assert.IsNotNull(shader, "A test shader is required.");
            AssetDatabase.CreateAsset(new Material(shader), path);
            AssetDatabase.SaveAssets();
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(path), "Setup failed: " + path);
            return path;
        }

        private GameObject NewPrefabInstance(out GameObject child)
        {
            var root = new GameObject(Unique("PrefabRoot"));
            new GameObject(Unique("PrefabChild")).transform.SetParent(root.transform);
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{Folder}/{root.name}.prefab");
            Object.DestroyImmediate(root);

            var instance = Make((GameObject)PrefabUtility.InstantiatePrefab(prefab));
            child = instance.transform.GetChild(0).gameObject;
            return instance;
        }

        [TestCase("Assets/Materials/Foo.mat", true, true)]
        [TestCase("Packages/com.unity.render-pipelines.universal/Runtime/Materials/Lit.mat", true, false)]
        [TestCase("Resources/unity_builtin_extra", true, false)]
        [TestCase("Library/unity default resources", true, false)]
        [TestCase("", true, false)]
        [TestCase(null, true, false)]
        [TestCase("", false, true)]
        public void IsEditableMaterial_AcceptsOnlyProjectAssetsAndSceneEmbeddedMaterials(string assetPath, bool persistent, bool expected)
        {
            Assert.AreEqual(expected, MaterialFunctions.IsEditableMaterial(assetPath, persistent));
        }

        [Test]
        public void IsEditableMaterial_RefusesAReadOnlyMaterialAndLetsAWritablePackageThrough()
        {
            Assert.IsFalse(MaterialFunctions.IsEditableMaterial("Assets/Models/Hero.fbx", true, notEditable: true));
            Assert.IsFalse(MaterialFunctions.IsEditableMaterial("Packages/com.unity.x/Mat.mat", true, writablePackage: false));
            Assert.IsTrue(MaterialFunctions.IsEditableMaterial("Packages/com.local.x/Mat.mat", true, writablePackage: true));
            Assert.IsFalse(MaterialFunctions.IsEditableMaterial("Library/unity default resources", true, writablePackage: true));
        }

        [Test]
        public void SetMaterialProperty_RefusesTheSharedDefaultMaterialOfASceneObject()
        {
            var cube = Make(GameObject.CreatePrimitive(PrimitiveType.Cube));
            cube.name = Unique("Cube");
            var shared = cube.GetComponent<Renderer>().sharedMaterial;
            if (AssetDatabase.GetAssetPath(shared).StartsWith("Assets/", StringComparison.Ordinal))
                Assert.Ignore("This project's default material is a project asset.");

            // A property no shader declares: if the refusal is missing the call cannot edit the shared material.
            var answer = Refused("set_material_property", "path", cube.name, "name", "_KwNoSuchProperty", "value", "1");

            Assert.AreEqual("MATERIAL_READ_ONLY", (string)answer["code"]);
        }

        [Test]
        public void SetMaterialProperty_OnAProjectMaterialNamesTheAssetAndSaysEveryRendererChanges()
        {
            var path = NewMaterial("Edit");

            var answer = Ok("set_material_property", "path", path, "name", "_Color", "value", "1,0,0,1");

            Assert.AreEqual(path, (string)answer["data"]["assetPath"]);
            StringAssert.Contains("every renderer", (string)answer["data"]["note"]);
            Assert.AreEqual(Color.red, AssetDatabase.LoadAssetAtPath<Material>(path).GetColor("_Color"));
        }

        [Test]
        public void SetMaterialProperty_RefusesAForcedTypeThePropertyDoesNotHave()
        {
            var path = NewMaterial("Mismatch");
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var before = material.GetColor("_Color");

            var answer = Refused("set_material_property", "path", path, "name", "_Color", "value", "1", "type", "float");

            Assert.AreEqual("INVALID_PARAM", (string)answer["code"]);
            StringAssert.Contains("Color", answer.ToString());
            Assert.AreEqual(before, material.GetColor("_Color"));
        }

        [TestCase("1e40")]
        [TestCase("-1e40")]
        [TestCase("3.5e38")]
        public void SetMaterialProperty_RefusesAFloatThatDoesNotFitAFloat(string value)
        {
            var path = NewMaterial("Huge");
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!material.HasProperty("PixelSnap"))
                Assert.Ignore("The test shader has no float property.");
            var before = material.GetFloat("PixelSnap");

            var answer = Refused("set_material_property", "path", path, "name", "PixelSnap", "value", value);

            Assert.AreEqual("INVALID_PARAM", (string)answer["code"]);
            Assert.AreEqual(before, material.GetFloat("PixelSnap"));
        }

        [Test]
        public void SetMaterialProperty_AcceptsAForcedTypeThePropertyHas()
        {
            var path = NewMaterial("Match");

            Ok("set_material_property", "path", path, "name", "_Color", "value", "0,1,0,1", "type", "color");

            Assert.AreEqual(Color.green, AssetDatabase.LoadAssetAtPath<Material>(path).GetColor("_Color"));
        }

        [Test]
        public void SetParent_ReportsAReparentUnityRefusedInsteadOfSayingItWorked()
        {
            var root = new GameObject(Unique("AssetRoot"));
            new GameObject(Unique("AssetChild")).transform.SetParent(root.transform);
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{Folder}/{root.name}.prefab");
            Object.DestroyImmediate(root);
            var assetChild = prefab.transform.GetChild(0).gameObject;
            var other = Make(new GameObject(Unique("Other")));

            LogAssert.ignoreFailingMessages = true;
            try
            {
                var answer = Refused("set_parent", "child", Id(assetChild), "parent", Id(other));

                Assert.AreEqual("REPARENT_FAILED", (string)answer["code"]);
                Assert.AreSame(prefab.transform, assetChild.transform.parent);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        public void SetComponentProperties_ReportsATransformParentUnityRefused()
        {
            var instance = NewPrefabInstance(out var child);
            var other = Make(new GameObject(Unique("Other")));
            var properties = "{\"parent\":{\"fileID\":\"" + Id(other.transform) + "\"}}";

            LogAssert.ignoreFailingMessages = true;
            try
            {
                var answer = Call("set_component_properties", "target", Id(child), "component", "Transform", "properties", properties);

                StringAssert.Contains("set_parent", answer.ToString());
                Assert.AreSame(instance.transform, child.transform.parent);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        public void SetParent_RefusesToMoveAChildOfAPrefabInstance()
        {
            var instance = NewPrefabInstance(out var child);
            var other = Make(new GameObject(Unique("Other")));

            var parented = Refused("set_parent", "child", Id(child), "parent", Id(other));
            var unparented = Refused("set_parent", "child", Id(child));

            Assert.AreEqual("PREFAB_INSTANCE_CHILD", (string)parented["code"]);
            Assert.AreEqual("PREFAB_INSTANCE_CHILD", (string)unparented["code"]);
            Assert.AreSame(instance.transform, child.transform.parent);
        }

        [Test]
        public void SetParent_StillMovesTheRootOfAPrefabInstance()
        {
            var instance = NewPrefabInstance(out _);
            var other = Make(new GameObject(Unique("Other")));

            Ok("set_parent", "child", Id(instance), "parent", Id(other));
            Assert.AreSame(other.transform, instance.transform.parent);

            Ok("set_parent", "child", Id(instance));
            Assert.IsNull(instance.transform.parent);
        }

        [Test]
        public void SetParent_SaysWhenTheObjectIsAlreadyARoot()
        {
            var go = Make(new GameObject(Unique("Root")));

            var answer = Ok("set_parent", "child", Id(go));

            StringAssert.Contains("already a root", (string)answer["message"]);
        }
    }
}
