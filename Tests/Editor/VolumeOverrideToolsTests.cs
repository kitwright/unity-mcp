// Copyright (C) KitWright. All rights reserved.

#if KITWRIGHT_URP
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The Volume override writers. The profile is created in memory rather than through
    /// create_volume, which would leave a .asset behind in Assets/ for every test that ran.
    /// </summary>
    public sealed class VolumeOverrideToolsTests
    {
        private const string Subject = "KwVolumeSubject";

        private GameObject subject;
        private VolumeProfile profile;

        [SetUp]
        public void CreateVolume()
        {
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
        }

        private T Override<T>() where T : VolumeComponent => profile.components.OfType<T>().SingleOrDefault();

        [Test]
        public void AnOverrideOnASavedProfileIsStoredInsideTheProfileAsset()
        {
            const string folderName = "__KwVolumeAssetTests";
            const string folder = "Assets/" + folderName;
            const string path = folder + "/KwProfile.asset";
            const string host = "KwSavedVolume";

            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", folderName);

            var saved = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(saved, path);
            var go = new GameObject(host);
            go.AddComponent<Volume>().sharedProfile = saved;

            try
            {
                Ok("add_volume_override", "target", host, "override_type", "Bloom");

                // In the file, not only in the list. A VolumeComponent no asset owns serializes as a
                // null reference, so the override was gone after the next reload while the call had
                // already reported success.
                var bloom = saved.components.OfType<Bloom>().Single();
                Assert.IsTrue(AssetDatabase.Contains(bloom), "The override has to live inside the profile asset.");
                Assert.Contains(bloom, AssetDatabase.LoadAllAssetsAtPath(path));

                Ok("remove_volume_override", "target", host, "override_type", "Bloom");

                Assert.AreEqual(1, AssetDatabase.LoadAllAssetsAtPath(path).Length,
                    "Removing has to take the sub-asset with it rather than orphan it in the file.");
            }
            finally
            {
                Object.DestroyImmediate(go);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void UndoTakesBackAnOverrideValue()
        {
            Ok("add_volume_override", "target", Subject, "override_type", "Bloom");
            var bloom = Override<Bloom>();
            bloom.intensity.value = 1f;

            // Its own group, so the revert below cannot reach into the test runner's.
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();

            Ok("set_volume_override_property", "target", Subject,
                "override_type", "Bloom", "property", "intensity", "value", "7");
            Assert.AreEqual(7f, bloom.intensity.value, 0.0001f);

            Undo.RevertAllDownToGroup(group);

            // The value lives on the override, which is a ScriptableObject of its own: recording the
            // profile gave Undo the component list and nothing of the value to put back.
            Assert.AreEqual(1f, bloom.intensity.value, 0.0001f);
        }

        [Test]
        public void AnOverrideIsAddedOnceAndTakenBackOffAgain()
        {
            Ok("add_volume_override", "target", Subject, "override_type", "bloom");

            var bloom = Override<Bloom>();
            Assert.IsNotNull(bloom, "add_volume_override should have put a Bloom on the profile.");
            Assert.IsTrue(bloom.active);

            Assert.AreEqual("OVERRIDE_ALREADY_ADDED",
                Code("add_volume_override", "target", Subject, "override_type", "Bloom"));
            Assert.AreEqual(1, profile.components.Count, "The refused call must not add a second Bloom.");
            Assert.AreEqual("OVERRIDE_TYPE_NOT_FOUND",
                Code("add_volume_override", "target", Subject, "override_type", "NotAnEffect_ZZZ"));

            Ok("remove_volume_override", "target", Subject, "override_type", "Bloom");
            Assert.IsNull(Override<Bloom>());
            Assert.AreEqual("OVERRIDE_NOT_PRESENT",
                Code("remove_volume_override", "target", Subject, "override_type", "Bloom"));
        }

        [Test]
        public void SettingAPropertyAlsoFlipsItsOverrideStateOrTheEffectStaysAtTheDefault()
        {
            Ok("add_volume_override", "target", Subject, "override_type", "Bloom");
            Ok("set_volume_override_property",
                "target", Subject, "override_type", "Bloom", "property", "intensity", "value", "2.5");

            var bloom = Override<Bloom>();
            Assert.AreEqual(2.5f, bloom.intensity.value, 0.001f);
            Assert.IsTrue(bloom.intensity.overrideState,
                "A value written without overrideState is ignored by the volume system.");

            Assert.AreEqual("PROPERTY_NOT_FOUND", Code("set_volume_override_property",
                "target", Subject, "override_type", "Bloom", "property", "wobble", "value", "1"));
            Assert.AreEqual("VALUE_COERCION_FAILED", Code("set_volume_override_property",
                "target", Subject, "override_type", "Bloom", "property", "intensity", "value", "loud"));
            Assert.AreEqual(2.5f, bloom.intensity.value, 0.001f, "A refused write must leave the old value.");

            Assert.AreEqual("OVERRIDE_NOT_PRESENT", Code("set_volume_override_property",
                "target", Subject, "override_type", "Vignette", "property", "intensity", "value", "1"));
        }

        [Test]
        public void AVolumeWithNoProfileAndAnObjectWithNoVolumeBothSayWhichIsMissing()
        {
            var bare = new GameObject("KwVolumeBare");
            try
            {
                Assert.AreEqual("NO_VOLUME",
                    Code("add_volume_override", "target", bare.name, "override_type", "Bloom"));

                bare.AddComponent<Volume>();
                Assert.AreEqual("NO_VOLUME_PROFILE",
                    Code("add_volume_override", "target", bare.name, "override_type", "Bloom"));
            }
            finally
            {
                Object.DestroyImmediate(bare);
            }
        }
    }
}
#endif
