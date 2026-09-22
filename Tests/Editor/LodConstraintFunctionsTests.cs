// Copyright (C) KitWright. All rights reserved.

#if KITWRIGHT_ANIMATION
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEngine.Animations;

namespace KitWright.Editor.Tests
{
    public sealed class LodConstraintFunctionsTests
    {
        [Test]
        public void ResolveConstraintType_KnownTypes()
        {
            Assert.AreEqual(typeof(PositionConstraint), LodConstraintFunctions.ResolveConstraintType("position").type);
            Assert.AreEqual(typeof(RotationConstraint), LodConstraintFunctions.ResolveConstraintType("rotation").type);
            Assert.AreEqual(typeof(ScaleConstraint), LodConstraintFunctions.ResolveConstraintType("scale").type);
            Assert.AreEqual(typeof(AimConstraint), LodConstraintFunctions.ResolveConstraintType("aim").type);
            Assert.AreEqual(typeof(LookAtConstraint), LodConstraintFunctions.ResolveConstraintType("lookat").type);
            Assert.AreEqual(typeof(ParentConstraint), LodConstraintFunctions.ResolveConstraintType("parent").type);
        }

        [Test]
        public void ResolveConstraintType_CaseInsensitive()
        {
            Assert.AreEqual(typeof(AimConstraint), LodConstraintFunctions.ResolveConstraintType("AIM").type);
        }

        [Test]
        public void ResolveConstraintType_CanonicalName()
        {
            Assert.AreEqual("LookAt", LodConstraintFunctions.ResolveConstraintType("lookat").canonical);
        }

        [Test]
        public void ResolveConstraintType_UnknownReturnsNull()
        {
            Assert.IsNull(LodConstraintFunctions.ResolveConstraintType("wobble").type);
        }
    }
}
#endif
