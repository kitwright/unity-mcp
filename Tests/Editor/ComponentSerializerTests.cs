// Copyright (C) KitWright. Licensed under MIT.

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
                StringAssert.Contains("truncated", message);
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
    }

    internal sealed class ScriptableObjectReadProbe : ScriptableObject
    {
        public int number;
        public string label;
    }
}
