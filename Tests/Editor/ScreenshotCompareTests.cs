// Copyright (C) KitWright. All rights reserved.

using System.IO;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class ScreenshotCompareTests
    {
        // Under Library, not Assets: these never need to be imported, so the fixture costs no asset refresh.
        private static readonly string Folder =
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Library/KitWrightMcp/CompareTests");

        [SetUp]
        public void SetUp() => Directory.CreateDirectory(Folder);

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(Folder))
                Directory.Delete(Folder, true);
        }

        private static string WritePng(string name, int width, int height, Color32 fill, params (int x, int y, Color32 color)[] overrides)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color32[width * height];
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i] = fill;
                foreach (var (x, y, color) in overrides)
                    pixels[y * width + x] = color;

                texture.SetPixels32(pixels);
                texture.Apply();

                var path = Path.Combine(Folder, name);
                File.WriteAllBytes(path, texture.EncodeToPNG());
                return path;
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        private static JObject Compare(string a, string b, float tolerance = 0.02f, string diff = null) =>
            JObject.Parse(ScreenshotFunctions.CompareScreenshots(a, b, tolerance, diff));

        [Test]
        public void CompareScreenshots_IdenticalImagesReportNoDifference()
        {
            var a = WritePng("a.png", 8, 8, new Color32(10, 20, 30, 255));
            var b = WritePng("b.png", 8, 8, new Color32(10, 20, 30, 255));

            var data = Compare(a, b, 0f)["data"];
            Assert.AreEqual(0, data["differing_pixels"].Value<int>());
            Assert.AreEqual(0, data["max_channel_delta"].Value<int>());
        }

        [Test]
        public void CompareScreenshots_CountsExactlyTheChangedPixels()
        {
            var a = WritePng("a.png", 10, 10, new Color32(0, 0, 0, 255));
            var b = WritePng("b.png", 10, 10, new Color32(0, 0, 0, 255),
                (1, 1, new Color32(255, 255, 255, 255)),
                (2, 2, new Color32(255, 255, 255, 255)));

            var data = Compare(a, b, 0f)["data"];
            Assert.AreEqual(2, data["differing_pixels"].Value<int>());
            Assert.AreEqual(100, data["total_pixels"].Value<int>());
            Assert.AreEqual(2f, data["percent_different"].Value<float>(), 0.0001f);
            Assert.AreEqual(255, data["max_channel_delta"].Value<int>());
        }

        // The knob that separates a real regression from anti-aliasing noise: the same pair reads as
        // different at tolerance 0 and identical once the tolerance clears the channel move.
        [Test]
        public void CompareScreenshots_ToleranceDecidesWhetherASmallMoveCounts()
        {
            var a = WritePng("a.png", 4, 4, new Color32(100, 100, 100, 255));
            var b = WritePng("b.png", 4, 4, new Color32(100, 100, 100, 255),
                (0, 0, new Color32(103, 100, 100, 255)));

            Assert.AreEqual(1, Compare(a, b, 0f)["data"]["differing_pixels"].Value<int>());
            Assert.AreEqual(0, Compare(a, b, 0.02f)["data"]["differing_pixels"].Value<int>());
        }

        [Test]
        public void CompareScreenshots_DifferentSizesAreAnErrorNotAPartialComparison()
        {
            var a = WritePng("a.png", 8, 8, Color.black);
            var b = WritePng("b.png", 8, 4, Color.black);

            StringAssert.Contains("SIZE_MISMATCH", ScreenshotFunctions.CompareScreenshots(a, b));
        }

        [Test]
        public void CompareScreenshots_WritesADiffImageMatchingTheComparedSize()
        {
            var a = WritePng("a.png", 6, 6, Color.black);
            var b = WritePng("b.png", 6, 6, Color.black, (3, 3, Color.white));
            var diffPath = Path.Combine(Folder, "diff.png");

            var reported = Compare(a, b, 0f, diffPath)["data"]["diff_path"].Value<string>();

            Assert.IsTrue(File.Exists(reported), reported);

            var diff = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(diff.LoadImage(File.ReadAllBytes(reported)));
                Assert.AreEqual(6, diff.width);
                Assert.AreEqual(6, diff.height);
                Assert.AreEqual(new Color32(255, 0, 0, 255), (Color32)diff.GetPixel(3, 3));
            }
            finally
            {
                Object.DestroyImmediate(diff);
            }
        }

        [Test]
        public void CompareScreenshots_MissingFileIsReportedAgainstTheParameterThatNamedIt()
        {
            var a = WritePng("a.png", 4, 4, Color.black);

            var message = ScreenshotFunctions.CompareScreenshots(a, Path.Combine(Folder, "gone.png"));

            StringAssert.Contains("SCREENSHOT_NOT_FOUND", message);
            StringAssert.Contains("path_b", message);
        }
    }
}
