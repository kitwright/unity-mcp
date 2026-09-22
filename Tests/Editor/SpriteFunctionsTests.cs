// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class SpriteFunctionsTests
    {
        [Test]
        public void TryParseVector2Int_Valid()
        {
            Assert.IsTrue(SpriteFunctions.TryParseVector2Int("4,8", out var v));
            Assert.AreEqual(new Vector2Int(4, 8), v);
        }

        [Test]
        public void TryParseVector2Int_HandlesParenthesesAndSpaces()
        {
            Assert.IsTrue(SpriteFunctions.TryParseVector2Int("( 2 , 3 )", out var v));
            Assert.AreEqual(new Vector2Int(2, 3), v);
        }

        [Test]
        public void TryParseVector2Int_TooFew()
        {
            Assert.IsFalse(SpriteFunctions.TryParseVector2Int("5", out _));
        }

        [Test]
        public void TryParseVector2Int_NonNumeric()
        {
            Assert.IsFalse(SpriteFunctions.TryParseVector2Int("a,b", out _));
        }

        [Test]
        public void TryParseVector2Int_NullOrEmpty()
        {
            Assert.IsFalse(SpriteFunctions.TryParseVector2Int(null, out _));
            Assert.IsFalse(SpriteFunctions.TryParseVector2Int("", out _));
        }
    }
}
