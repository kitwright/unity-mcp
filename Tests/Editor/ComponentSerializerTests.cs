// Copyright (C) KitWright. All rights reserved.

using System;
using System.Linq;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class ComponentSerializerTests
    {
        [Test]
        public void NameFilter_SplitsOnCommasAndMatchesCaseInsensitiveSubstrings()
        {
            Assert.IsEmpty(ComponentPropertyFunctions.SplitFilterTerms(null));
            Assert.IsEmpty(ComponentPropertyFunctions.SplitFilterTerms("  "));
            Assert.IsEmpty(ComponentPropertyFunctions.SplitFilterTerms(" , ,"));

            var terms = ComponentPropertyFunctions.SplitFilterTerms(" resolution , matchWidth ");
            Assert.AreEqual(new[] { "resolution", "matchWidth" }, terms);

            // Serialized names are m_-prefixed and PascalCase; a lowercase substring must still hit.
            Assert.IsTrue(ComponentPropertyFunctions.MatchesAnyTerm("m_ReferenceResolution", terms));
            Assert.IsTrue(ComponentPropertyFunctions.MatchesAnyTerm("m_MatchWidthOrHeight", terms));
            Assert.IsFalse(ComponentPropertyFunctions.MatchesAnyTerm("m_ScaleFactor", terms));
            Assert.IsFalse(ComponentPropertyFunctions.MatchesAnyTerm(null, terms));
        }

        [Test]
        public void ExtractPPtrTypeName_ParsesComponentType()
        {
            Assert.AreEqual("Rigidbody", ComponentSerializer.ExtractPPtrTypeName("PPtr<$Rigidbody>"));
            Assert.AreEqual("GameObject", ComponentSerializer.ExtractPPtrTypeName("PPtr<$GameObject>"));
        }

        [Test]
        public void ExtractPPtrTypeName_NonPPtrReturnsNull()
        {
            Assert.IsNull(ComponentSerializer.ExtractPPtrTypeName("int"));
            Assert.IsNull(ComponentSerializer.ExtractPPtrTypeName(""));
            Assert.IsNull(ComponentSerializer.ExtractPPtrTypeName(null));
        }

        // intValue clamps a negative to 0 on an unsigned field, so an "all bits set" mask used to write
        // as "no bits set" and still report success. MeshRenderer.m_RenderingLayerMask is uint32 and
        // needs no optional module, so it stands in for PhysicsManager's layer collision matrix.
        [Test]
        public void WriteProperties_NegativeMaskOnAnUnsignedFieldSetsEveryBitNotZero()
        {
            var go = new GameObject("ComponentSerializerUnsignedProbe");
            try
            {
                var renderer = go.AddComponent<MeshRenderer>();
                Assert.AreEqual(SerializedPropertyNumericType.UInt32,
                    new SerializedObject(renderer).FindProperty("m_RenderingLayerMask").numericType,
                    "Test target is no longer an unsigned field; pick another one.");

                var results = ComponentSerializer.WriteProperties(renderer, new JObject { ["m_RenderingLayerMask"] = -1 });

                Assert.IsTrue(results[0].Success, results[0].Error);
                Assert.AreEqual(uint.MaxValue,
                    new SerializedObject(renderer).FindProperty("m_RenderingLayerMask").uintValue);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void WriteProperties_UnsignedFieldTakesValuesPastIntMaxValue()
        {
            var go = new GameObject("ComponentSerializerUnsignedRangeProbe");
            try
            {
                var renderer = go.AddComponent<MeshRenderer>();

                var results = ComponentSerializer.WriteProperties(renderer,
                    new JObject { ["m_RenderingLayerMask"] = 4294967295L });

                Assert.IsTrue(results[0].Success, results[0].Error);
                Assert.AreEqual(uint.MaxValue,
                    new SerializedObject(renderer).FindProperty("m_RenderingLayerMask").uintValue);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // A user script with a `long` field is ordinary, and the 64-bit properties Unity ships are
        // PPtr internals it will not let a test set — hence a fixture type rather than a stock one.
        private sealed class SixtyFourBitFixture : ScriptableObject
        {
            public long Signed;
            public ulong Unsigned;
        }

        [Test]
        public void ReadProperties_SixtyFourBitFieldsAreNotTruncatedToThirtyTwo()
        {
            var asset = ScriptableObject.CreateInstance<SixtyFourBitFixture>();
            try
            {
                asset.Signed = 5_000_000_000L;
                asset.Unsigned = ulong.MaxValue;

                var props = ComponentSerializer.ReadProperties(asset, out _);

                Assert.AreEqual(5_000_000_000L, props.First(p => p.Name == "Signed").Value);
                Assert.AreEqual(ulong.MaxValue, props.First(p => p.Name == "Unsigned").Value);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void WriteProperties_SixtyFourBitFieldsRoundTripThroughJson()
        {
            var asset = ScriptableObject.CreateInstance<SixtyFourBitFixture>();
            try
            {
                var results = ComponentSerializer.WriteProperties(asset, new JObject { ["Signed"] = 5_000_000_000L });

                Assert.IsTrue(results[0].Success, results[0].Error);
                Assert.AreEqual(5_000_000_000L, asset.Signed);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        private enum Difficulty { Easy = 0, Normal = 1, Hard = 5 }

        private sealed class RoundTripFixture : ScriptableObject
        {
            public int[] Numbers = { 10, 20, 30 };
            public double Precise;
            public ulong Unsigned;
            public Difficulty Level;
        }

        // A failed field is reported as failed; the object must also look like it was never touched.
        [Test]
        public void WriteProperties_ArrayElementRejected_LeavesTheArrayAsItWas()
        {
            var asset = ScriptableObject.CreateInstance<RoundTripFixture>();
            try
            {
                var results = ComponentSerializer.WriteProperties(asset, new JObject
                {
                    ["Numbers"] = new JArray(99, "bad")
                });

                Assert.IsFalse(results[0].Success, "a string is not an int, so the write has to fail");
                Assert.AreEqual(new[] { 10, 20, 30 }, asset.Numbers,
                    "the array was resized and half filled before the element that failed");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void WriteProperties_OneFieldFailing_KeepsTheFieldsThatSucceeded()
        {
            var asset = ScriptableObject.CreateInstance<RoundTripFixture>();
            try
            {
                var results = ComponentSerializer.WriteProperties(asset, new JObject
                {
                    ["Precise"] = 1.5,
                    ["Numbers"] = new JArray(1, "bad")
                });

                Assert.IsTrue(results[0].Success, results[0].Error);
                Assert.IsFalse(results[1].Success);
                Assert.AreEqual(1.5, asset.Precise, "rolling back the bad field must not roll back the good one");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        // 16777217 is the first integer a float cannot hold: it rounds to 16777216.
        [Test]
        public void DoubleFields_KeepThePrecisionAFloatWouldLose()
        {
            var asset = ScriptableObject.CreateInstance<RoundTripFixture>();
            try
            {
                var results = ComponentSerializer.WriteProperties(asset, new JObject { ["Precise"] = 16777217.0 });

                Assert.IsTrue(results[0].Success, results[0].Error);
                Assert.AreEqual(16777217.0, asset.Precise);
                Assert.AreEqual(16777217.0,
                    ComponentSerializer.ReadProperties(asset, out _).First(p => p.Name == "Precise").Value);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void WriteProperties_UnsignedAboveLongMaxValue_IsAccepted()
        {
            var asset = ScriptableObject.CreateInstance<RoundTripFixture>();
            try
            {
                var results = ComponentSerializer.WriteProperties(asset,
                    new JObject { ["Unsigned"] = ulong.MaxValue });

                Assert.IsTrue(results[0].Success, results[0].Error);
                Assert.AreEqual(ulong.MaxValue, asset.Unsigned, "read reports it, so write has to accept it back");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        // -1 still has to mean every bit, which is how a mask is written.
        [Test]
        public void WriteProperties_MinusOneOnAnUnsignedField_SetsEveryBit()
        {
            var asset = ScriptableObject.CreateInstance<RoundTripFixture>();
            try
            {
                ComponentSerializer.WriteProperties(asset, new JObject { ["Unsigned"] = -1 });

                Assert.AreEqual(ulong.MaxValue, asset.Unsigned);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void EnumValue_CanBeWrittenBackInTheShapeItWasReadIn()
        {
            var asset = ScriptableObject.CreateInstance<RoundTripFixture>();
            try
            {
                asset.Level = Difficulty.Hard;
                var read = ComponentSerializer.ReadProperties(asset, out _).First(p => p.Name == "Level").Value;
                asset.Level = Difficulty.Easy;

                var results = ComponentSerializer.WriteProperties(asset,
                    new JObject { ["Level"] = JToken.FromObject(read) });

                Assert.IsTrue(results[0].Success, results[0].Error);
                Assert.AreEqual(Difficulty.Hard, asset.Level);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        // Settings singletons keep writable fields off the inspector, and NextVisible skips exactly
        // those — so the dump used to omit properties set_project_settings can write.
        [Test]
        public void ReadProperties_IncludeHiddenSurfacesWhatNextVisibleSkips()
        {
            var go = new GameObject("ComponentSerializerHiddenProbe");
            try
            {
                bool HasHideFlags(bool includeHidden) => ComponentSerializer
                    .ReadProperties(go.transform, out _, includeHidden: includeHidden)
                    .Any(p => p.Name == "m_ObjectHideFlags");

                Assert.IsFalse(HasHideFlags(false));
                Assert.IsTrue(HasHideFlags(true));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

#if KITWRIGHT_PHYSICS2D
        [Test]
        public void FlagsEnum_MaskRoundTrips()
        {
            var go = new GameObject("ComponentSerializerFlagsProbe");
            try
            {
                var body = go.AddComponent<Rigidbody2D>();
                const int mask = (int)(RigidbodyConstraints2D.FreezePositionX | RigidbodyConstraints2D.FreezePositionY);

                var results = ComponentSerializer.WriteProperties(body, new JObject { ["m_Constraints"] = mask });
                Assert.IsTrue(results[0].Success, results[0].Error);
                Assert.AreEqual((RigidbodyConstraints2D)mask, body.constraints);

                var snapshot = ComponentSerializer.ReadProperties(body, out _).First(p => p.Name == "m_Constraints");
                var token = JToken.FromObject(snapshot.Value);
                Assert.AreEqual(mask, token.Type == JTokenType.Object ? token.Value<int>("value") : token.Value<int>());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
#endif

        [Test]
        public void ReadProperties_DescendStopsAtMaxProperties()
        {
            var go = new GameObject("__cap_probe");
            try
            {
                var line = go.AddComponent<LineRenderer>();
                line.positionCount = 1000;

                ComponentSerializer.ReadProperties(line, out var total, descend: true);
                Assert.AreEqual(Math.Min(50, total),
                    ComponentSerializer.ReadProperties(line, out _, descend: true, maxProperties: 50).Count);
                Assert.Greater(total, 50, "A 1000-position LineRenderer must exceed the cap under descend.");

                var response = ComponentPropertyFunctions.GetComponentProperties(
                    component_instance_id: ObjectIdCodec.GetSerializableId(line),
                    descend: true,
                    max_properties: 50);
                var message = JObject.FromObject(response).Value<string>("message");

                StringAssert.Contains($"50 of {total} properties", message);
                StringAssert.Contains($"Showing 1-50 of {total}; pass cursor=50", message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void GetComponentProperties_CursorMovesTheWindowOnAndStillCountsAgainstTheRealTotal()
        {
            var go = new GameObject("__cursor_probe");
            try
            {
                var line = go.AddComponent<LineRenderer>();
                line.positionCount = 1000;
                var id = ObjectIdCodec.GetSerializableId(line);

                ComponentSerializer.ReadProperties(line, out var total, descend: true);
                Assert.Greater(total, 100, "A 1000-position LineRenderer must exceed two pages of 50.");

                var page1 = JObject.FromObject(ComponentPropertyFunctions.GetComponentProperties(
                    component_instance_id: id, descend: true, max_properties: 50));
                var page2 = JObject.FromObject(ComponentPropertyFunctions.GetComponentProperties(
                    component_instance_id: id, descend: true, max_properties: 50, cursor: 50));

                StringAssert.Contains($"Showing 51-100 of {total}; pass cursor=100",
                    page2.Value<string>("message"));

                var names1 = page1["data"]["properties"].Select(p => p.Value<string>("Name")).ToList();
                var names2 = page2["data"]["properties"].Select(p => p.Value<string>("Name")).ToList();
                Assert.AreEqual(50, names2.Count);
                CollectionAssert.IsEmpty(names1.Intersect(names2),
                    "Page two repeated page one, so the window did not move.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

#if KITWRIGHT_PARTICLES
        [Test]
        public void UnsupportedPropertyType_ReadsAsUnreadableMarker()
        {
            var go = new GameObject("ComponentSerializerUnreadableProbe");
            try
            {
                var particles = go.AddComponent<ParticleSystem>();
                var props = ComponentSerializer.ReadProperties(particles, out _);

                Assert.IsTrue(props.Any(p => p.Value is string s && s.StartsWith("<unreadable ")),
                    "Expected at least one '<unreadable {type}>' marker among ParticleSystem's module properties.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
#endif

        // get_scriptable_object reads through the same ComponentSerializer.ReadProperties overload
        // as the component tools, so a signature change there has to keep surfacing every property.
        [Test]
        public void GetScriptableObject_ReportsEveryPropertyReadPropertiesSees()
        {
            const string folderName = "__KitWrightScriptableObjectReadTests";
            var folder = "Assets/" + folderName;
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", folderName);

            try
            {
                var path = folder + "/Probe.asset";
                var asset = ScriptableObject.CreateInstance<ScriptableObjectReadProbe>();
                AssetDatabase.CreateAsset(asset, path);

                var expected = ComponentSerializer.ReadProperties(asset, out _).Count;
                var response = JObject.FromObject(ScriptableObjectFunctions.GetScriptableObject(path));

                Assert.IsTrue(response.Value<bool>("success"), response.ToString());
                Assert.Greater(expected, 0, "the probe declares serialized fields, so a read must see them.");
                Assert.AreEqual(expected, ((JArray)response["data"]["properties"]).Count);
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(folder))
                    AssetDatabase.DeleteAsset(folder);
            }
        }

        // set_component_property used to be the tool that failed loudly on a bad field name; with it
        // gone, the plural has to fail too when nothing landed, or a typo reads as success.
        [Test]
        public void SetComponentProperties_NothingApplied_IsAnError()
        {
            var go = new GameObject("__KitWrightPropertyGuardProbe");
            try
            {
                go.AddComponent<Camera>();

                var allBad = JObject.FromObject(ComponentPropertyFunctions.SetComponentProperties(
                    target: go.name, component: "Camera", properties: @"{""noSuchField"": 1}"));
                Assert.IsFalse(allBad.Value<bool>("success"), allBad.ToString());
                Assert.AreEqual("PROPERTY_SET_FAILED", allBad.Value<string>("code"));

                var partial = JObject.FromObject(ComponentPropertyFunctions.SetComponentProperties(
                    target: go.name, component: "Camera",
                    properties: @"{""field of view"": 42, ""noSuchField"": 1}"));
                Assert.IsTrue(partial.Value<bool>("success"), partial.ToString());
                Assert.AreEqual(1, partial["data"].Value<int>("successCount"));
                Assert.AreEqual(1, partial["data"].Value<int>("failCount"),
                    "a partial write stays diagnosable rather than becoming an error");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // WriteProperties has two callers, so the guard above only half-closed the hole: a typo on
        // an .asset still came back as success, with "asset saved" attached to a write that landed
        // nothing.
        [Test]
        public void SetScriptableObjectProperties_NothingApplied_IsAnError()
        {
            const string folderName = "__KitWrightSoPropertyGuardProbe";
            var folder = "Assets/" + folderName;
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", folderName);

            try
            {
                var path = folder + "/Probe.asset";
                AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<ScriptableObjectReadProbe>(), path);

                var allBad = JObject.FromObject(ScriptableObjectFunctions.SetScriptableObjectProperties(
                    path, @"{""noSuchField"": 1}"));
                Assert.IsFalse(allBad.Value<bool>("success"), allBad.ToString());
                Assert.AreEqual("PROPERTY_SET_FAILED", allBad.Value<string>("code"));

                var partial = JObject.FromObject(ScriptableObjectFunctions.SetScriptableObjectProperties(
                    path, @"{""number"": 7, ""noSuchField"": 1}"));
                Assert.IsTrue(partial.Value<bool>("success"), partial.ToString());
                Assert.AreEqual(1, partial["data"].Value<int>("successCount"));
                Assert.AreEqual(1, partial["data"].Value<int>("failCount"));
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(folder))
                    AssetDatabase.DeleteAsset(folder);
            }
        }
    }

    internal sealed class ScriptableObjectReadProbe : ScriptableObject
    {
        public int number;
        public string label;
    }
}
