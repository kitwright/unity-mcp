// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.IO;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    // Runs entirely under Path.GetTempPath(): nothing lands in the project, so no .meta or asset residue.
    public sealed class AtomicFileTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "KitWrightAtomicFileTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); }
            catch { }
        }

        [Test]
        public void WriteAllText_CreatesFileWithoutLeavingSiblings()
        {
            var path = Path.Combine(_root, "New.cs");

            AtomicFile.WriteAllText(path, "// one\n");

            Assert.AreEqual("// one\n", File.ReadAllText(path));
            AssertNoSiblings(path);
        }

        [Test]
        public void WriteAllText_ReplacesExistingContentWithoutLeavingSiblings()
        {
            var path = Path.Combine(_root, "Existing.cs");
            AtomicFile.WriteAllText(path, "// one\n");

            AtomicFile.WriteAllText(path, "// two\r\n// three\r\n");

            Assert.AreEqual("// two\r\n// three\r\n", File.ReadAllText(path));
            AssertNoSiblings(path);
        }

        private static void AssertNoSiblings(string path)
        {
            Assert.AreEqual(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)),
                "the swap left a sibling behind");
        }
    }
}
