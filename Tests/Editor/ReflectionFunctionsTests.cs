// Copyright (C) KitWright. All rights reserved.

using System.Linq;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class ReflectionProbeSubject
    {
        public int Visible(int value) => value;

        public float Scaled(int steps, float factor = 0.5f, string label = null) => steps * factor;

        public int Sum(params int[] values) => values.Length;

        private int Hidden(int value) => value;
    }

    public static class ReflectionProbeExtensions
    {
        public static int KitWrightProbe(this ReflectionProbeSubject subject, int factor) => factor;
    }
}

namespace KitWright.Editor.Tests.Left
{
    // Paired with the namesake below so the ambiguity path has a case that does not depend on
    // which non-Unity assemblies the host project happens to load.
    public sealed class ReflectionProbeDuplicate { }
}

namespace KitWright.Editor.Tests.Right
{
    public sealed class ReflectionProbeDuplicate { }
}

namespace KitWright.Editor.Tests
{

    public sealed class ReflectionFunctionsTests
    {
        private static JObject Call(
            string name = null,
            string member = null,
            string search = null,
            string scope = null,
            bool includeNonPublic = false)
        {
            var result = ReflectionFunctions.ReflectApi(name, member, search, scope, includeNonPublic);
            return JObject.Parse(JsonConvert.SerializeObject(result));
        }

        [Test]
        public void Resolve_ShortUnityTypeName()
        {
            var type = ReflectionFunctions.Resolve("Rigidbody", out _, out var ambiguous);

            Assert.AreEqual(typeof(Rigidbody), type);
            Assert.IsFalse(ambiguous);
        }

        [Test]
        public void Resolve_StaticClassOutsideUnityObjectHierarchy()
        {
            Assert.AreEqual(typeof(Mathf), ReflectionFunctions.Resolve("Mathf", out _, out _));
        }

        [Test]
        public void Resolve_AmbiguousShortNameReportsMatches()
        {
            var type = ReflectionFunctions.Resolve("ReflectionProbeDuplicate", out var matches, out var ambiguous);

            Assert.IsNull(type);
            Assert.IsTrue(ambiguous);
            Assert.Contains("KitWright.Editor.Tests.Left.ReflectionProbeDuplicate", matches);
            Assert.Contains("KitWright.Editor.Tests.Right.ReflectionProbeDuplicate", matches);
        }

        [Test]
        public void Resolve_UnityObjectWinsOverSystemObject()
        {
            var type = ReflectionFunctions.Resolve("Object", out var alternatives, out var ambiguous);

            Assert.AreEqual(typeof(Object), type);
            Assert.IsFalse(ambiguous);
            Assert.Contains("System.Object", alternatives);
        }

        [Test]
        public void Resolve_FullNameBeatsAmbiguity()
        {
            Assert.AreEqual(typeof(Object), ReflectionFunctions.Resolve("UnityEngine.Object", out _, out var ambiguous));
            Assert.IsFalse(ambiguous);
        }

        [Test]
        public void Resolve_NestedTypeByShortName()
        {
            Assert.AreEqual(typeof(Camera.GateFitMode), ReflectionFunctions.Resolve("GateFitMode", out _, out _));
        }

        [Test]
        public void Resolve_NestedTypeBySourceSpelling()
        {
            Assert.AreEqual(typeof(Camera.GateFitMode), ReflectionFunctions.Resolve("Camera.GateFitMode", out _, out _));
            Assert.AreEqual(typeof(Camera.GateFitMode), ReflectionFunctions.Resolve("UnityEngine.Camera+GateFitMode", out _, out _));
        }

        [Test]
        public void Resolve_UnityTypeWinsAShortNameSharedWithTheBcl()
        {
            var type = ReflectionFunctions.Resolve("Debug", out var alternatives, out var ambiguous);

            Assert.AreEqual(typeof(Debug), type);
            Assert.IsFalse(ambiguous);
            Assert.Contains("System.Diagnostics.Debug", alternatives);
        }

        [Test]
        public void ReflectApi_ReportsTheShortNameItPickedFrom()
        {
            var message = Call("Debug")["message"].Value<string>();

            StringAssert.Contains("Resolved to 'UnityEngine.Debug'", message);
            StringAssert.Contains("System.Diagnostics.Debug", message);
        }

        [Test]
        public void Resolve_TopLevelTypeBeatsNestedNamesakes()
        {
            var type = ReflectionFunctions.Resolve("Physics", out _, out var ambiguous);

            Assert.AreEqual(typeof(Physics), type);
            Assert.IsFalse(ambiguous);
        }

        [Test]
        public void Resolve_TypoReturnsCandidates()
        {
            var type = ReflectionFunctions.Resolve("Rigibody", out var candidates, out var ambiguous);

            Assert.IsNull(type);
            Assert.IsFalse(ambiguous);
            Assert.Contains("Rigidbody", candidates);
        }

        [Test]
        public void Resolve_UnknownNameReturnsNoCandidates()
        {
            var type = ReflectionFunctions.Resolve("Zzz_NoSuchType_9", out var candidates, out _);

            Assert.IsNull(type);
            Assert.IsEmpty(candidates);
        }

        [Test]
        public void Signatures_IncludeParameterNames()
        {
            var signatures = ReflectionFunctions.Signatures(
                typeof(Rigidbody), "AddForce", ReflectionFunctions.DeclaredMembers);

            Assert.IsNotEmpty(signatures);
            Assert.IsTrue(signatures.Any(s => s.Contains("Vector3 force")), string.Join(" | ", signatures));
        }

        [Test]
        public void Signatures_FallBackToInheritedMembers()
        {
            Assert.IsEmpty(ReflectionFunctions.Signatures(
                typeof(Rigidbody), "GetComponent", ReflectionFunctions.DeclaredMembers));

            Assert.IsNotEmpty(ReflectionFunctions.Signatures(
                typeof(Rigidbody), "GetComponent", ReflectionFunctions.InheritedMembers));
        }

        [Test]
        public void ReflectApi_OpenGenericDoesNotReflectMembers()
        {
            Assert.IsNotNull(ReflectionFunctions.ReflectApi("System.Collections.Generic.List`1"));
        }

        [Test]
        public void Search_RanksTheExactMatchFirst()
        {
            var results = Call(search: "Rigidbody", scope: "unity")["data"]["results"];

            Assert.AreEqual("UnityEngine.Rigidbody", results[0]["full_name"].Value<string>());
        }

        [Test]
        public void Search_RejectsAnUnknownScope()
        {
            Assert.AreEqual("INVALID_SCOPE", Call(search: "Rigidbody", scope: "everywhere")["code"].Value<string>());
        }

        [Test]
        public void Search_ProjectScopeExcludesUnityAssemblies()
        {
            Assert.IsFalse(ReflectionFunctions.MatchesScope("UnityEngine.CoreModule", "project"));
            Assert.IsTrue(ReflectionFunctions.MatchesScope("UnityEngine.CoreModule", "unity"));
        }

        [Test]
        public void Member_FallsBackToExtensionMethods()
        {
            var response = Call(nameof(ReflectionProbeSubject), "KitWrightProbe");

            Assert.IsTrue(response["success"].Value<bool>());
            Assert.IsTrue(response["data"]["extension"].Value<bool>());
            Assert.IsTrue(response["data"]["signatures"][0].Value<string>().Contains("int factor"));
        }

        [Test]
        public void Type_ListsApplicableExtensionMethods()
        {
            var extensions = Call(nameof(ReflectionProbeSubject))["data"]["extension_methods"]
                .Values<string>();

            Assert.Contains("KitWrightProbe", extensions.ToArray());
        }

        [Test]
        public void Member_NonPublicIsHiddenUnlessRequested()
        {
            Assert.AreEqual("MEMBER_NOT_FOUND", Call(nameof(ReflectionProbeSubject), "Hidden")["code"].Value<string>());

            var response = Call(nameof(ReflectionProbeSubject), "Hidden", includeNonPublic: true);
            Assert.IsTrue(response["success"].Value<bool>());
        }

        [Test]
        public void Signatures_MarkOutParameters()
        {
            var signatures = ReflectionFunctions.Signatures(
                typeof(Physics), "Raycast", ReflectionFunctions.DeclaredMembers);

            Assert.IsTrue(signatures.Any(s => s.Contains("out RaycastHit hitInfo")), string.Join(" | ", signatures));
        }

        [Test]
        public void Signatures_ShowOptionalDefaults()
        {
            var signature = ReflectionFunctions.Signatures(
                typeof(ReflectionProbeSubject), "Scaled", ReflectionFunctions.DeclaredMembers).Single();

            Assert.AreEqual("float Scaled(int steps, float factor = 0.5, string label = null)", signature);
        }

        [Test]
        public void Signatures_MarkParamsArrays()
        {
            var signature = ReflectionFunctions.Signatures(
                typeof(ReflectionProbeSubject), "Sum", ReflectionFunctions.DeclaredMembers).Single();

            Assert.AreEqual("int Sum(params int[] values)", signature);
        }

        [Test]
        public void Signatures_SpellOutGenericArguments()
        {
            var signatures = ReflectionFunctions.Signatures(
                typeof(Component), "GetComponent", ReflectionFunctions.DeclaredMembers);

            Assert.IsTrue(signatures.Any(s => s.Contains("GetComponent<T>")), string.Join(" | ", signatures));
        }

        [Test]
        public void Header_ReportsInterfaces()
        {
            var interfaces = Call("UnityEngine.Transform")["data"]["interfaces"].Values<string>();

            Assert.Contains("IEnumerable", interfaces.ToArray());
        }
    }
}
