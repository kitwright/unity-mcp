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
    }
}
