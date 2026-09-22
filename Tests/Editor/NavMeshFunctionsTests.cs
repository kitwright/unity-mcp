// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class NavMeshFunctionsTests
    {
        [Test]
        public void TryParseVector3_Valid()
        {
            Assert.IsTrue(ValueConverter.TryParseVector3("1,2,3", out var v, out _));
            Assert.AreEqual(new Vector3(1, 2, 3), v);
        }

        [Test]
        public void TryParseVector3_HandlesParenthesesAndSpaces()
        {
            Assert.IsTrue(ValueConverter.TryParseVector3("( 1.5 , -2 , 0 )", out var v, out _));
            Assert.AreEqual(new Vector3(1.5f, -2f, 0f), v);
        }

        [Test]
        public void TryParseVector3_TooFewComponents()
        {
            Assert.IsFalse(ValueConverter.TryParseVector3("1,2", out _, out _));
        }

        [Test]
        public void TryParseVector3_NonNumeric()
        {
            Assert.IsFalse(ValueConverter.TryParseVector3("a,b,c", out _, out _));
        }

        [Test]
        public void TryParseVector3_NullOrEmpty()
        {
            Assert.IsFalse(ValueConverter.TryParseVector3(null, out _, out _));
            Assert.IsFalse(ValueConverter.TryParseVector3("", out _, out _));
        }
    }
}
