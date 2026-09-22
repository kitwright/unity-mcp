// Copyright (C) KitWright. All rights reserved.

using System;
using System.Reflection;
using KitWright.Editor.Tools.Scripting;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class LoopGuardInjectorTests
    {
        private static string Inject(string source)
        {
            var type = typeof(LoopBudgetGuard).Assembly.GetType("KitWright.Editor.Tools.Scripting.LoopGuardInjector");
            var method = type.GetMethod("Inject", BindingFlags.Public | BindingFlags.Static);
            return (string)method.Invoke(null, new object[] { source });
        }

        private const string Check = "global::KitWright.Editor.Tools.Scripting.LoopBudgetGuard.Check(";

        [Test]
        public void WrapsWhileCondition()
        {
            Assert.AreEqual("while (" + Check + "i < 10)) { }", Inject("while (i < 10) { }"));
        }

        [Test]
        public void WrapsForCondition()
        {
            Assert.AreEqual("for (var i = 0;" + Check + " i < n); i++) { }",
                Inject("for (var i = 0; i < n; i++) { }"));
        }

        [Test]
        public void EmptyForConditionBecomesConstantCheck()
        {
            Assert.AreEqual("for (;" + Check + "true);) { }", Inject("for (;;) { }"));
        }

        [Test]
        public void GuardsDoWhileThroughItsCondition()
        {
            Assert.AreEqual("do { } while (" + Check + "keep));", Inject("do { } while (keep);"));
        }

        [Test]
        public void GuardsSingleStatementBodyWithoutTouchingIt()
        {
            Assert.AreEqual("while (" + Check + "x)) i++;", Inject("while (x) i++;"));
        }

        [Test]
        public void LeavesForeachAlone()
        {
            const string source = "foreach (var x in items) { }";
            Assert.AreEqual(source, Inject(source));
        }

        [Test]
        public void IgnoresKeywordsInsideLiteralsAndComments()
        {
            const string source = "var s = \"while (true)\"; // for (;;)\n/* while (x) */ var v = @\"for (;;)\";";
            Assert.AreEqual(source, Inject(source));
        }

        // @$"" is as legal as $@"" and the scanner used to read only the latter, so the @$"" case
        // fell through as "not a string" and the keyword inside it was rewritten as code.
        [Test]
        public void IgnoresKeywordsInsideEveryInterpolatedVerbatimSpelling()
        {
            const string source = "var a = $@\"while (x)\"; var b = @$\"for (;;)\"; var c = $\"while (y)\";";
            Assert.AreEqual(source, Inject(source));
        }

        [Test]
        public void IgnoresIdentifiersThatMerelyStartWithAKeyword()
        {
            const string source = "var formatter = whileHelper(2);";
            Assert.AreEqual(source, Inject(source));
        }

        [Test]
        public void GuardsNestedLoops()
        {
            var result = Inject("while (a) { for (i = 0; i < n; i++) { } }");
            Assert.AreEqual(2, CountOccurrences(result, Check));
        }

        [Test]
        public void SkipsSemicolonsNestedInsideTheForHeader()
        {
            var result = Inject("for (var i = f(() => { g(); }); i < n; i++) { }");
            Assert.IsTrue(result.Contains(Check + " i < n)"), result);
        }

        [Test]
        public void CheckThrowsOnceTheBudgetIsSpent()
        {
            Begin(TimeSpan.Zero);
            try
            {
                Assert.Throws<TimeoutException>(() =>
                {
                    for (var i = 0; LoopBudgetGuard.Check(true); i++)
                        if (i > 10000) Assert.Fail("guard never fired");
                });
            }
            finally { End(); }
        }

        [Test]
        public void CheckIsInertOutsideASnippet()
        {
            End();
            Assert.IsTrue(LoopBudgetGuard.Check(true));
            Assert.IsFalse(LoopBudgetGuard.Check(false));
        }

        private static void Begin(TimeSpan budget) => Invoke("Begin", budget);

        private static void End() => Invoke("End", null);

        private static void Invoke(string name, object arg)
        {
            var method = typeof(LoopBudgetGuard).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            method.Invoke(null, arg == null ? new object[0] : new[] { arg });
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
