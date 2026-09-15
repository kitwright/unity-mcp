// Copyright (C) KitWright. Licensed under MIT.

using System;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class TestRunnerFunctionsTests
    {
        private static readonly DateTime Now = new DateTime(2026, 7, 13, 12, 0, 0);

        [Test]
        public void StuckAssessment_FlagsRunnerStartAtThirtySeconds()
        {
            Assert.IsNull(TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-29), null, Now));

            var assessment = TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-30), null, Now);

            Assert.IsNotNull(assessment);
            Assert.AreEqual("runner_start_or_transition", assessment.Phase);
            Assert.AreEqual(30, assessment.SecondsSinceActivity);
            StringAssert.Contains("another caller", assessment.Hint);
        }

        [Test]
        public void StuckAssessment_GivesKnownTestLongerThreshold()
        {
            const string testName = "Game.Tests.LoadsLargeScene";
            Assert.IsNull(TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-60), testName, Now));
            Assert.IsNull(TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-119), testName, Now));

            var assessment = TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-120), testName, Now);

            Assert.IsNotNull(assessment);
            Assert.AreEqual("test_execution", assessment.Phase);
            Assert.AreEqual(120, assessment.SecondsSinceActivity);
            StringAssert.Contains(testName, assessment.Hint);
            StringAssert.Contains("legitimate long-running test", assessment.Hint);
        }

        [Test]
        public void StuckAssessment_IgnoresFutureActivity()
        {
            Assert.IsNull(TestRunnerFunctions.AssessStuckState(Now.AddSeconds(60), null, Now));
        }

        private static Newtonsoft.Json.Linq.JObject Job(string status, bool hasFilters, int totalTests) =>
            new Newtonsoft.Json.Linq.JObject
            {
                ["status"] = status,
                ["hasFilters"] = hasFilters,
                ["totalTests"] = totalTests
            };

        [Test]
        public void AFilteredRunThatMatchedNothingIsNotAPass()
        {
            // NUnit finishes an empty run as Passed, so a misspelt name or the wrong separator would
            // otherwise report a green run that tested nothing.
            Assert.IsTrue(TestRunnerFunctions.MatchedNothing(Job("finished", hasFilters: true, totalTests: 0)));

            Assert.IsFalse(TestRunnerFunctions.MatchedNothing(Job("finished", hasFilters: true, totalTests: 1)));
            Assert.IsFalse(TestRunnerFunctions.MatchedNothing(Job("running", hasFilters: true, totalTests: 0)),
                "A filtered run reports no total until it finishes.");
            Assert.IsFalse(TestRunnerFunctions.MatchedNothing(Job("finished", hasFilters: false, totalTests: 0)),
                "An unfiltered run of a project with no tests is not the caller's mistake.");
        }
    }
}
