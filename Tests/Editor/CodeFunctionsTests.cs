// Copyright (C) KitWright. Licensed under MIT.

using System.IO;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class CodeFunctionsTests
    {
        // Under Temp/ rather than Assets/: a .cs dropped into Assets/ triggers a compile and
        // domain reload, which kills the test run this assertion is in.
        private const string Folder = "Temp/__KitWrightCodeFunctionsTests";

        private const string Sound = "public class Probe { void A() { } }";

        [SetUp]
        public void SetUp()
        {
            Directory.CreateDirectory(FullPath(string.Empty));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(FullPath(string.Empty)))
                Directory.Delete(FullPath(string.Empty), true);
        }

        [Test]
        public void ComputeSha256_DeterministicAndLowercase()
        {
            var a = CodeFunctions.ComputeSha256("hello");
            var b = CodeFunctions.ComputeSha256("hello");
            Assert.AreEqual(a, b);
            Assert.AreEqual(64, a.Length);
            Assert.AreEqual(a.ToLowerInvariant(), a);
        }

        [Test]
        public void ComputeSha256_DifferentContentDifferentHash()
        {
            Assert.AreNotEqual(CodeFunctions.ComputeSha256("a"), CodeFunctions.ComputeSha256("b"));
        }

        [Test]
        public void ComputeSha256_NullTreatedAsEmpty()
        {
            Assert.AreEqual(CodeFunctions.ComputeSha256(""), CodeFunctions.ComputeSha256(null));
        }

        [Test]
        public void CreateScript_RefusesAnExistingFileAndLeavesItUntouched()
        {
            var original = "public class Probe { }";
            File.WriteAllText(FullPath("Probe.cs"), original);

            var refused = CodeFunctions.CreateScript("Probe", "public class Probe { void Clobbered() { } }", Folder);

            StringAssert.Contains("SCRIPT_EXISTS", refused);
            StringAssert.Contains("edit_script", refused);
            StringAssert.Contains("patch_script", refused);
            Assert.AreEqual(original, File.ReadAllText(FullPath("Probe.cs")),
                "A refused create must leave the file untouched.");
        }

        [Test]
        public void EditScript_RejectsAnIntroducedNonBraceError_ButNotAPreexistingOne()
        {
            // Braces balance in all three versions, so a brace count alone cannot tell them apart.
            var sound = "public class Probe { void A() { B(1); } }";
            var broken = "public class Probe { void A() { B(1; } }";

            var soundPath = Folder + "/Sound.txt";
            File.WriteAllText(FullPath("Sound.txt"), sound);
            var rejected = CodeFunctions.EditScript(soundPath, broken, CodeFunctions.ComputeSha256(sound));
            StringAssert.Contains("SYNTAX_REGRESSION", rejected);
            Assert.AreEqual(sound, File.ReadAllText(FullPath("Sound.txt")));

            var brokenPath = Folder + "/Broken.txt";
            File.WriteAllText(FullPath("Broken.txt"), broken);
            var stillBroken = "public class Probe { void A() { B(2; } }";
            var applied = CodeFunctions.EditScript(brokenPath, stillBroken, CodeFunctions.ComputeSha256(broken));
            StringAssert.Contains("Updated script", applied);
            Assert.AreEqual(stillBroken, File.ReadAllText(FullPath("Broken.txt")));
        }

        [Test]
        public void PatchScript_RefusesAnEditThatUnbalancesBraces()
        {
            var path = Write("Braces.txt", Sound);

            var refused = CodeFunctions.PatchScript(path, "{ } }", "{ }");

            StringAssert.Contains("SYNTAX_REGRESSION", refused);
            StringAssert.Contains("never closed", refused);
            Assert.AreEqual(Sound, Read("Braces.txt"), "A refused patch must leave the file untouched.");
        }

        // Both problems blank the rest of the file when masked, so a brace count would report the
        // wrong line; the payload has to name the literal that is still open.
        [Test]
        public void PatchScript_RefusesAnUnterminatedStringOrCharLiteral_AndNamesIt()
        {
            var stringPath = Write("Verbatim.txt", Sound);
            var refusedString = CodeFunctions.PatchScript(stringPath, "{ }", "{ string s = @\"open; }");

            StringAssert.Contains("SYNTAX_REGRESSION", refusedString);
            StringAssert.Contains("Unterminated verbatim string", refusedString);
            Assert.AreEqual(Sound, Read("Verbatim.txt"));

            var charPath = Write("Char.txt", Sound);
            var refusedChar = CodeFunctions.PatchScript(charPath, "{ }", "{ char c = 'x; }");

            StringAssert.Contains("SYNTAX_REGRESSION", refusedChar);
            StringAssert.Contains("Unterminated char literal", refusedChar);
            Assert.AreEqual(Sound, Read("Char.txt"));
        }

        [Test]
        public void PatchScript_RefusesAnEditThatMismatchesABracket()
        {
            const string call = "public class Probe { void A() { B(1); } }";
            var path = Write("Bracket.txt", call);

            var refused = CodeFunctions.PatchScript(path, "B(1)", "B(1]");

            StringAssert.Contains("SYNTAX_REGRESSION", refused);
            StringAssert.Contains("expected ')'", refused);
            Assert.AreEqual(call, Read("Bracket.txt"));
        }

        // The rule is a regression check, not an absolute one: an edit that leaves a file no more
        // broken than it found it still has to go through, or a mid-refactor file cannot be fixed.
        [Test]
        public void PatchScript_WritesAnEditThatLeavesAnAlreadyBrokenFileEquallyBroken()
        {
            const string broken = "public class Probe { void A() { B(1; } }";
            var path = Write("AlreadyBroken.txt", broken);

            var applied = CodeFunctions.PatchScript(path, "B(1;", "B(2;");

            StringAssert.Contains("Patched script", applied);
            Assert.AreEqual("public class Probe { void A() { B(2; } }", Read("AlreadyBroken.txt"));
        }

        // A patch whose replacement equals what it matched is not special-cased: it reports the
        // replacement it made and rewrites the same bytes.
        [Test]
        public void PatchScript_ANoOpEditIsReportedAsAppliedAndLeavesTheBytesIdentical()
        {
            var path = Write("NoOp.txt", Sound);

            var result = CodeFunctions.PatchScript(path, "void A()", "void A()");

            StringAssert.Contains("Patched script", result);
            StringAssert.Contains("Replaced first occurrence (of 1 total)", result);
            StringAssert.Contains(CodeFunctions.ComputeSha256(Sound), result);
            Assert.AreEqual(Sound, Read("NoOp.txt"));
        }

        [Test]
        public void EditScript_RequiresAShaAndRejectsAStaleOne()
        {
            var path = Write("Sha.txt", Sound);
            const string rewrite = "public class Probe { void A() { B(); } }";

            var missing = CodeFunctions.EditScript(path, rewrite, null);
            StringAssert.Contains("SHA_REQUIRED", missing);
            Assert.AreEqual(Sound, Read("Sha.txt"));

            var stale = CodeFunctions.EditScript(path, rewrite, CodeFunctions.ComputeSha256("some older content"));
            StringAssert.Contains("STALE_FILE", stale);
            Assert.AreEqual(Sound, Read("Sha.txt"));

            var applied = CodeFunctions.EditScript(path, rewrite, CodeFunctions.ComputeSha256(Sound));
            StringAssert.Contains("Updated script", applied);
            Assert.AreEqual(rewrite, Read("Sha.txt"));
        }

        [Test]
        public void PatchScript_RejectsAStaleShaButAcceptsTheCurrentOne()
        {
            var path = Write("PatchSha.txt", Sound);

            var stale = CodeFunctions.PatchScript(path, "void A()", "void Renamed()",
                expected_sha256: CodeFunctions.ComputeSha256("some older content"));
            StringAssert.Contains("STALE_FILE", stale);
            Assert.AreEqual(Sound, Read("PatchSha.txt"));

            var applied = CodeFunctions.PatchScript(path, "void A()", "void Renamed()",
                expected_sha256: CodeFunctions.ComputeSha256(Sound));
            StringAssert.Contains("Patched script", applied);
            StringAssert.Contains("void Renamed()", Read("PatchSha.txt"));
        }

        private static string Write(string fileName, string content)
        {
            File.WriteAllText(FullPath(fileName), content);
            return Folder + "/" + fileName;
        }

        private static string Read(string fileName) => File.ReadAllText(FullPath(fileName));

        private static string FullPath(string fileName)
        {
            var root = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            return Path.Combine(root, Folder, fileName);
        }
    }
}
