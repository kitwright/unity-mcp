// Copyright (C) KitWright. Licensed under MIT.

using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;

namespace KitWright.Editor.Tests
{
    public sealed class BuildFunctionsTests
    {
        [Test]
        public void TryResolveTarget_EmptyUsesActiveTarget()
        {
            Assert.IsTrue(BuildFunctions.TryResolveTarget(null, out var target));
            Assert.AreEqual(EditorUserBuildSettings.activeBuildTarget, target);
        }

        [Test]
        public void TryResolveTarget_KnownAliases()
        {
            Assert.IsTrue(BuildFunctions.TryResolveTarget("windows64", out var win));
            Assert.AreEqual(BuildTarget.StandaloneWindows64, win);

            Assert.IsTrue(BuildFunctions.TryResolveTarget("android", out var android));
            Assert.AreEqual(BuildTarget.Android, android);

            Assert.IsTrue(BuildFunctions.TryResolveTarget("webgl", out var webgl));
            Assert.AreEqual(BuildTarget.WebGL, webgl);
        }

        [Test]
        public void TryResolveTarget_CaseInsensitive()
        {
            Assert.IsTrue(BuildFunctions.TryResolveTarget("WINDOWS64", out var target));
            Assert.AreEqual(BuildTarget.StandaloneWindows64, target);
        }

        [Test]
        public void TryResolveTarget_UnknownReturnsFalse()
        {
            Assert.IsFalse(BuildFunctions.TryResolveTarget("nintendo_switch_2", out _));
        }

        [Test]
        public void DefaultOutputPath_WindowsHasExeExtension()
        {
            var path = BuildFunctions.DefaultOutputPath(BuildTarget.StandaloneWindows64, "MyGame");
            Assert.AreEqual("Builds/StandaloneWindows64/MyGame.exe", path);
        }

        [Test]
        public void DefaultOutputPath_WebGLIsDirectory()
        {
            var path = BuildFunctions.DefaultOutputPath(BuildTarget.WebGL, "MyGame");
            Assert.AreEqual("Builds/WebGL/MyGame", path);
        }

        [Test]
        public void ABuildOutsideTheProjectIsFine_ButNotOverTheOperatingSystem()
        {
            // A build folder outside the project is the ordinary case, not an attack: confining
            // output to the project root would break every team that keeps builds elsewhere.
            Assert.IsFalse(BuildFunctions.IsSystemLocation(@"C:\Builds\MyGame.exe"));
            Assert.IsFalse(BuildFunctions.IsSystemLocation("/home/ci/out/MyGame"));
            Assert.IsFalse(BuildFunctions.IsSystemLocation(
                System.IO.Path.GetFullPath("Builds/StandaloneWindows64/MyGame.exe")));

            var windows = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows))
            {
                Assert.IsTrue(BuildFunctions.IsSystemLocation(windows + @"\System32\evil.exe"));
                Assert.IsTrue(BuildFunctions.IsSystemLocation(windows.Replace('\\', '/') + "/notepad.exe"));
            }

            Assert.IsTrue(BuildFunctions.IsSystemLocation("/usr/bin/MyGame"));
            Assert.IsTrue(BuildFunctions.IsSystemLocation("/System/Library/x"));

            // A folder that merely starts with the same letters is not inside it.
            Assert.IsFalse(BuildFunctions.IsSystemLocation("/usrlocal/MyGame"));
        }
    }
}
