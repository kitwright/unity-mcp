// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.IO;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class FileWritePreconditionTests
    {
        private string _folder;

        // Under Temp/, not Assets/: these tests only exercise file preconditions, and every write goes
        // through AssetDatabase.Refresh. Creating and deleting an asset folder around that had the
        // project regenerating Assembly-CSharp.csproj while the previous regeneration still held it,
        // and the run failed on "Sharing violation on Assembly-CSharp.csproj" from TearDown - a fault
        // of the fixture, reported against whichever test happened to be finishing.
        [SetUp]
        public void SetUp()
        {
            _folder = "Temp/__KitWrightWritePreconditionTests";
            Directory.CreateDirectory(ProjectPath(_folder));
        }

        [TearDown]
        public void TearDown()
        {
            var folder = ProjectPath(_folder);
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }

        [Test]
        public void WriteFile_RefusesToOverwriteWithoutSha_AndRejectsAStaleOne()
        {
            var path = _folder + "/Probe_" + Guid.NewGuid().ToString("N") + ".txt";

            var created = FileFunctions.WriteFile(path, "original");
            StringAssert.Contains("Written", created);
            Assert.IsFalse(created.Contains("SHA_REQUIRED"), "Creating a new file needs no precondition.");

            var blindOverwrite = FileFunctions.WriteFile(path, "clobbered");
            StringAssert.Contains("SHA_REQUIRED", blindOverwrite);
            Assert.AreEqual("original", File.ReadAllText(ProjectPath(path)),
                "A refused write must leave the file untouched.");

            var sha = ShaOf(path);
            var stale = FileFunctions.WriteFile(path, "clobbered", "0000000000000000000000000000000000000000000000000000000000000000");
            StringAssert.Contains("STALE_FILE", stale);
            Assert.AreEqual("original", File.ReadAllText(ProjectPath(path)));

            var accepted = FileFunctions.WriteFile(path, "updated", sha);
            StringAssert.Contains("Written", accepted);
            Assert.AreEqual("updated", File.ReadAllText(ProjectPath(path)));
        }

        [Test]
        public void ReadFile_IssuesShaOnlyWhenTheWholeFileWasReturned()
        {
            var smallPath = _folder + "/Small_" + Guid.NewGuid().ToString("N") + ".txt";
            FileFunctions.WriteFile(smallPath, "hello");

            var small = Json(FileFunctions.ReadFile(smallPath));
            StringAssert.Contains("\"sha256\":", small);
            StringAssert.Contains("\"content\":\"hello\"", small);
            Assert.IsFalse(small.Contains("\"truncated\""));

            // Over the 10000-char read cap: the tail is missing, so no sha may be issued -
            // one would let a rewrite built from the visible part pass the precondition.
            var bigPath = _folder + "/Big_" + Guid.NewGuid().ToString("N") + ".txt";
            FileFunctions.WriteFile(bigPath, new string('x', 10500));

            var big = Json(FileFunctions.ReadFile(bigPath));
            StringAssert.Contains("\"truncated\":true", big);
            Assert.IsFalse(big.Contains("\"sha256\":"), "A truncated read must not issue a sha.");
            StringAssert.Contains("patch_script", big);
        }

        [Test]
        public void EditScript_RequiresTheShaBeforeItWillReplaceAFile()
        {
            // Deliberately not a .cs file: dropping one under Assets/ triggers a script
            // compilation and domain reload, which kills the test run this assertion is in.
            // edit_script's precondition does not care about the extension.
            var path = _folder + "/Script_" + Guid.NewGuid().ToString("N") + ".txt";
            var original = "public class Probe { }";
            FileFunctions.WriteFile(path, original);

            var noSha = CodeFunctions.EditScript(path, "public class Probe { void Added() { } }", null);
            StringAssert.Contains("SHA_REQUIRED", noSha);
            Assert.AreEqual(original, File.ReadAllText(ProjectPath(path)));

            var applied = CodeFunctions.EditScript(path, "public class Probe { void Added() { } }", ShaOf(path));
            StringAssert.Contains("Updated script", applied);
            StringAssert.Contains("Added", File.ReadAllText(ProjectPath(path)));
        }

        private static string ProjectPath(string assetPath)
        {
            return Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? string.Empty, assetPath);
        }

        private static string ShaOf(string assetPath)
        {
            return CodeFunctions.ComputeSha256(File.ReadAllText(ProjectPath(assetPath)));
        }

        private static string Json(object result)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(result);
        }
    }
}
