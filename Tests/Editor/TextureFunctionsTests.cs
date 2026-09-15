// Copyright (C) KitWright. Licensed under MIT.

using System.Collections.Generic;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class TextureFunctionsTests
    {
        [Test]
        public void ParseColor_HexRgba()
        {
            var c = TextureFunctions.ParseColor("#ff0000ff");
            Assert.AreEqual(255, c.r);
            Assert.AreEqual(0, c.g);
            Assert.AreEqual(0, c.b);
            Assert.AreEqual(255, c.a);
        }

        [Test]
        public void ParseColor_CommaSeparatedDefaultsAlphaTo255()
        {
            var c = TextureFunctions.ParseColor("10,20,30");
            Assert.AreEqual(10, c.r);
            Assert.AreEqual(20, c.g);
            Assert.AreEqual(30, c.b);
            Assert.AreEqual(255, c.a);
        }

        [Test]
        public void ParseColor_CommaSeparatedWithAlpha()
        {
            var c = TextureFunctions.ParseColor("10,20,30,40");
            Assert.AreEqual(40, c.a);
        }

        [Test]
        public void ParsePalette_NullOrEmptyReturnsNull()
        {
            Assert.IsNull(TextureFunctions.ParsePalette(null));
            Assert.IsNull(TextureFunctions.ParsePalette("   "));
        }

        [Test]
        public void ParsePalette_SemicolonSeparated()
        {
            var palette = TextureFunctions.ParsePalette("#000000ff;#ffffffff;10,10,10");
            Assert.AreEqual(3, palette.Count);
            Assert.AreEqual(255, palette[1].r);
        }

        // A gradient that never reaches 0 loses the first colour of the palette outright: black to
        // white renders as grey to white.
        [TestCase(0f, 0f, 0.5f, 0f)]
        [TestCase(0f, 1f, 0.5f, 1f)]
        [TestCase(180f, 0f, 0.5f, 1f)]
        [TestCase(180f, 1f, 0.5f, 0f)]
        [TestCase(45f, 0f, 0f, 0f)]
        [TestCase(45f, 1f, 1f, 1f)]
        [TestCase(90f, 0.5f, 0f, 0f)]
        [TestCase(90f, 0.5f, 1f, 1f)]
        public void LinearGradientT_SpansThePaletteAtEveryAngle(float angle, float u, float v, float expected)
        {
            var rad = angle * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

            Assert.AreEqual(expected, TextureFunctions.LinearGradientT(new Vector2(u, v), dir), 0.0001f);
        }

        [Test]
        public void LerpPalette_ClampsEnds()
        {
            var p = new List<Color32> { new Color32(0, 0, 0, 255), new Color32(255, 255, 255, 255) };
            Assert.AreEqual(0, TextureFunctions.LerpPalette(p, -1f).r);
            Assert.AreEqual(255, TextureFunctions.LerpPalette(p, 2f).r);
        }

        [Test]
        public void LerpPalette_MidpointInterpolates()
        {
            var p = new List<Color32> { new Color32(0, 0, 0, 255), new Color32(255, 255, 255, 255) };
            var mid = TextureFunctions.LerpPalette(p, 0.5f);
            Assert.That(mid.r, Is.InRange(126, 129));
        }

        [Test]
        public void LerpPalette_SingleColorReturnsIt()
        {
            var p = new List<Color32> { new Color32(7, 7, 7, 255) };
            Assert.AreEqual(7, TextureFunctions.LerpPalette(p, 0.9f).r);
        }

        [Test]
        public void PatternColor_CheckerboardAlternates()
        {
            var p = new List<Color32> { new Color32(0, 0, 0, 255), new Color32(255, 255, 255, 255) };
            // size 1: (0,0)->idx0, (1,0)->idx1
            Assert.AreEqual(0, TextureFunctions.PatternColor(0, 0, "checkerboard", p, 1).r);
            Assert.AreEqual(255, TextureFunctions.PatternColor(1, 0, "checkerboard", p, 1).r);
        }

        [Test]
        public void PatternColor_UnknownPatternFallsBackToFirst()
        {
            var p = new List<Color32> { new Color32(5, 5, 5, 255), new Color32(9, 9, 9, 255) };
            Assert.AreEqual(5, TextureFunctions.PatternColor(3, 4, "does_not_exist", p, 8).r);
        }

        [Test]
        public void PatternColor_StripesHorizontalVsVertical()
        {
            var p = new List<Color32> { new Color32(0, 0, 0, 255), new Color32(255, 255, 255, 255) };
            // stripes_v keys off x, stripes_h off y
            Assert.AreEqual(0, TextureFunctions.PatternColor(0, 5, "stripes_v", p, 4).r);
            Assert.AreEqual(255, TextureFunctions.PatternColor(5, 0, "stripes_v", p, 4).r);
            Assert.AreEqual(0, TextureFunctions.PatternColor(5, 0, "stripes_h", p, 4).r);
            Assert.AreEqual(255, TextureFunctions.PatternColor(0, 5, "stripes_h", p, 4).r);
        }
    }
}
