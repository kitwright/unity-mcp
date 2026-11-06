// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.IO;
using KitWright.Editor.Services;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    public sealed class ShaderFunctionsTests
    {
        // Guards only: importing a .shader compiles its variants, which left the editor still updating
        // for the next fixture (7 EDITOR_BUSY failures) and pushed the suite from 45s to 888s. What is
        // worth pinning is that create_shader does not touch the disk when it refuses.
        [Test]
        public void CreateShader_RejectsANameThatWouldNotCompileWithoutWritingAFile()
        {
            StringAssert.Contains("INVALID_NAME", ShaderFunctions.CreateShader("9Lives").ToString());
            StringAssert.Contains("INVALID_NAME", ShaderFunctions.CreateShader("has spaces").ToString());
            StringAssert.Contains("INVALID_NAME", ShaderFunctions.CreateShader("Custom/Nested").ToString());

            Assert.IsFalse(File.Exists(ShaderFunctions.ResolvePaths("9Lives", "Shaders").fullPath));
        }

        [Test]
        public void ResolvePaths_DefaultFolderIsShaders()
        {
            var (_, relativePath) = ShaderFunctions.ResolvePaths("MyShader", "Shaders");
            Assert.AreEqual("Assets/Shaders/MyShader.shader", relativePath);
        }

        [Test]
        public void ResolvePaths_NullPathFallsBackToShaders()
        {
            var (_, relativePath) = ShaderFunctions.ResolvePaths("MyShader", null);
            Assert.AreEqual("Assets/Shaders/MyShader.shader", relativePath);
        }

        [Test]
        public void ResolvePaths_StripsLeadingAssetsPrefix()
        {
            var (_, relativePath) = ShaderFunctions.ResolvePaths("Foo", "Assets/Custom/Sub");
            Assert.AreEqual("Assets/Custom/Sub/Foo.shader", relativePath);
        }

        [Test]
        public void ResolvePaths_NormalizesBackslashes()
        {
            var (_, relativePath) = ShaderFunctions.ResolvePaths("Foo", "Custom\\Sub");
            Assert.AreEqual("Assets/Custom/Sub/Foo.shader", relativePath);
        }

        [Test]
        public void ResolvePaths_BareAssetsFallsBackToShaders()
        {
            var (_, relativePath) = ShaderFunctions.ResolvePaths("Foo", "Assets");
            Assert.AreEqual("Assets/Shaders/Foo.shader", relativePath);
        }

        [TestCase("Foo", "Assets/../../KitWrightEscape")]
        [TestCase("Foo", "../KitWrightEscape")]
        [TestCase("../../../KitWrightEscape", "Shaders")]
        public void ResolvePaths_RefusesAFolderOrNameThatLeavesAssets(string name, string path)
        {
            Assert.Throws<PathOutsideProjectException>(() => ShaderFunctions.ResolvePaths(name, path));
        }

        [Test]
        public void ResolvePaths_RefusesAnAbsoluteFolder()
        {
            if (Path.DirectorySeparatorChar != '\\')
                Assert.Ignore("Only a drive letter survives the leading '/' trim; elsewhere '/tmp/x' lands under Assets.");

            Assert.Throws<PathOutsideProjectException>(() =>
                ShaderFunctions.ResolvePaths("Foo", Path.Combine(Path.GetTempPath(), "KitWrightEscape")));
        }

        [Test]
        public void CreateShader_RefusesAPathOutsideAssetsBeforeTouchingTheDisk()
        {
            var escape = "KitWrightEscape_" + Guid.NewGuid().ToString("N");
            var outside = Path.GetFullPath(Path.Combine(ApplicationPaths.ProjectRoot, "..", escape));

            try
            {
                Assert.AreEqual("PATH_OUTSIDE_PROJECT", Code("create_shader", "name", "Foo", "path", "Assets/../../" + escape));
                Assert.IsFalse(Directory.Exists(outside), "create_shader made a folder outside the project before refusing.");
            }
            finally
            {
                if (Directory.Exists(outside))
                    Directory.Delete(outside, true);
            }
        }
    }
}
