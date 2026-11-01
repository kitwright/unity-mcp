// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    public sealed class HierarchyLookupTests
    {
        private readonly List<GameObject> _made = new List<GameObject>();
        private bool _wasDirty;

        [SetUp]
        public void RememberSceneState() => _wasDirty = SceneManager.GetActiveScene().isDirty;

        [TearDown]
        public void DestroyWhatTheTestMade()
        {
            foreach (var go in _made)
            {
                if (go != null)
                    UnityEngine.Object.DestroyImmediate(go);
            }
            _made.Clear();

            var scene = SceneManager.GetActiveScene();
            if (!_wasDirty && scene.IsValid())
                HierarchyFunctionsTests.ClearSceneDirtiness(scene);
        }

        private GameObject Make(string name, Transform parent = null, params Type[] components)
        {
            var go = new GameObject(name, components);
            if (parent != null)
                go.transform.SetParent(parent, false);
            _made.Add(go);
            return go;
        }

        private static string Unique(string stem) => stem + "_" + Guid.NewGuid().ToString("N");

#if KITWRIGHT_PHYSICS
        [Test]
        public void WriteProperties_ReflectionWriteSurvivesALaterSerializedWriteInTheSameCall()
        {
            var body = Make(Unique("KwBody"), null, typeof(Rigidbody)).GetComponent<Rigidbody>();

            var results = ComponentSerializer.WriteProperties(body,
                new JObject { ["mass"] = 4.5f, ["m_UseGravity"] = false });

            Assert.IsTrue(results.All(r => r.Success), string.Join("; ", results.Select(r => r.Error)));
            Assert.AreEqual(4.5f, body.mass, "mass goes through the reflection fallback; a later field must not undo it.");
            Assert.IsFalse(body.useGravity);
        }
#endif

        [Test]
        public void ByPath_TwoSiblingsShareAName_IsAmbiguousInsteadOfTakingTheFirst()
        {
            var root = Make(Unique("KwPathRoot"));
            var childName = Unique("KwDup");
            var first = Make(childName, root.transform);
            var second = Make(childName, root.transform);
            var path = root.name + "/" + childName;

            var ambiguous = Assert.Throws<AmbiguousTargetException>(
                () => ObjectsHelper.FindObject(path, ObjectsHelper.MethodByPath));
            Assert.AreEqual(2, ambiguous.MatchCount);
            Assert.AreEqual(2, ObjectsHelper.FindObjects(path, ObjectsHelper.MethodByPath).Count);

            Assert.AreEqual("AMBIGUOUS_TARGET", Code("delete_game_object", "target", path, "find_method", "by_path"));
            Assert.AreEqual("AMBIGUOUS_TARGET",
                Code("rename_game_object", "target", path, "find_method", "by_path", "new_name", "KwRenamed"));
            Assert.IsTrue(first != null && second != null, "Neither sibling may be deleted on an ambiguous by_path call.");
            Assert.AreEqual(childName, first.name);
            Assert.AreEqual(childName, second.name);
        }

        [TestCase("by_nmae")]
        [TestCase("BY_NAME")]
        public void UnknownFindMethod_IsInvalidParamListingTheValidOnes(string method)
        {
            var go = Make(Unique("KwMethod"));

            var thrown = Assert.Throws<ToolArgumentException>(() => ObjectsHelper.FindObjects(go.name, method));

            Assert.AreEqual("INVALID_PARAM", thrown.Code);
            Assert.AreEqual("find_method", thrown.ParamName);
            Assert.AreEqual(method, thrown.Provided);
            StringAssert.Contains("by_path", thrown.Expected);
            Assert.AreEqual(1, ObjectsHelper.FindObjects(go.name, ObjectsHelper.MethodByName).Count);
        }

        [Test]
        public void SetRectTransform_RejectsBadValuesAndUnknownAnchorWithoutApplyingAnything()
        {
            if (ToolRegistry.GetMethod("set_rect_transform") == null)
                Assert.Ignore("The uGUI tools are compiled out without the com.unity.ugui package.");

            var go = Make(Unique("KwRect"), null, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(5f, 5f);
            rect.anchoredPosition = new Vector2(1f, 2f);

            var cases = new[]
            {
                new[] { "anchored_position", "oops" },
                new[] { "size", "1,2,3" },
                new[] { "pivot", "NaN,0.5" },
                new[] { "anchor", "top-lefft" },
            };
            foreach (var bad in cases)
            {
                var answer = Refused("set_rect_transform",
                    "target", go.name, "anchored_position", "9,9", "size", "10,10", bad[0], bad[1]);

                Assert.AreEqual("INVALID_PARAM", (string)answer["code"], answer.ToString());
                Assert.AreEqual(bad[0], (string)answer["data"]["param"], answer.ToString());
                Assert.AreEqual(new Vector2(5f, 5f), rect.sizeDelta, $"{bad[0]} is invalid, so nothing may be applied.");
                Assert.AreEqual(new Vector2(1f, 2f), rect.anchoredPosition, $"{bad[0]} is invalid, so nothing may be applied.");
            }
        }

#if KITWRIGHT_PHYSICS
        [Test]
        public void PasteComponentValues_ReportsUnresolvedTokensAndFailsWhenNothingWasPasted()
        {
            var source = Make(Unique("KwSource"), null, typeof(BoxCollider));
            var withCollider = Make(Unique("KwHasCollider"), null, typeof(BoxCollider));
            var without = Make(Unique("KwNoCollider"));
            var missing = Unique("KwMissing");

            var copied = JObject.FromObject(ComponentBatchFunctions.CopyComponent(source.name, "BoxCollider"));
            Assert.IsTrue((bool)copied["success"], copied.ToString());

            var none = JObject.FromObject(ComponentBatchFunctions.PasteComponentValues(without.name, "BoxCollider"));
            Assert.IsFalse((bool)none["success"], none.ToString());
            Assert.AreEqual("PASTE_FAILED", (string)none["code"]);

            var partial = JObject.FromObject(
                ComponentBatchFunctions.PasteComponentValues($"{withCollider.name},,{missing}", "BoxCollider"));
            Assert.IsTrue((bool)partial["success"], partial.ToString());
            CollectionAssert.AreEquivalent(new[] { "<empty>", missing },
                partial["data"]["unresolved"].Select(t => (string)t).ToArray());
        }

        [Test]
        public void AddComponentToMany_ReportsTokensThatMatchedNothing()
        {
            var target = Make(Unique("KwAddTarget"));
            var missing = Unique("KwMissing");

            var added = JObject.FromObject(
                ComponentBatchFunctions.AddComponentToMany($"{target.name},{missing}", "Rigidbody"));

            Assert.IsTrue((bool)added["success"], added.ToString());
            Assert.IsNotNull(target.GetComponent<Rigidbody>());
            CollectionAssert.AreEqual(new[] { missing }, added["data"]["unresolved"].Select(t => (string)t).ToArray());
        }
#endif

        [Test]
        public void DuplicateGameObject_OfAChildStaysUnderTheSameParentRightAfterTheOriginal()
        {
            var parent = Make(Unique("KwDupParent"));
            var original = Make(Unique("KwOriginal"), parent.transform);
            var sibling = Make(Unique("KwSibling"), parent.transform);
            var copyName = Unique("KwCopy");

            var result = JObject.FromObject(GameObjectFunctions.DuplicateGameObject(
                ObjectIdCodec.GetSerializableId(original), copyName, ObjectsHelper.MethodById));
            Assert.IsTrue((bool)result["success"], result.ToString());

            var copy = GameObject.Find(copyName);
            Assert.IsNotNull(copy, "The duplicate is not in the scene.");
            _made.Add(copy);

            Assert.AreSame(parent.transform, copy.transform.parent);
            Assert.AreEqual(original.transform.GetSiblingIndex() + 1, copy.transform.GetSiblingIndex());
            Assert.AreEqual(2, sibling.transform.GetSiblingIndex());
        }

        [Test]
        public void GetHierarchy_AmbiguousRootNameAnswersAmbiguousTargetNotAToolException()
        {
            var name = Unique("KwHierarchyDup");
            Make(name);
            Make(name);

            var answer = new FunctionInvoker().Invoke(new FunctionCall
            {
                FunctionName = "get_hierarchy",
                Parameters = new Dictionary<string, string> { ["root_name"] = name }
            });

            StringAssert.Contains("\"code\":\"AMBIGUOUS_TARGET\"", answer);
            StringAssert.DoesNotContain("TOOL_EXCEPTION", answer);
        }

        [Test]
        public void ByPath_TrailingSlashEmptySegmentAndParentStepResolveLikeTransformFind()
        {
            var root = Make(Unique("KwPathRoot"));
            var b = Make("KwB", root.transform);
            var c = Make("KwC", root.transform);
            var path = root.name + "/KwB";

            Assert.AreSame(b, ObjectsHelper.FindObject(path + "/", ObjectsHelper.MethodByPath));
            Assert.AreSame(b, ObjectsHelper.FindObject(root.name + "//KwB", ObjectsHelper.MethodByPath));
            Assert.AreSame(c, ObjectsHelper.FindObject(path + "/../KwC", ObjectsHelper.MethodByPath));
            Assert.AreSame(c, ObjectsHelper.FindObject("../KwC", ObjectsHelper.MethodByPath, root: b));
            Assert.IsNull(ObjectsHelper.FindObject(path + "/../KwNothing", ObjectsHelper.MethodByPath));
        }

        [Test]
        public void ByPath_ParentStepStillReportsSameNamedSiblingsAsAmbiguous()
        {
            var root = Make(Unique("KwPathRoot"));
            var childName = Unique("KwDup");
            Make(childName, root.transform);
            Make(childName, root.transform);
            var path = root.name + "/" + childName + "/../" + childName;

            var ambiguous = Assert.Throws<AmbiguousTargetException>(
                () => ObjectsHelper.FindObject(path, ObjectsHelper.MethodByPath));
            Assert.AreEqual(2, ambiguous.MatchCount);
        }

        [Test]
        public void CreateGameObjectAndPrimitive_ValidateFindMethodEvenWithoutAParent()
        {
            var name = Unique("KwNeverMade");

            try
            {
                var plain = Refused("create_game_object", "name", name, "find_method", "by_nmae");
                var primitive = Refused("create_primitive", "name", name, "primitive_type", "Cube", "find_method", "by_nmae");

                Assert.AreEqual("INVALID_PARAM", (string)plain["code"], plain.ToString());
                Assert.AreEqual("INVALID_PARAM", (string)primitive["code"], primitive.ToString());
                Assert.IsNull(GameObject.Find(name));
            }
            finally
            {
                var leaked = GameObject.Find(name);
                if (leaked != null)
                    _made.Add(leaked);
            }
        }

        [Test]
        public void CreateUiTools_RefuseAnUnknownAnchorBeforeCreatingAnything()
        {
            if (ToolRegistry.GetMethod("create_button") == null)
                Assert.Ignore("The uGUI tools are compiled out without the com.unity.ugui package.");

            var parent = Make(Unique("KwUiParent"), null, typeof(Canvas));
            var name = Unique("KwNeverMade");
            var calls = new[]
            {
                new[] { "create_button", "text", "x" },
                new[] { "create_text", "text", "x" },
                new[] { "create_image" },
                new[] { "create_ui_element", "kind", "panel" },
            };

            foreach (var call in calls)
            {
                var args = call.Skip(1)
                    .Concat(new[] { "name", name, "parent_name", parent.name, "anchor", "top-lefft" })
                    .ToArray();

                var answer = Refused(call[0], args);

                Assert.AreEqual("INVALID_PARAM", (string)answer["code"], answer.ToString());
                Assert.AreEqual("anchor", (string)answer["data"]["param"], answer.ToString());
                Assert.AreEqual(0, parent.transform.childCount, $"{call[0]} created something before refusing.");
                Assert.IsNull(GameObject.Find(name), call[0]);
            }

            Ok("create_image", "name", name, "parent_name", parent.name, "anchor", "top-left");
            Assert.AreEqual(1, parent.transform.childCount);
        }

#if KITWRIGHT_URP
        [Test]
        public void SetVolumeOverrideProperty_RefusesANonFiniteColorComponent()
        {
            var host = Make(Unique("KwVolumeColor"));
            var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
            host.AddComponent<UnityEngine.Rendering.Volume>().sharedProfile = profile;
            try
            {
                Ok("add_volume_override", "target", host.name, "override_type", "Bloom");

                foreach (var bad in new[] { "NaN,Infinity,0", "1,0.5,-Infinity,1" })
                    Assert.AreEqual("VALUE_COERCION_FAILED", Code("set_volume_override_property",
                        "target", host.name, "override_type", "Bloom", "property", "tint", "value", bad), bad);

                Ok("set_volume_override_property",
                    "target", host.name, "override_type", "Bloom", "property", "tint", "value", "0.5,0.25,1,1");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }
#endif
    }
}
