// Copyright (C) KitWright. All rights reserved.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using static KitWright.Editor.Tests.ToolCall;
#if KITWRIGHT_URP
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#endif

namespace KitWright.Editor.Tests
{
    public sealed class CameraVolumeTagFixesTests
    {
        private readonly List<GameObject> _made = new List<GameObject>();
        private readonly List<string> _addedTags = new List<string>();
        private readonly List<string> _addedLayers = new List<string>();
        private string _tagManagerBefore;

        [SetUp]
        public void RememberTagManager() => _tagManagerBefore = TagManagerSnapshot();

        [TearDown]
        public void RestoreTagManagerAndScene()
        {
            foreach (var go in _made)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
            _made.Clear();

            foreach (var tag in _addedTags)
                Call("remove_tag", "tag", tag);
            _addedTags.Clear();
            foreach (var layer in _addedLayers)
                Call("remove_layer", "layer", layer);
            _addedLayers.Clear();

            Assert.AreEqual(_tagManagerBefore, TagManagerSnapshot(), "The test left TagManager changed.");
        }

        private static string Unique(string stem) => stem + "_" + System.Guid.NewGuid().ToString("N");

        private GameObject Make(string stem)
        {
            var go = new GameObject(Unique(stem));
            _made.Add(go);
            return go;
        }

        private Camera MakeCamera() => Make("KwCam").AddComponent<Camera>();

        private static string TagManagerSnapshot()
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            return string.Join("|", Strings(tagManager.FindProperty("tags"))) + "#" + string.Join("|", Strings(tagManager.FindProperty("layers")));
        }

        private static IEnumerable<string> Strings(SerializedProperty array)
        {
            for (var i = 0; i < array.arraySize; i++)
                yield return array.GetArrayElementAtIndex(i).stringValue;
        }

        private static string TagManagerFile() =>
            File.ReadAllText(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProjectSettings", "TagManager.asset")));

        [Test]
        public void CullingMask_RefusesNamesItCannotReadAndLeavesTheMaskAlone()
        {
            var camera = MakeCamera();
            var name = camera.gameObject.name;
            camera.cullingMask = (1 << 0) | (1 << 5);
            var before = camera.cullingMask;

            var typo = Refused("set_camera_culling_mask", "layers", "Water,KwNoSuchLayer", "game_object_name", name);
            Assert.AreEqual("INVALID_PARAM", (string)typo["code"]);
            StringAssert.Contains("KwNoSuchLayer", typo["data"]["invalid"].ToString());

            var trailingComma = Refused("set_camera_culling_mask", "layers", "Default,UI,", "game_object_name", name);
            StringAssert.Contains("<empty>", trailingComma["data"]["invalid"].ToString());

            Assert.AreEqual("INVALID_PARAM",
                Code("set_camera_culling_mask", "layers", "[\"Default\",\"UI\"]", "game_object_name", name));
            Assert.AreEqual("INVALID_PARAM",
                Code("set_camera_culling_mask", "layers", "Default", "game_object_name", name, "mode", "append"));
            Assert.AreEqual("INVALID_PARAM",
                Code("set_camera_culling_mask", "layers", "Default", "game_object_name", name, "mode", ""));

            Assert.AreEqual(before, camera.cullingMask, "A refused call must not touch the mask.");
        }

        [Test]
        public void CullingMask_AcceptsEverythingNothingAndPaddedNames()
        {
            var camera = MakeCamera();
            var name = camera.gameObject.name;

            Ok("set_camera_culling_mask", "layers", "Everything", "game_object_name", name);
            Assert.AreEqual(-1, camera.cullingMask);

            Ok("set_camera_culling_mask", "layers", "nothing", "game_object_name", name);
            Assert.AreEqual(0, camera.cullingMask);

            Ok("set_camera_culling_mask", "layers", " Default , UI ", "game_object_name", name);
            Assert.AreEqual((1 << 0) | (1 << 5), camera.cullingMask);

            Assert.IsFalse(CameraFunctions.TryParseLayerNames("", out _, out var invalid));
            CollectionAssert.AreEqual(new[] { "<empty>" }, invalid);
        }

        [Test]
        public void CullingMaskToString_NamesALayerThatHasNoNameInsteadOfDroppingIt()
        {
            var unnamed = Enumerable.Range(8, 24).FirstOrDefault(i => string.IsNullOrEmpty(LayerMask.LayerToName(i)));
            if (unnamed == 0)
                Assert.Ignore("Every user layer slot is named in this project.");

            Assert.AreEqual($"layer {unnamed}", CameraFunctions.CullingMaskToString(1 << unnamed));
            Assert.AreEqual("Nothing", CameraFunctions.CullingMaskToString(0));
        }

        [Test]
        public void SetCameraSettings_RefusesWhatItCannotApplyInsteadOfReportingAnOldValue()
        {
            var camera = MakeCamera();
            var name = camera.gameObject.name;
            var flags = camera.clearFlags;
            var color = camera.backgroundColor;

            Assert.AreEqual("INVALID_PARAM", Code("set_camera_settings", "game_object_name", name, "clear_flags", "garbage"));
            Assert.AreEqual("INVALID_PARAM", Code("set_camera_settings", "game_object_name", name, "clear_flags", "99"));
            Assert.AreEqual("INVALID_PARAM", Code("set_camera_settings", "game_object_name", name, "background_color", "   "));
            Assert.AreEqual("NO_PARAMS", Code("set_camera_settings", "game_object_name", name));
            Assert.AreEqual(flags, camera.clearFlags);
            Assert.AreEqual(color, camera.backgroundColor);

            Ok("set_camera_settings", "game_object_name", name, "clear_flags", "Color");
            Assert.AreEqual(CameraClearFlags.SolidColor, camera.clearFlags);
            Ok("set_camera_settings", "game_object_name", name, "clear_flags", "none");
            Assert.AreEqual(CameraClearFlags.Nothing, camera.clearFlags);
            Assert.AreEqual(CameraClearFlags.SolidColor, CameraFunctions.ParseClearFlags("solid"));
        }

        [Test]
        public void SetCameraSettings_RefusesClipPlanesThatCannotBeRight()
        {
            var camera = MakeCamera();
            var name = camera.gameObject.name;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 1000f;

            Assert.AreEqual("INVALID_PARAM", Code("set_camera_settings", "game_object_name", name, "near", "0"));
            Assert.AreEqual("INVALID_PARAM", Code("set_camera_settings", "game_object_name", name, "near", "-1"));
            Assert.AreEqual("INVALID_PARAM", Code("set_camera_settings", "game_object_name", name, "far", "-5"));
            Assert.AreEqual("INVALID_PARAM", Code("set_camera_settings", "game_object_name", name, "far", "0.1"));
            Assert.AreEqual("INVALID_PARAM", Code("set_camera_settings", "game_object_name", name, "near", "2000"));
            Assert.AreEqual("INVALID_PARAM", Code("set_camera_settings", "game_object_name", name, "near", "100", "far", "50"));
            Assert.AreEqual(0.3f, camera.nearClipPlane, 0.0001f);
            Assert.AreEqual(1000f, camera.farClipPlane, 0.0001f);

            var applied = Ok("set_camera_settings", "game_object_name", name, "near", "0.1", "far", "50");
            Assert.AreEqual(0.1f, camera.nearClipPlane, 0.0001f);
            Assert.AreEqual(50f, camera.farClipPlane, 0.0001f);
            StringAssert.Contains("near=0.1", (string)applied["message"]);

            camera.orthographic = true;
            Ok("set_camera_settings", "game_object_name", name, "near", "-5");
        }

        [Test]
        public void SetCameraProjection_RefusesASizeThatIsNotPositiveAndAProjectionThatIsOnlyCloseToAName()
        {
            var camera = MakeCamera();
            var name = camera.gameObject.name;

            Assert.AreEqual("INVALID_PARAM", Code("set_camera_projection", "projection", "orthographic", "size", "0", "game_object_name", name));
            Assert.AreEqual("INVALID_PARAM", Code("set_camera_projection", "projection", "orthographic", "size", "-3", "game_object_name", name));
            Assert.AreEqual("INVALID_PARAM", Code("set_camera_projection", "projection", "orthographic", "size", "200000", "game_object_name", name));
            Assert.AreEqual("INVALID_PARAM", Code("set_camera_projection", "projection", "orthogonal-foo", "game_object_name", name));
            Assert.AreEqual("INVALID_PARAM", Code("set_camera_projection", "projection", "perspective3D", "game_object_name", name));
            Assert.IsFalse(camera.orthographic, "A refused call must not flip the projection.");

            Ok("set_camera_projection", "projection", " Ortho ", "size", "4", "game_object_name", name);
            Assert.IsTrue(camera.orthographic);
            Assert.AreEqual(4f, camera.orthographicSize, 0.0001f);
            Ok("set_camera_projection", "projection", "persp", "game_object_name", name);
            Assert.IsFalse(camera.orthographic);
        }

        [Test]
        public void ACameraLookupSaysWhetherTheObjectIsMissingOrJustHasNoCamera()
        {
            var plain = Make("KwNotACamera");
            Assert.AreEqual("NO_CAMERA_ON_TARGET", Code("get_camera_properties", "game_object_name", plain.name));
            Assert.AreEqual("CAMERA_NOT_FOUND", Code("get_camera_properties", "game_object_name", Unique("KwNoSuchObject")));
        }

        [Test]
        public void TheDefaultCameraIsTheMainCameraOrTheOnlyOneAndNeverAnArbitraryPick()
        {
            var first = MakeCamera();
            var second = MakeCamera();

            Assert.AreSame(first, CameraFunctions.PickDefaultCamera(first, new[] { first, second }));
            Assert.AreSame(second, CameraFunctions.PickDefaultCamera(null, new[] { second }));
            Assert.IsNull(CameraFunctions.PickDefaultCamera(null, new[] { first, second }));
            Assert.IsNull(CameraFunctions.PickDefaultCamera(null, new Camera[0]));
        }

        [Test]
        public void AddTag_TrimsTheNameAndSavesTagManager()
        {
            var tag = Unique("KwTag");
            Ok("add_tag", "tag", "  " + tag + " ");
            _addedTags.Add(tag);

            CollectionAssert.Contains(InternalEditorUtility.tags, tag);
            CollectionAssert.DoesNotContain(InternalEditorUtility.tags, "  " + tag + " ");
            StringAssert.Contains(tag, TagManagerFile(), "add_tag has to flush TagManager, not only mark it dirty.");

            Ok("remove_tag", "tag", " " + tag);
            _addedTags.Remove(tag);
            StringAssert.DoesNotContain(tag, TagManagerFile(), "remove_tag has to flush TagManager too.");
        }

        [Test]
        public void RemoveTag_CallsABuiltInTagBuiltInInsteadOfMissing()
        {
            Assert.AreEqual("CANNOT_REMOVE_BUILTIN_TAG", Code("remove_tag", "tag", "Player"));
            Assert.AreEqual("CANNOT_REMOVE_BUILTIN_TAG", Code("remove_tag", "tag", "MainCamera"));
            Assert.AreEqual("TAG_NOT_FOUND", Code("remove_tag", "tag", Unique("KwNoSuchTag")));
        }

        [Test]
        public void AddAndRemoveLayer_TrimSaveAndWarnThatUsersOfTheSlotAreLeftAlone()
        {
            if (!Enumerable.Range(8, 24).Any(i => string.IsNullOrEmpty(LayerMask.LayerToName(i))))
                Assert.Ignore("No free user layer slot in this project.");

            var layer = Unique("KwLy");
            Ok("add_layer", "layer", " " + layer + " ");
            _addedLayers.Add(layer);

            Assert.GreaterOrEqual(LayerMask.NameToLayer(layer), 8);
            StringAssert.Contains(layer, TagManagerFile(), "add_layer has to flush TagManager.");

            var removed = Ok("remove_layer", "layer", layer);
            _addedLayers.Remove(layer);
            StringAssert.Contains("culling masks", (string)removed["hint"]);
            StringAssert.DoesNotContain(layer, TagManagerFile(), "remove_layer has to flush TagManager too.");
            Assert.AreEqual("LAYER_NOT_FOUND", Code("remove_layer", "layer", layer));
        }

        [Test]
        public void SetTagAndLayer_IsAnErrorWhenNothingWasApplied()
        {
            var go = Make("KwTagLayer");
            var layerBefore = go.layer;
            var tagBefore = go.tag;

            Assert.AreEqual("NO_PARAMS", Code("set_tag_and_layer", "target", go.name));
            Assert.AreEqual("NO_PARAMS", Code("set_tag_and_layer", "target", go.name, "tag", ""));

            var layerOnly = Refused("set_tag_and_layer", "target", go.name, "layer", "KwLayerNobodyDefined");
            Assert.AreEqual("INVALID_PARAM", (string)layerOnly["code"]);
            StringAssert.Contains("KwLayerNobodyDefined", layerOnly["data"]["warnings"].ToString());

            Refused("set_tag_and_layer", "target", go.name, "tag", "KwTagNobodyDefined");
            Assert.AreEqual(layerBefore, go.layer);
            Assert.AreEqual(tagBefore, go.tag);

            var half = Ok("set_tag_and_layer", "target", go.name, "tag", "EditorOnly", "layer", "KwLayerNobodyDefined");
            Assert.AreEqual("EditorOnly", go.tag);
            Assert.AreEqual(layerBefore, go.layer);
            StringAssert.Contains("KwLayerNobodyDefined", half["data"]["warnings"].ToString());
        }

#if KITWRIGHT_PHYSICS
        [Test]
        public void PhysicsQueries_RefuseVectorsThatOverflowAndRaysThatNormalizeToNothing()
        {
            Assert.AreEqual("INVALID_PARAM", Code("physics_raycast", "origin", "1e39,0,0", "direction", "0,-1,0"));
            Assert.AreEqual("INVALID_PARAM", Code("physics_raycast", "origin", "0,0,0", "direction", "1e39,0,0"));
            Assert.AreEqual("INVALID_PARAM", Code("physics_raycast", "origin", "0,0,0", "direction", "3e-6,0,0"));
            Assert.AreEqual("INVALID_PARAM", Code("physics_overlap", "center", "1e39,0,0"));
            Assert.AreEqual("INVALID_PARAM", Code("physics_overlap", "center", "0,0,0", "shape", "box", "size", "1e39,1,1"));
        }

        [Test]
        public void PhysicsQueries_RefuseAMaxHitsThatIsNotPositive()
        {
            Assert.AreEqual("INVALID_PARAM", Code("physics_raycast", "origin", "0,0,0", "direction", "0,-1,0", "max_hits", "0"));
            Assert.AreEqual("INVALID_PARAM", Code("physics_overlap", "center", "0,0,0", "max_hits", "-1"));

            Ok("physics_raycast", "origin", "0,100000,0", "direction", "0,-1,0", "max_distance", "1");
            Ok("physics_overlap", "center", "0,100000,0", "radius", "0.5");
        }
#endif

#if KITWRIGHT_PHYSICS2D
        [Test]
        public void Physics2DOverlapPoint_RefusesAPointThatOverflows()
        {
            Assert.AreEqual("INVALID_PARAM", Code("physics2d_overlap_point", "point", "1e39,0"));
        }
#endif
    }

#if KITWRIGHT_URP
    public sealed class VolumeFixesTests
    {
        private const string Subject = "KwVolumeFixSubject";

        private GameObject subject;
        private VolumeProfile profile;
        private Object[] selectionBefore;

        [SetUp]
        public void CreateVolume()
        {
            selectionBefore = Selection.objects;
            subject = new GameObject(Subject);
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            subject.AddComponent<Volume>().sharedProfile = profile;
        }

        [TearDown]
        public void DestroyVolume()
        {
            if (subject != null)
                Object.DestroyImmediate(subject);
            if (profile != null)
                Object.DestroyImmediate(profile);

            subject = null;
            profile = null;
            Selection.objects = selectionBefore;
        }

        private T Override<T>() where T : VolumeComponent => profile.components.OfType<T>().SingleOrDefault();

        [Test]
        public void CreateVolume_RefusesANameThatCannotBeAFileNameBeforeCreatingAnything()
        {
            var before = ObjectsHelper.FindObjectsByTypeUnsorted<Volume>(FindObjectsInactive.Include).Length;

            foreach (var name in new[] { "Sub/Vol", "../x", "back\\slash", ".hidden", " padded", "trailing." })
                Assert.AreEqual("INVALID_NAME", Code("create_volume", "name", name), name);

            Assert.AreEqual(before, ObjectsHelper.FindObjectsByTypeUnsorted<Volume>(FindObjectsInactive.Include).Length,
                "A refused name must not leave a Volume object behind.");
            Assert.IsFalse(AssetFunctions.IsValidAssetName(""));
        }

        [Test]
        public void CreateVolume_StillMakesTheObjectAndItsProfileAsset()
        {
            var name = "KwVol_" + System.Guid.NewGuid().ToString("N");
            var answer = Ok("create_volume", "name", name);
            var path = (string)answer["data"]["profilePath"];
            var go = GameObject.Find(name);

            try
            {
                Assert.AreEqual($"Assets/{name}.asset", path);
                Assert.IsNotNull(go);
                Assert.AreSame(AssetDatabase.LoadAssetAtPath<VolumeProfile>(path), go.GetComponent<Volume>().sharedProfile);
            }
            finally
            {
                if (go != null)
                    Object.DestroyImmediate(go);
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void AddVolumeOverride_CanLeaveEveryParameterUnticked()
        {
            var plain = Ok("add_volume_override", "target", Subject, "override_type", "Bloom", "override_all_parameters", "false");
            Assert.IsFalse((bool)plain["data"]["allParametersOverridden"]);
            Assert.IsFalse(Override<Bloom>().parameters.Any(p => p.overrideState),
                "override_all_parameters=false has to add the override the way the Inspector does: nothing ticked.");

            Ok("add_volume_override", "target", Subject, "override_type", "Vignette");
            Assert.IsTrue(Override<Vignette>().parameters.Any(p => p.overrideState),
                "The default has to stay what it was: every parameter ticked.");

            var info = (Newtonsoft.Json.Linq.JArray)Ok("get_volume_info", "target", Subject)["data"];
            Assert.AreEqual(0, (int)info.Single(o => (string)o["type"] == "Bloom")["overriddenParameters"]);
            var vignette = info.Single(o => (string)o["type"] == "Vignette");
            Assert.AreEqual((int)vignette["parameters"], (int)vignette["overriddenParameters"]);
        }

        [Test]
        public void RemoveVolumeOverride_UndoBringsBackAnOverrideThatIsStillAlive()
        {
            Ok("add_volume_override", "target", Subject, "override_type", "Bloom");

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Ok("remove_volume_override", "target", Subject, "override_type", "Bloom");
            Assert.AreEqual(0, profile.components.Count);

            Undo.RevertAllDownToGroup(group);

            Assert.AreEqual(1, profile.components.Count);
            Assert.IsTrue(profile.components[0] != null,
                "Undo restored the list but the override it points at had been destroyed outside Undo.");
        }

        [Test]
        public void TheVolumeToolsStepOverANullEntryInTheOverrideList()
        {
            profile.components.Add(null);

            Assert.AreEqual(0, ((Newtonsoft.Json.Linq.JArray)Ok("get_volume_info", "target", Subject)["data"]).Count);
            Ok("add_volume_override", "target", Subject, "override_type", "Bloom");
            Ok("set_volume_override_property", "target", Subject, "override_type", "Bloom", "property", "intensity", "value", "2");
            Ok("remove_volume_override", "target", Subject, "override_type", "Bloom");
        }

        [Test]
        public void SetVolumeOverrideProperty_ReportsWhatTheParameterStoredNotWhatWasAsked()
        {
            Ok("add_volume_override", "target", Subject, "override_type", "Vignette");

            var clamped = Ok("set_volume_override_property",
                "target", Subject, "override_type", "Vignette", "property", "intensity", "value", "5");
            Assert.AreEqual(1f, Override<Vignette>().intensity.value, 0.0001f);
            Assert.AreEqual("1", (string)clamped["data"]["value"]);
            Assert.IsTrue((bool)clamped["data"]["clamped"]);

            var exact = Ok("set_volume_override_property",
                "target", Subject, "override_type", "Vignette", "property", "intensity", "value", "0.25");
            Assert.AreEqual("0.25", (string)exact["data"]["value"]);
            Assert.IsFalse((bool)exact["data"]["clamped"]);
        }

        [Test]
        public void SetVolumeOverrideProperty_TakesADeclaredEnumNameAndNothingElse()
        {
            Ok("add_volume_override", "target", Subject, "override_type", "Tonemapping");

            Assert.AreEqual("VALUE_COERCION_FAILED", Code("set_volume_override_property",
                "target", Subject, "override_type", "Tonemapping", "property", "mode", "value", "99"));
            Assert.AreEqual("VALUE_COERCION_FAILED", Code("set_volume_override_property",
                "target", Subject, "override_type", "Tonemapping", "property", "mode", "value", "Neutral,ACES"));
            Assert.AreEqual(TonemappingMode.None, Override<Tonemapping>().mode.value);

            Ok("set_volume_override_property",
                "target", Subject, "override_type", "Tonemapping", "property", "mode", "value", "aces");
            Assert.AreEqual(TonemappingMode.ACES, Override<Tonemapping>().mode.value);

            Assert.IsFalse(VolumeFunctions.TryCoerce("99", typeof(TonemappingMode), out _));
            Assert.IsTrue(VolumeFunctions.TryCoerce(" neutral ", typeof(TonemappingMode), out var parsed));
            Assert.AreEqual(TonemappingMode.Neutral, parsed);
        }

        [Test]
        public void SetVolumeOverrideProperty_SaysWhenAParameterTypeIsNotSupported()
        {
            Ok("add_volume_override", "target", Subject, "override_type", "Vignette");

            Assert.AreEqual("PARAM_TYPE_UNSUPPORTED", Code("set_volume_override_property",
                "target", Subject, "override_type", "Vignette", "property", "center", "value", "0.5,0.5"));
        }

        [Test]
        public void SetVolumeOverrideProperty_SavesTheProfileItChanged()
        {
            const string folderName = "__KwVolumeFixTests";
            const string folder = "Assets/" + folderName;
            const string host = "KwSavedVolumeFix";

            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", folderName);

            var saved = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(saved, folder + "/KwProfile.asset");
            var go = new GameObject(host);
            go.AddComponent<Volume>().sharedProfile = saved;

            try
            {
                Ok("add_volume_override", "target", host, "override_type", "Bloom");
                Ok("set_volume_override_property", "target", host, "override_type", "Bloom", "property", "intensity", "value", "3");

                Assert.IsFalse(EditorUtility.IsDirty(saved),
                    "set_volume_override_property has to save the profile like add and remove do.");
            }
            finally
            {
                Object.DestroyImmediate(go);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void ResolveOverrideType_RefusesANameTwoTypesShareAndPrefersTheUrpOne()
        {
            var ambiguous = Assert.Throws<AmbiguousTypeException>(() => VolumeFunctions.ResolveOverrideType("KwDupOverride"));
            Assert.AreEqual(2, ambiguous.Candidates.Length);

            Assert.AreEqual(typeof(VolumeDupA.KwDupOverride), VolumeFunctions.ResolveOverrideType(typeof(VolumeDupA.KwDupOverride).FullName));

            Assert.AreEqual(typeof(UnityEngine.Rendering.Universal.KwDupUrpPreferred), VolumeFunctions.ResolveOverrideType("KwDupUrpPreferred"));
        }
    }

    namespace VolumeDupA
    {
        public sealed class KwDupOverride : VolumeComponent { }
    }

    namespace VolumeDupB
    {
        public sealed class KwDupOverride : VolumeComponent { }
        public sealed class KwDupUrpPreferred : VolumeComponent { }
    }
#endif
}

#if KITWRIGHT_URP
namespace UnityEngine.Rendering.Universal
{
    public sealed class KwDupUrpPreferred : VolumeComponent { }
}
#endif
