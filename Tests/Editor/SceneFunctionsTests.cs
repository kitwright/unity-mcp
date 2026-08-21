// Copyright (C) KitWright. Licensed under MIT.

using System.IO;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class SceneFunctionsTests
    {
        // Under Temp/ rather than Assets/: the occupancy refusal happens before any scene API runs,
        // so the probe never has to be a real scene asset, and nothing here can be imported.
        private const string TestFolder = "Temp/__KitWrightSceneTests";
        private const string OccupiedPath = TestFolder + "/Occupied.unity";

        [SetUp]
        public void SetUp()
        {
            Directory.CreateDirectory(TestFolder);
            File.WriteAllText(OccupiedPath, "%YAML 1.1\n");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(TestFolder))
                Directory.Delete(TestFolder, true);
        }

        [Test]
        public void CreateNewScene_PathAlreadyOccupied_RefusesWithoutTouchingTheFile()
        {
            var before = new FileInfo(OccupiedPath);
            var length = before.Length;
            var lastWrite = before.LastWriteTimeUtc;

            var result = SceneFunctions.CreateNewScene("Occupied", TestFolder + "/");

            StringAssert.Contains("SCENE_EXISTS", result.ToString());
            var after = new FileInfo(OccupiedPath);
            Assert.AreEqual(length, after.Length);
            Assert.AreEqual(lastWrite, after.LastWriteTimeUtc);
        }

        [Test]
        public void CreateNewScene_SavePathWithoutTrailingSlash_StaysInsideTheFolder()
        {
            // Plain concatenation turned "Temp/X" + "Occupied.unity" into "Temp/XOccupied.unity",
            // which missed the occupancy check and then failed to save while reporting success.
            StringAssert.Contains("SCENE_EXISTS", SceneFunctions.CreateNewScene("Occupied", TestFolder).ToString());
        }
    }
}
