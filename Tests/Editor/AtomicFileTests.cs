// Copyright (C) KitWright. All rights reserved.

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
        public void WriteAllText_ToAPathThatDoesNotExistYet_LogsNothing()
        {
            var logged = new System.Collections.Generic.List<string>();
            UnityEngine.Application.LogCallback capture = (message, stack, type) => logged.Add(type + ": " + message);

            UnityEngine.Application.logMessageReceived += capture;
            try
            {
                AtomicFile.WriteAllText(Path.Combine(_root, "Fresh.cs"), "// one\n");
            }
            finally
            {
                UnityEngine.Application.logMessageReceived -= capture;
            }

            CollectionAssert.IsEmpty(logged);
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

        [Test]
        public void WriteAllText_LeavesAnUnrelatedTmpSiblingAlone()
        {
            var path = Path.Combine(_root, "Player.cs");
            var neighbour = path + ".tmp";
            AtomicFile.WriteAllText(path, "// one\n");
            File.WriteAllText(neighbour, "// someone else's draft\n");

            AtomicFile.WriteAllText(path, "// two\n");

            Assert.AreEqual("// two\n", File.ReadAllText(path));
            Assert.AreEqual("// someone else's draft\n", File.ReadAllText(neighbour),
                "the swap wrote through a file that was never ours");
        }

        [Test]
        public void WriteAllText_KeepsABomTheFileHadAndAddsNoneToANewFile()
        {
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            var withBom = Path.Combine(_root, "WithBom.cs");
            File.WriteAllBytes(withBom, bom);

            AtomicFile.WriteAllText(withBom, "// two\n");

            var written = File.ReadAllBytes(withBom);
            CollectionAssert.AreEqual(bom, new[] { written[0], written[1], written[2] });
            Assert.AreEqual("// two\n", File.ReadAllText(withBom));

            var fresh = Path.Combine(_root, "Fresh.cs");
            AtomicFile.WriteAllText(fresh, "// one\n");

            Assert.AreEqual((byte)'/', File.ReadAllBytes(fresh)[0]);
        }

        private static void AssertNoSiblings(string path)
        {
            Assert.AreEqual(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)),
                "the swap left a sibling behind");
        }
    }
}
