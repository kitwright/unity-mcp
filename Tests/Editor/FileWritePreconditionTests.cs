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
        public void ReadFile_ReadsALongFileInPartsAndSaysWhyItCannotShowAFolderOrABinary()
        {
            var longPath = _folder + "/Long_" + Guid.NewGuid().ToString("N") + ".txt";
            FileFunctions.WriteFile(longPath, new string('a', 10000) + new string('b', 500));

            var first = Newtonsoft.Json.Linq.JObject.Parse(Json(FileFunctions.ReadFile(longPath)))["data"];
            Assert.AreEqual(10000, (int)first["next_offset"]);

            var rest = Newtonsoft.Json.Linq.JObject.Parse(Json(FileFunctions.ReadFile(longPath, 10000)))["data"];
            Assert.AreEqual(new string('b', 500), (string)rest["content"]);
            Assert.IsTrue(rest["next_offset"] == null || rest["next_offset"].Type == Newtonsoft.Json.Linq.JTokenType.Null, "null means there is no more");
            Assert.IsNull(rest["sha256"], "a part of a file must not issue a sha");

            StringAssert.Contains("PATH_IS_DIRECTORY", Json(FileFunctions.ReadFile(_folder)));

            var binaryPath = _folder + "/Binary_" + Guid.NewGuid().ToString("N") + ".bin";
            File.WriteAllBytes(ProjectPath(binaryPath), new byte[] { 0x89, 0x50, 0x00, 0x0D, 0x41 });
            StringAssert.Contains("BINARY_FILE", Json(FileFunctions.ReadFile(binaryPath)));
        }

        [Test]
        public void ReadFile_NeverSplitsASurrogatePairAcrossParts_AndRefusesAnOffsetOutsideTheFile()
        {
            var path = _folder + "/Emoji_" + Guid.NewGuid().ToString("N") + ".txt";
            var text = new string('a', 9999) + "\U0001F600" + new string('b', 20);
            FileFunctions.WriteFile(path, text);

            var first = Newtonsoft.Json.Linq.JObject.Parse(Json(FileFunctions.ReadFile(path)))["data"];
            var next = (int)first["next_offset"];
            Assert.AreEqual(9999, next, "the emoji starts at 9999 and must move whole to the next part");

            var second = Newtonsoft.Json.Linq.JObject.Parse(Json(FileFunctions.ReadFile(path, next)))["data"];
            Assert.AreEqual((string)first["content"] + (string)second["content"], text);

            StringAssert.Contains("INVALID_PARAM", Json(FileFunctions.ReadFile(path, 10000)));
            StringAssert.Contains("INVALID_PARAM", Json(FileFunctions.ReadFile(path, -5)));
            StringAssert.Contains("INVALID_PARAM", Json(FileFunctions.ReadFile(path, text.Length + 1)));
        }

        [Test]
        public void WriteFile_RewritesAFileTooLongForOneReadWithTheShaFromGetScriptSha()
        {
            // read_file issues no sha for a part, so a file read whole in parts is rewritten with
            // get_script_sha's, which proves the same thing: the file has not changed since.
            var path = _folder + "/Long_" + Guid.NewGuid().ToString("N") + ".txt";
            FileFunctions.WriteFile(path, new string('x', 10500));

            var sha = (string)Newtonsoft.Json.Linq.JObject.Parse(Json(CodeFunctions.GetScriptSha(path)))["data"]["sha256"];
            var applied = FileFunctions.WriteFile(path, "short", sha);

            StringAssert.Contains("Written", applied);
            Assert.AreEqual("short", File.ReadAllText(ProjectPath(path)));
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
