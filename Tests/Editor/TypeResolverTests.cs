// Copyright (C) KitWright. Licensed under MIT.

using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests.TypeResolverLeft
{
    // Paired with the namesake below so the ambiguity path has a case that does not depend on
    // which non-Unity assemblies the host project happens to load.
    public sealed class TypeResolverProbeDuplicate : ScriptableObject { }
}

namespace KitWright.Editor.Tests.TypeResolverRight
{
    public sealed class TypeResolverProbeDuplicate : ScriptableObject { }
}

namespace KitWright.Editor.Tests.TypeResolverShadow
{
    // Shadows a Unity type's short name, which the resolver must not prefer over Unity's own.
    public sealed class Camera : ScriptableObject { }
}

namespace KitWright.Editor.Tests
{
    public sealed class TypeResolverTests
    {
        [Test]
        public void Resolve_UnityTypeWinsOverAProjectNamesake()
        {
            Assert.AreEqual(typeof(UnityEngine.Camera), TypeResolver.Resolve("Camera"));
            Assert.AreEqual(typeof(UnityEngine.Camera), TypeResolver.ResolveComponent("Camera"));
        }

        [Test]
        public void Resolve_AmbiguousShortNameReturnsNull()
        {
            Assert.IsNull(TypeResolver.Resolve("TypeResolverProbeDuplicate"),
                "A short name shared by two project types resolved to whichever was scanned first.");
        }

        [Test]
        public void Resolve_FullNameBeatsAmbiguity()
        {
            Assert.AreEqual(
                typeof(TypeResolverRight.TypeResolverProbeDuplicate),
                TypeResolver.Resolve("KitWright.Editor.Tests.TypeResolverRight.TypeResolverProbeDuplicate"));
        }

        [Test]
        public void UnresolvedError_SaysAmbiguousRatherThanNotFound()
        {
            var json = JsonConvert.SerializeObject(
                TypeResolver.UnresolvedError("TypeResolverProbeDuplicate", "COMPONENT_TYPE_NOT_FOUND", "component_type"));

            StringAssert.Contains("AMBIGUOUS_TYPE", json,
                "an ambiguous name was reported as if the type did not exist");
            StringAssert.Contains("TypeResolverLeft.TypeResolverProbeDuplicate", json, "candidates must name the choices");
            StringAssert.Contains("TypeResolverRight.TypeResolverProbeDuplicate", json);
        }

        [Test]
        public void UnresolvedError_KeepsNotFoundForANameNothingDeclares()
        {
            var json = JsonConvert.SerializeObject(
                TypeResolver.UnresolvedError("NoSuchTypeAnywhere", "COMPONENT_TYPE_NOT_FOUND", "component_type"));

            StringAssert.Contains("COMPONENT_TYPE_NOT_FOUND", json);
            Assert.IsFalse(json.Contains("AMBIGUOUS_TYPE"));
        }
    }
}
