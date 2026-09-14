// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.IO;
using KitWright.Editor.Services;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class PathSafetyTests
    {
        [Test]
        public void ResolveProjectPath_RelativeEscape_Throws()
        {
            Assert.Throws<PathOutsideProjectException>(() =>
                PathSafety.ResolveProjectPath("../../../Users/me/.ssh/id_rsa"));
        }

        [Test]
        public void ResolveProjectPath_AbsoluteOutsideProject_Throws()
        {
            var outside = Path.Combine(Path.GetTempPath(), "kitwright-escape.txt");
            Assert.Throws<PathOutsideProjectException>(() => PathSafety.ResolveProjectPath(outside));
        }

        [TestCase("Assets/Foo/Bar.cs", "Assets/Foo/Bar.cs")]
        [TestCase("Assets/A/../B.cs", "Assets/B.cs")]
        public void ResolveProjectPath_InsideProject_Resolves(string requested, string expectedRelative)
        {
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(ApplicationPaths.ProjectRoot, expectedRelative)),
                PathSafety.ResolveProjectPath(requested));
        }

        // Every one of these starts with "Assets/", which is all the write tools used to check.
        [TestCase("Assets/../../outside.png")]
        [TestCase("Assets/../Library/sneak.png")]
        [TestCase("Assets/Textures/../../../escape.png")]
        [TestCase("NotAssets/x.png")]
        [TestCase("")]
        [TestCase(null)]
        public void ResolveAssetPath_LandingOutsideAssets_Throws(string requested)
        {
            Assert.Throws<PathOutsideProjectException>(() => PathSafety.ResolveAssetPath(requested));
        }

        [TestCase("Assets/Textures/white.png", "Assets/Textures/white.png")]
        [TestCase("Assets\\Textures\\white.png", "Assets/Textures/white.png")]
        [TestCase("Assets/Textures/../white.png", "Assets/white.png")]
        public void ResolveAssetPath_StayingUnderAssets_Resolves(string requested, string expectedRelative)
        {
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(ApplicationPaths.ProjectRoot, expectedRelative)),
                PathSafety.ResolveAssetPath(requested));
        }
    }
}
