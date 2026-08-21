// Copyright (C) KitWright. Licensed under MIT.

using KitWright.Editor.Services.UnityLogs;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class UnityLogsRepositoryTests
    {
        // A non-empty "nothing matched" sentence made source "auto" stop at the cache, so the
        // console fallback was dead as soon as one log was cached.
        [Test]
        public void GetRecentLogs_ReturnsNullWhenTheCacheHoldsNoMatch()
        {
            using (var repository = new UnityLogsRepository())
            {
                repository.StartListening();
                repository.Clear();

                Debug.Log("KitWrightNoMatchProbe_" + System.Guid.NewGuid().ToString("N"));

                Assert.IsNull(repository.GetRecentLogs(
                    logType: null,
                    count: 10,
                    sinceSeconds: 0,
                    filterText: "NoSuchTextAnywhere_" + System.Guid.NewGuid().ToString("N"),
                    groupDuplicates: false));
            }
        }

        // The repository drops the plugin's own chatter by prefix. log_message used to borrow that
        // prefix, so a line an agent asked the editor to log could never be read back.
        [Test]
        public void GetRecentLogs_KeepsAgentLoggedLinesAndStillDropsPluginChatter()
        {
            var token = "KitWrightPrefix_" + System.Guid.NewGuid().ToString("N");

            using (var repository = new UnityLogsRepository())
            {
                repository.StartListening();
                repository.Clear();

                Debug.Log("[MCP] " + token + " kept");
                Debug.Log("[KitWright] " + token + " chatter");
                Debug.Log("[KitWright MCP Server] " + token + " chatter");

                var logs = repository.GetRecentLogs(
                    logType: null,
                    count: 20,
                    sinceSeconds: 0,
                    filterText: token,
                    groupDuplicates: false);

                Assert.That(logs, Does.Contain("[MCP] " + token + " kept"),
                    "what log_message writes has to be readable through get_console_logs");
                Assert.That(logs, Does.Not.Contain("[KitWright] " + token));
                Assert.That(logs, Does.Not.Contain("[KitWright MCP Server] " + token));
            }
        }

        [Test]
        public void GetRecentLogs_FiltersGroupsAndTruncatesCachedEntries()
        {
            var token = "KitWrightConsoleGrouping_" + System.Guid.NewGuid().ToString("N");
            var longPayload = new string('x', 360);

            using (var repository = new UnityLogsRepository())
            {
                repository.StartListening();
                repository.Clear();

                Debug.Log(token + " duplicate");
                Debug.Log(token + " duplicate");
                Debug.Log(token + " unique");
                Debug.Log(token + " " + longPayload);

                var grouped = repository.GetRecentLogs(
                    logType: "log",
                    count: 10,
                    sinceSeconds: 0,
                    filterText: token,
                    groupDuplicates: true);

                Assert.That(grouped, Does.Contain("Console logs (4 entries, 3 unique, filter: log, source: cache"));
                Assert.That(grouped, Does.Contain("[LOG] " + token + " duplicate (x2)"));
                Assert.That(grouped, Does.Contain("[LOG] " + token + " unique"));
                Assert.That(grouped, Does.Contain("... (+"));
                Assert.That(grouped.Length, Is.LessThan(1400));

                var filtered = repository.GetRecentLogs(
                    logType: "log",
                    count: 10,
                    sinceSeconds: 0,
                    filterText: token + " unique",
                    groupDuplicates: true);

                Assert.That(filtered, Does.Contain("Console logs (1 entries, filter: log, source: cache"));
                Assert.That(filtered, Does.Contain("[LOG] " + token + " unique"));
                Assert.That(filtered, Does.Not.Contain("duplicate"));

                var ungrouped = repository.GetRecentLogs(
                    logType: "log",
                    count: 10,
                    sinceSeconds: 0,
                    filterText: token + " duplicate",
                    groupDuplicates: false);

                Assert.That(ungrouped, Does.Contain("Console logs (2 entries, filter: log, source: cache"));
                Assert.That(ungrouped, Does.Not.Contain("(x2)"));
            }
        }

        [Test]
        public void LogRaisedOffTheMainThread_IsCapturedWithoutTheMainThreadTicking()
        {
            var token = "KitWrightThreadedLog_" + System.Guid.NewGuid().ToString("N");

            using (var repository = new UnityLogsRepository())
            {
                repository.StartListening();
                repository.Clear();

                // This test body owns the main thread for its whole duration, exactly as a modal
                // dialog does. A main-thread-only subscription cannot deliver anything here.
                System.Threading.Tasks.Task.Run(() => Debug.Log(token)).Wait(5000);

                var logs = repository.GetRecentLogs(logType: "log", count: 10, filterText: token);
                Assert.That(logs, Does.Contain(token));
            }
        }

        [Test]
        public void GetRecentLogs_KeepsMultiLineBodyAndMatchesFilterBelowTheFirstLine()
        {
            var token = "KitWrightMultiLine_" + System.Guid.NewGuid().ToString("N");

            using (var repository = new UnityLogsRepository())
            {
                repository.StartListening();
                repository.Clear();

                Debug.Log(token + " header\nsecond line " + token + "-below");

                var result = repository.GetRecentLogs(
                    logType: "log",
                    count: 10,
                    sinceSeconds: 0,
                    filterText: token + "-below");

                Assert.That(result, Does.Contain(token + " header"));
                Assert.That(result, Does.Contain("second line " + token + "-below"));
            }
        }

        [Test]
        public void SplitMessageAndStackTrace_SplitsAtTheFirstStackFrameAndKeepsMultiLineBodies()
        {
            UnityLogsRepository.SplitMessageAndStackTrace(
                "Gradle build failed:\n> Task :app:compile FAILED\nUnityEngine.Debug:LogError (object)",
                out var body, out var stack);

            Assert.AreEqual("Gradle build failed:\n> Task :app:compile FAILED", body);
            Assert.AreEqual("UnityEngine.Debug:LogError (object)", stack);

            UnityLogsRepository.SplitMessageAndStackTrace("first\nsecond", out body, out stack);
            Assert.AreEqual("first\nsecond", body);
            Assert.IsNull(stack);

            UnityLogsRepository.SplitMessageAndStackTrace(
                "boom\n  at Foo.Bar () (at Assets/Foo.cs:12)", out body, out stack);
            Assert.AreEqual("boom", body);
            Assert.That(stack, Does.Contain("Assets/Foo.cs:12"));
        }

        [Test]
        public void LogNotificationGuard_FollowsWhetherASubscriberIsAttached()
        {
            var sessions = MCP.Server.SSE.SSESessionManager.Instance;
            sessions.ResetForTests();

            try
            {
                Assert.IsFalse(sessions.HasLogSubscribers,
                    "A log with no SSE session attached must not build a notification.");

                sessions.SetLoggingLevel(null, "info");
                Assert.IsTrue(sessions.HasLogSubscribers, "The guard must not mute a real subscriber.");
            }
            finally
            {
                sessions.ResetForTests();
            }
        }

        [Test]
        public void HelperMethods_HandleEmptyTextAndLongLines()
        {
            Assert.IsTrue(UnityLogsRepository.MatchesTextFilter("Hello Console", "console"));
            Assert.IsFalse(UnityLogsRepository.MatchesTextFilter("Hello Console", "missing"));
            Assert.IsTrue(UnityLogsRepository.MatchesTextFilter(null, null));
            Assert.IsFalse(UnityLogsRepository.MatchesTextFilter(null, "missing"));

            var line = new string('a', 305);
            var truncated = UnityLogsRepository.TruncateLine(line);

            Assert.That(truncated, Does.StartWith(new string('a', 300)));
            Assert.That(truncated, Does.EndWith("... (+5 chars)"));
        }

        [Test]
        public void StripRichText_RemovesUnityMarkupButKeepsOtherAngleBrackets()
        {
            Assert.AreEqual(
                "fail: TestResultCollector missing request id",
                UnityLogsRepository.StripRichText(
                    "<color=#ff6b6b>fail:</color> <color=#58D68D><b>TestResultCollector</b></color> missing request id"));

            Assert.AreEqual("done", UnityLogsRepository.StripRichText("<size=20><i>done</i></size>"));

            Assert.AreEqual("List<int> has 3 items", UnityLogsRepository.StripRichText("List<int> has 3 items"));
            Assert.AreEqual("<node id=\"7\" />", UnityLogsRepository.StripRichText("<node id=\"7\" />"));

            Assert.AreEqual("plain", UnityLogsRepository.StripRichText("plain"));
            Assert.IsNull(UnityLogsRepository.StripRichText(null));
        }

        [Test]
        public void GetRecentLogs_StampsEntriesWithoutBreakingDuplicateGrouping()
        {
            var token = "KitWrightConsoleStamp_" + System.Guid.NewGuid().ToString("N");

            using (var repository = new UnityLogsRepository())
            {
                repository.StartListening();
                repository.Clear();

                Debug.Log(token + " <b>duplicate</b>");
                Debug.Log(token + " <b>duplicate</b>");

                var stamped = repository.GetRecentLogs(
                    logType: "log",
                    count: 10,
                    sinceSeconds: 0,
                    filterText: token,
                    groupDuplicates: true,
                    includeStackTrace: false,
                    includeTimestamps: true);

                Assert.That(stamped, Does.Not.Contain("<b>"));
                Assert.That(stamped, Does.Contain("Console logs (2 entries, 1 unique, filter: log, source: cache"));
                Assert.That(stamped, Does.Match(@"\n\d{2}:\d{2}:\d{2} \[LOG\] " + token + @" duplicate \(x2\)"));

                var unstamped = repository.GetRecentLogs(
                    logType: "log",
                    count: 10,
                    sinceSeconds: 0,
                    filterText: token,
                    groupDuplicates: true);

                Assert.That(unstamped, Does.Contain("[LOG] " + token + " duplicate (x2)"));
                Assert.That(unstamped, Does.Not.Match(@"\n\d{2}:\d{2}:\d{2} \["));
            }
        }

        [Test]
        public void FormatStackTrace_NormalizesLineEndingsAndCapsLength()
        {
            var normalized = UnityLogsRepository.FormatStackTrace("First\r\nSecond\rThird\n");

            Assert.AreEqual("\n    First\n    Second\n    Third", normalized);
            Assert.That(normalized, Does.Not.Contain("\r"));

            var longTrace = new string('s', 2105);
            var truncated = UnityLogsRepository.FormatStackTrace(longTrace);
            Assert.That(truncated, Does.StartWith("\n    " + new string('s', 2000)));
            Assert.That(truncated, Does.EndWith("... (+105 chars)"));
        }

        [Test]
        public void MissingConsoleLevelBits_ReportsTheLevelsTheConsoleWindowIsHiding()
        {
            // 7682 = a reported consoleFlags value: LogLevelError (1<<9) on, Log (1<<7) and Warning (1<<8) off.
            Assert.AreEqual(new[] { 1 << 7, 1 << 8 }, Tools.Builtins.VisualFeedbackFunctions.MissingConsoleLevelBits(7682));
            Assert.IsEmpty(Tools.Builtins.VisualFeedbackFunctions.MissingConsoleLevelBits(7682 | (1 << 7) | (1 << 8)));
        }
    }
}
