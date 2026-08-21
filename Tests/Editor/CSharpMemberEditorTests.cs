// Copyright (C) KitWright. Licensed under MIT.

using System.Collections.Generic;
using KitWright.Editor.Tools.Scripting;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class CSharpMemberEditorTests
    {
        // Deliberately hostile: 'Jump' also appears in a comment, in an attribute's string argument,
        // and as a call inside another method. Only the declaration should ever be found.
        // Normalised because a verbatim string carries this file's own line endings, and the
        // asserts below are written with '\n' -- on a CRLF checkout they would all miss.
        private static readonly string Source = @"using UnityEngine;

public class Player : MonoBehaviour
{
    public int health = 10;

    // Jump is the interesting one.
    [ContextMenu(""Jump"")]
    public void Jump()
    {
        Debug.Log(""{ not a real brace }"");
    }

    public void Land() => Debug.Log(""landed"");

    void Update()
    {
        if (health > 0) { Jump(); }
    }
}
".Replace("\r\n", "\n");

        private static CSharpMemberEditor.EditOutcome Apply(params CSharpMemberEditor.MemberEdit[] edits) =>
            CSharpMemberEditor.Apply(Source, new List<CSharpMemberEditor.MemberEdit>(edits));

        private static CSharpMemberEditor.MemberEdit Edit(
            string op, string method = null, string replacement = null, string position = null, string anchor = null) =>
            new CSharpMemberEditor.MemberEdit
            {
                Op = op,
                ClassName = "Player",
                MethodName = method,
                Replacement = replacement,
                Position = position,
                AnchorMethod = anchor
            };

        [Test]
        public void ReplaceMethod_SwapsTheBodyAndLeavesNeighboursIntact()
        {
            var result = Apply(Edit(CSharpMemberEditor.OpReplace, "Jump", "public void Jump()\n{\n    Debug.Log(\"up\");\n}"));

            Assert.IsTrue(result.Success, result.Message);
            StringAssert.Contains("Debug.Log(\"up\")", result.Source);
            Assert.IsFalse(result.Source.Contains("not a real brace"), "Old body survived the replacement.");
            StringAssert.Contains("public void Land()", result.Source);
            StringAssert.Contains("public int health = 10;", result.Source);
            StringAssert.Contains("if (health > 0) { Jump(); }", result.Source);
        }

        // The attribute and the comment above a method are part of it; leaving them behind would
        // duplicate whatever the replacement carries.
        [Test]
        public void ReplaceMethod_TakesTheAttributeAndCommentAboveItToo()
        {
            var result = Apply(Edit(CSharpMemberEditor.OpReplace, "Jump", "public void Jump() { }"));

            Assert.IsTrue(result.Success, result.Message);
            Assert.IsFalse(result.Source.Contains("ContextMenu"), "Attribute was left behind.");
            Assert.IsFalse(result.Source.Contains("Jump is the interesting one"), "Comment was left behind.");
        }

        [Test]
        public void ReplaceMethod_ReindentsTheReplacementToTheMembersNesting()
        {
            var result = Apply(Edit(CSharpMemberEditor.OpReplace, "Jump", "public void Jump()\n{\n    return;\n}"));

            Assert.IsTrue(result.Success, result.Message);
            StringAssert.Contains("\n    public void Jump()\n    {\n        return;\n    }", result.Source);
        }

        [Test]
        public void ReplaceMethod_HandlesAnExpressionBodiedMember()
        {
            var result = Apply(Edit(CSharpMemberEditor.OpReplace, "Land", "public void Land() => Debug.Log(\"thud\");"));

            Assert.IsTrue(result.Success, result.Message);
            StringAssert.Contains("thud", result.Source);
            Assert.IsFalse(result.Source.Contains("landed"), "Old expression body survived.");
            StringAssert.Contains("void Update()", result.Source);
        }

        [Test]
        public void DeleteMethod_RemovesItAndItsSurroundingBlankLine()
        {
            var result = Apply(Edit(CSharpMemberEditor.OpDelete, "Jump"));

            Assert.IsTrue(result.Success, result.Message);
            Assert.IsFalse(result.Source.Contains("public void Jump()"), "Method survived the delete.");
            Assert.IsFalse(result.Source.Contains("ContextMenu"));
            StringAssert.Contains("public int health = 10;\n\n    public void Land()", result.Source);
        }

        [Test]
        public void InsertMethod_AtEndLandsInsideTheClassBeforeItsClosingBrace()
        {
            var result = Apply(Edit(CSharpMemberEditor.OpInsert, replacement: "void Reset() { }", position: "end"));

            Assert.IsTrue(result.Success, result.Message);
            StringAssert.Contains("void Update()", result.Source);
            StringAssert.Contains("    void Reset() { }\n}", result.Source);
            Assert.IsNull(CSharpSyntaxCheck.FindProblem(result.Source));
        }

        [Test]
        public void InsertMethod_AfterAnAnchorLandsBetweenItAndTheNextMember()
        {
            var result = Apply(Edit(CSharpMemberEditor.OpInsert, replacement: "void Hover() { }", position: "after", anchor: "Jump"));

            Assert.IsTrue(result.Success, result.Message);
            var hover = result.Source.IndexOf("void Hover()", System.StringComparison.Ordinal);
            var jump = result.Source.IndexOf("public void Jump()", System.StringComparison.Ordinal);
            var land = result.Source.IndexOf("public void Land()", System.StringComparison.Ordinal);
            Assert.Greater(hover, jump);
            Assert.Less(hover, land);
        }

        [Test]
        public void InsertMethod_BeforeAnAnchorLandsAboveItsAttributes()
        {
            var result = Apply(Edit(CSharpMemberEditor.OpInsert, replacement: "void Crouch() { }", position: "before", anchor: "Jump"));

            Assert.IsTrue(result.Success, result.Message);
            var crouch = result.Source.IndexOf("void Crouch()", System.StringComparison.Ordinal);
            var health = result.Source.IndexOf("public int health", System.StringComparison.Ordinal);
            var jump = result.Source.IndexOf("public void Jump()", System.StringComparison.Ordinal);
            Assert.Greater(crouch, health);
            Assert.Less(crouch, jump);
        }

        [Test]
        public void Edits_AreAppliedInOrderAgainstTheResultOfTheOneBefore()
        {
            var result = Apply(
                Edit(CSharpMemberEditor.OpDelete, "Land"),
                Edit(CSharpMemberEditor.OpInsert, replacement: "public void Land() { }", position: "end"));

            Assert.IsTrue(result.Success, result.Message);
            StringAssert.Contains("public void Land() { }", result.Source);
            Assert.IsFalse(result.Source.Contains("landed"), "The original Land survived.");
        }

        // A name cannot pick an overload, so the tool has to say so rather than guess.
        [Test]
        public void OverloadedMethod_IsRejectedWithTheSignatures()
        {
            const string overloaded = @"public class Calc
{
    public int Add(int a) { return a; }
    public int Add(int a, int b) { return a + b; }
}";

            var result = CSharpMemberEditor.Apply(overloaded, new List<CSharpMemberEditor.MemberEdit>
            {
                new CSharpMemberEditor.MemberEdit
                {
                    Op = CSharpMemberEditor.OpReplace, ClassName = "Calc", MethodName = "Add", Replacement = "public int Add() { return 0; }"
                }
            });

            Assert.IsFalse(result.Success);
            Assert.AreEqual("AMBIGUOUS_METHOD", result.ErrorCode);
            Assert.AreEqual(2, result.Candidates.Length);
        }

        // A field holding a lambda that calls the method reads like a declaration to a text scan.
        [Test]
        public void FieldInitialiserCallingTheSameName_IsNotMistakenForTheDeclaration()
        {
            const string tricky = @"using System;

public class Trap
{
    Action shortcut = () => Fire();

    void Fire()
    {
        Console.WriteLine(""boom"");
    }
}";

            var result = CSharpMemberEditor.Apply(tricky, new List<CSharpMemberEditor.MemberEdit>
            {
                new CSharpMemberEditor.MemberEdit
                {
                    Op = CSharpMemberEditor.OpReplace, ClassName = "Trap", MethodName = "Fire", Replacement = "void Fire() { }"
                }
            });

            Assert.IsTrue(result.Success, result.Message);
            StringAssert.Contains("Action shortcut = () => Fire();", result.Source);
            Assert.IsFalse(result.Source.Contains("boom"), "The declaration was not the span that got replaced.");
        }

        [Test]
        public void MissingMethod_ReportsTheNamesThatAreThere()
        {
            var result = Apply(Edit(CSharpMemberEditor.OpReplace, "Sprint", "void Sprint() { }"));

            Assert.IsFalse(result.Success);
            Assert.AreEqual("METHOD_NOT_FOUND", result.ErrorCode);
            CollectionAssert.Contains(result.Candidates, "Jump");
            CollectionAssert.Contains(result.Candidates, "Update");
        }

        [Test]
        public void MissingType_ReportsTheTypesThatAreThere()
        {
            var result = CSharpMemberEditor.Apply(Source, new List<CSharpMemberEditor.MemberEdit>
            {
                new CSharpMemberEditor.MemberEdit
                {
                    Op = CSharpMemberEditor.OpReplace, ClassName = "Enemy", MethodName = "Jump", Replacement = "void Jump() { }"
                }
            });

            Assert.IsFalse(result.Success);
            Assert.AreEqual("TYPE_NOT_FOUND", result.ErrorCode);
            CollectionAssert.Contains(result.Candidates, "Player");
        }

        [Test]
        public void FailedEditNamesWhichEditFailed()
        {
            var result = Apply(
                Edit(CSharpMemberEditor.OpDelete, "Jump"),
                Edit(CSharpMemberEditor.OpDelete, "Sprint"));

            Assert.IsFalse(result.Success);
            StringAssert.Contains("Edit 2 of 2", result.Message);
        }

        // Edits are assembled with '\n'; on a CRLF file that would leave the edited member as the
        // only LF lines in it, and every diff tool would flag them.
        [Test]
        public void CrlfFile_StaysCrlfAfterAnEdit()
        {
            var crlf = Source.Replace("\n", "\r\n");

            var result = CSharpMemberEditor.Apply(crlf, new List<CSharpMemberEditor.MemberEdit>
            {
                new CSharpMemberEditor.MemberEdit
                {
                    Op = CSharpMemberEditor.OpReplace, ClassName = "Player", MethodName = "Jump",
                    Replacement = "public void Jump()\n{\n    return;\n}"
                }
            });

            Assert.IsTrue(result.Success, result.Message);
            StringAssert.DoesNotMatch(@"(?<!\r)\n", result.Source);
            StringAssert.Contains("\r\n    public void Jump()\r\n    {\r\n        return;\r\n    }", result.Source);
        }

        [Test]
        public void Mask_BlanksLiteralsAndCommentsWithoutMovingAnyIndex()
        {
            const string source = "var a = \"}{\"; // }\nint b;";
            var mask = CSharpMemberEditor.Mask(source);

            Assert.AreEqual(source.Length, mask.Length);
            Assert.IsFalse(mask.Contains("}"), "A brace inside a string or comment reached the mask.");
            StringAssert.Contains("int b;", mask);
        }
    }

    public sealed class CSharpSyntaxCheckTests
    {
        [Test]
        public void SoundSource_ReportsNoProblem()
        {
            Assert.IsNull(CSharpSyntaxCheck.FindProblem("class A { void B() { if (true) { } } }"));
        }

        [Test]
        public void UnclosedBrace_ReportsTheLineItOpenedOn()
        {
            var problem = CSharpSyntaxCheck.FindProblem("class A\n{\n    void B()\n    {\n}");

            Assert.IsNotNull(problem);
            StringAssert.Contains("never closed", problem);
        }

        [Test]
        public void MismatchedPair_NamesTheOneThatWasExpected()
        {
            var problem = CSharpSyntaxCheck.FindProblem("class A { void B(] { } }");

            Assert.IsNotNull(problem);
            StringAssert.Contains("expected ')'", problem);
        }

        [Test]
        public void StrayCloser_IsReportedRatherThanIgnored()
        {
            var problem = CSharpSyntaxCheck.FindProblem("class A { }\n}");

            Assert.IsNotNull(problem);
            StringAssert.Contains("closes nothing", problem);
        }

        [Test]
        public void UnterminatedBlockComment_IsNamedInsteadOfSurfacingAsABraceCount()
        {
            var problem = CSharpSyntaxCheck.FindProblem("class A { }\n/* still open");

            Assert.IsNotNull(problem);
            StringAssert.Contains("Unterminated block comment", problem);
        }

        [Test]
        public void BracesInsideLiteralsDoNotCount()
        {
            Assert.IsNull(CSharpSyntaxCheck.FindProblem("class A { string s = \"}\"; char c = '}'; }"));
        }

        [Test]
        public void ValidSourceIsNotReportedBrokenByAnApostropheOrARawString()
        {
            Assert.IsNull(CSharpSyntaxCheck.FindProblem(
                "class A\n{\n    #region Don't touch\n    void M() { }\n    #endregion\n}\n"),
                "A stray apostrophe opened a char literal that the newline never closed, blanking the rest of the file.");

            Assert.IsNull(CSharpSyntaxCheck.FindProblem(
                "class A\n{\n    const string S = \"\"\"\n    if (x) {\n    \"\"\";\n    void M() { }\n}\n"),
                "A raw string literal's body leaked into the mask as real code.");

            Assert.IsNull(CSharpSyntaxCheck.FindProblem("class A { string s = @\"\"\"\"; }"),
                "Four quotes after '@' are a verbatim string holding one quote, not a raw string opener.");
        }

        [Test]
        public void UnterminatedRawString_IsNamedInsteadOfSurfacingAsABraceCount()
        {
            var problem = CSharpSyntaxCheck.FindProblem("class A\n{\n    const string S = \"\"\"\n    still open\n");

            Assert.IsNotNull(problem);
            StringAssert.Contains("Unterminated raw string", problem);
        }

        [Test]
        public void UnterminatedVerbatimString_IsNamedInsteadOfSurfacingAsABraceCount()
        {
            var problem = CSharpSyntaxCheck.FindProblem("class A\n{\n    const string S = @\"still open\n");

            Assert.IsNotNull(problem);
            StringAssert.Contains("Unterminated verbatim string", problem);
        }

        [Test]
        public void UnterminatedCharLiteral_IsNamedInsteadOfSurfacingAsABraceCount()
        {
            var problem = CSharpSyntaxCheck.FindProblem("class A { char c = 'x; }");

            Assert.IsNotNull(problem);
            StringAssert.Contains("Unterminated char literal", problem);
        }
    }
}
