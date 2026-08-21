// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Text.RegularExpressions;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class ShaderFunctionsTests
    {
        // Deliberately no shader is created here: importing a .shader compiles its variants, which
        // left the editor still updating for the next fixture (7 EDITOR_BUSY failures) and pushed the
        // suite from 45s to 888s. The trash swap itself is pinned on a plain asset in
        // AssetFunctionsTests.DeleteAsset_*; what is left to check here is the guard and the count.
        [Test]
        public void DeleteShader_MissingFileIsAHardErrorNotASilentSuccess()
        {
            var missing = "Doomed_" + Guid.NewGuid().ToString("N");

            StringAssert.Contains("SHADER_NOT_FOUND",
                ShaderFunctions.DeleteShader(missing, "__KitWrightNoSuchFolder").ToString());
        }

        [Test]
        public void ListShaders_CountCapReportsThePreCapTotalAndTheShownCount()
        {
            // Was written against a project that ships several .shader files (URP). A bare project
            // has one or none, and a cap that truncates nothing prints no "showing" line at all -
            // so read the real total first instead of assuming the host project has one.
            var all = ShaderFunctions.ListShaders().ToString();
            var match = Regex.Match(all, @"Found (\d+) shader file");
            Assert.IsTrue(match.Success, all);
            if (int.Parse(match.Groups[1].Value) < 2)
                Assert.Ignore("Needs at least two shaders in the project for a cap to truncate anything.");

            var capped = ShaderFunctions.ListShaders(count: 1).ToString();

            StringAssert.Contains("showing 1", capped);
            Assert.IsFalse(capped.Contains("Found 1 shader file(s)"),
                "The reported total must be the pre-cap count, not the shown count.");
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
    }
}
