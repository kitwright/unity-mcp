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

        // The junction lives under the project's Temp folder: inside the root, so the path passes the
        // containment check exactly as a link under Assets would, and Unity never imports Temp.
        [Test]
        public void ResolveProjectPath_ThroughAJunction_Throws()
        {
            if (Path.DirectorySeparatorChar != '\\')
                Assert.Ignore("mklink is Windows-only; the reparse-point check itself is not.");

            var target = Path.Combine(Path.GetTempPath(), "kitwright-junction-target-" + Guid.NewGuid().ToString("N"));
            var link = Path.Combine(ApplicationPaths.ProjectRoot, "Temp", "kitwright-junction-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(Path.GetDirectoryName(link));

            if (!TryCreateJunction(link, target))
            {
                Directory.Delete(target, true);
                Assert.Ignore("Could not create a junction on this machine.");
            }

            try
            {
                File.WriteAllText(Path.Combine(target, "secret.txt"), "outside the project");
                var through = "Temp/" + Path.GetFileName(link) + "/secret.txt";

                // Without the check this resolves to a path under the project root and reads the file
                // that actually sits in the system temp directory.
                Assert.Throws<PathOutsideProjectException>(() => PathSafety.ResolveProjectPath(through));
            }
            finally
            {
                try { Directory.Delete(link); } catch { }
                try { Directory.Delete(target, true); } catch { }
            }
        }

        private static bool TryCreateJunction(string link, string target)
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            process.WaitForExit(10000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(link);
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
