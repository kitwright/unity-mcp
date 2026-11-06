// Copyright (C) KitWright. Licensed under MIT.

using System.IO;
using System.Linq;
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
        public void WriteFile_RefusesToOverwriteAnExistingScriptItWasNotShownAndLeavesItUntouched()
        {
            var original = "public class Probe { }";
            var path = Write("Probe.cs", original);

            var refused = FileFunctions.WriteFile(path, "public class Probe { void Clobbered() { } }");

            StringAssert.Contains("SHA_REQUIRED", refused);
            StringAssert.Contains("patch_script", refused);
            Assert.AreEqual(original, Read("Probe.cs"), "A refused write must leave the file untouched.");
        }

        [Test]
        public void WriteFile_CreatesANewScriptWithoutASha()
        {
            var path = Folder + "/Sub/Created.cs";

            StringAssert.Contains("Written", FileFunctions.WriteFile(path, Sound));

            Assert.AreEqual(Sound, Read("Sub/Created.cs"));
        }

        [TestCase("a\nb\n", "x\ny\n", "a\nb\n")]
        [TestCase("a\r\nb\r\n", "x\r\ny\r\n", "a\r\nb\r\n")]
        [TestCase("x\ny\n", "a\r\nb\r\n", "x\r\ny\r\n")]
        [TestCase("x\r\ny\r\n", "a\nb\n", "x\ny\n")]
        [TestCase("x\ny\r\n", "a\r\nb\nc\n", "x\ny\r\n")]
        [TestCase("no newline", "a\r\nb\r\n", "no newline")]
        public void MatchLineEndings_FollowsTheFileAndLeavesAMixedFileAlone(string text, string reference, string expected)
        {
            Assert.AreEqual(expected, CodeFunctions.MatchLineEndings(text, reference));
        }

        [Test]
        public void PatchScript_MatchesAMultiLineOldTextAgainstACrlfFile()
        {
            var original = "class A\r\n{\r\n    int x;\r\n    int y;\r\n}\r\n";
            var path = Write("Crlf.txt", original);

            var patched = CodeFunctions.PatchScript(path, "    int x;\n    int y;", "    int x;\n    int z;\n    int y;");

            StringAssert.Contains("Patched script", patched);
            Assert.AreEqual("class A\r\n{\r\n    int x;\r\n    int z;\r\n    int y;\r\n}\r\n", Read("Crlf.txt"));
        }

        [Test]
        public void WriteFile_KeepsAScriptsLineEndings()
        {
            var original = "class A\r\n{\r\n}\r\n";
            var path = Write("Rewrite.cs", original);

            FileFunctions.WriteFile(path, "class A\n{\n    int x;\n}\n", CodeFunctions.ComputeSha256(original));

            Assert.AreEqual("class A\r\n{\r\n    int x;\r\n}\r\n", Read("Rewrite.cs"));
        }

        [Test]
        public void PatchScript_KeepsTheBomOfTheFileItEdits()
        {
            var path = Write("Bom.txt", Sound);
            File.WriteAllBytes(FullPath("Bom.txt"),
                new System.Text.UTF8Encoding(true).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(Sound)).ToArray());

            CodeFunctions.PatchScript(path, "void A()", "void B()");

            var bytes = File.ReadAllBytes(FullPath("Bom.txt"));
            CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        }

        [Test]
        public void WriteFile_RejectsAnIntroducedNonBraceErrorInAScript_ButNotAPreexistingOne()
        {
            // Braces balance in all three versions, so a brace count alone cannot tell them apart.
            var sound = "public class Probe { void A() { B(1); } }";
            var broken = "public class Probe { void A() { B(1; } }";

            var soundPath = Write("Sound.cs", sound);
            var rejected = FileFunctions.WriteFile(soundPath, broken, CodeFunctions.ComputeSha256(sound));
            StringAssert.Contains("SYNTAX_REGRESSION", rejected);
            Assert.AreEqual(sound, Read("Sound.cs"));

            var brokenPath = Write("Broken.cs", broken);
            var stillBroken = "public class Probe { void A() { B(2; } }";
            var applied = FileFunctions.WriteFile(brokenPath, stillBroken, CodeFunctions.ComputeSha256(broken));
            StringAssert.Contains("Written", applied);
            Assert.AreEqual(stillBroken, Read("Broken.cs"));
        }

        // Only C# is checked: a shader or a text file that is not C# would trip the checker on sound content.
        [Test]
        public void WriteFile_DoesNotSyntaxCheckAFileThatIsNotCSharp()
        {
            const string sound = "Shader \"Probe\" { }";
            const string unbalanced = "Shader \"Probe\" { SubShader {";
            var path = Write("Probe.shader", sound);

            var applied = FileFunctions.WriteFile(path, unbalanced, CodeFunctions.ComputeSha256(sound));

            StringAssert.Contains("Written", applied);
            Assert.AreEqual(unbalanced, Read("Probe.shader"));
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
        public void WriteFile_RequiresAShaForAScriptAndRejectsAStaleOne()
        {
            var path = Write("Sha.cs", Sound);
            const string rewrite = "public class Probe { void A() { B(); } }";

            var missing = FileFunctions.WriteFile(path, rewrite, null);
            StringAssert.Contains("SHA_REQUIRED", missing);
            Assert.AreEqual(Sound, Read("Sha.cs"));

            var stale = FileFunctions.WriteFile(path, rewrite, CodeFunctions.ComputeSha256("some older content"));
            StringAssert.Contains("STALE_FILE", stale);
            Assert.AreEqual(Sound, Read("Sha.cs"));

            var applied = FileFunctions.WriteFile(path, rewrite, CodeFunctions.ComputeSha256(Sound));
            StringAssert.Contains("Written", applied);
            Assert.AreEqual(rewrite, Read("Sha.cs"));
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

        [Test]
        public void PatchScript_RefusesAnEmptyOldTextInsteadOfLoopingForever()
        {
            var path = Write("EmptyNeedle.txt", Sound);

            var refused = CodeFunctions.PatchScript(path, "", "x");

            StringAssert.Contains("EMPTY_OLD_TEXT", refused);
            Assert.AreEqual(Sound, Read("EmptyNeedle.txt"));
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
