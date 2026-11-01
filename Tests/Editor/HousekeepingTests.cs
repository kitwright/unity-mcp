// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    // Runs entirely under Path.GetTempPath(): nothing lands in the project, so no .meta or asset residue.
    public sealed class HousekeepingTests
    {
        private static readonly DateTime Now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Week = TimeSpan.FromDays(7);

        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "KitWrightHousekeepingTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); }
            catch { }
        }

        private static (string, DateTime) Days(string name, double daysOld) => (name, Now.AddDays(-daysOld));

        private string Make(string name, TimeSpan age)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
            return path;
        }

        [Test]
        public void SelectFilesToPrune_DropsOnlyWhatIsBeyondTheNewestAndOlderThanTheLimit()
        {
            var files = new[] { Days("a", 30), Days("b", 1), Days("c", 10), Days("d", 2), Days("e", 20) };

            Assert.That(Housekeeping.SelectFilesToPrune(files, 2, Week, Now), Is.EqualTo(new[] { "c", "e", "a" }));
        }

        [Test]
        public void SelectFilesToPrune_KeepsAFileBeyondTheNewestWhenItIsStillYoung()
        {
            var files = new[] { Days("a", 1), Days("b", 2), Days("c", 3), Days("d", 4), Days("e", 5) };

            Assert.IsEmpty(Housekeeping.SelectFilesToPrune(files, 2, Week, Now));
        }

        [Test]
        public void SelectFilesToPrune_KeepsAnOldFileThatIsAmongTheNewest()
        {
            var files = new[] { Days("a", 30), Days("b", 40) };

            Assert.IsEmpty(Housekeeping.SelectFilesToPrune(files, 5, Week, Now));
        }

        [Test]
        public void SelectFilesToPrune_AFileExactlyAsOldAsTheLimitIsKept()
        {
            Assert.IsEmpty(Housekeeping.SelectFilesToPrune(new[] { Days("a", 7) }, 0, Week, Now));
        }

        [Test]
        public void SelectFilesToPrune_WithNothingToKeepSelectsEveryEntryPastTheAgeLimit()
        {
            var directories = new[] { ("recent", Now.AddMinutes(-30)), ("stale", Now.AddHours(-2)) };

            Assert.That(Housekeeping.SelectFilesToPrune(directories, 0, TimeSpan.FromHours(1), Now), Is.EqualTo(new[] { "stale" }));
        }

        [Test]
        public void PruneFiles_DeletesOldFilesBeyondTheNewestAndSparesTheOneJustWritten()
        {
            Make("fresh1.png", TimeSpan.Zero);
            Make("fresh2.png", TimeSpan.FromMinutes(1));
            var oldOne = Make("old1.png", TimeSpan.FromDays(30));
            var oldTwo = Make("old2.png", TimeSpan.FromDays(31));
            var justWritten = Make("just-written.png", TimeSpan.FromDays(40));
            var otherKind = Make("note.txt", TimeSpan.FromDays(50));

            Housekeeping.PruneFiles(_root, "*.png", 2, justWritten);

            Assert.IsFalse(File.Exists(oldOne));
            Assert.IsFalse(File.Exists(oldTwo));
            Assert.IsTrue(File.Exists(justWritten), "the file written by the current call is never touched");
            Assert.IsTrue(File.Exists(otherKind), "only files matching the pattern are candidates");
            Assert.AreEqual(2, Directory.GetFiles(_root, "fresh*.png").Length);
        }

        [Test]
        public void PruneFiles_LeavesEveryFileAloneWhileTheyAreAllYoung()
        {
            for (var i = 0; i < 5; i++)
                Make("shot" + i + ".png", TimeSpan.FromHours(i));

            Housekeeping.PruneFiles(_root, "*.png", 2, Path.Combine(_root, "shot0.png"));

            Assert.AreEqual(5, Directory.GetFiles(_root, "*.png").Length);
        }

        [Test]
        public void PruneFiles_ADirectoryThatDoesNotExistIsNotAnError()
        {
            Assert.DoesNotThrow(() => Housekeeping.PruneFiles(Path.Combine(_root, "missing"), "*", 1, "x.png"));
        }

        [Test]
        public void DeleteStaleDirectories_RemovesOldSubdirectoriesWithTheirContentAndKeepsRecentOnes()
        {
            var stale = Path.Combine(_root, "stale");
            var recent = Path.Combine(_root, "recent");
            Directory.CreateDirectory(stale);
            Directory.CreateDirectory(recent);
            File.WriteAllText(Path.Combine(stale, "Snippet.cs"), "x");
            Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
            var looseFile = Make("loose.txt", TimeSpan.FromHours(5));

            Housekeeping.DeleteStaleDirectories(_root, TimeSpan.FromHours(1));

            Assert.IsFalse(Directory.Exists(stale));
            Assert.IsTrue(Directory.Exists(recent));
            Assert.IsTrue(File.Exists(looseFile), "only directories are swept");
        }

        [Test]
        public void DeleteStaleDirectories_ARootThatDoesNotExistIsNotAnError()
        {
            Assert.DoesNotThrow(() => Housekeeping.DeleteStaleDirectories(Path.Combine(_root, "missing"), TimeSpan.FromHours(1)));
        }

        [Test]
        public void Due_FiresOnTheFirstCallAndThenOnceEveryNCalls()
        {
            var writes = 3;

            Assert.IsTrue(Housekeeping.Due(ref writes, 3), "a counter that starts at N makes the first write prune");
            Assert.IsFalse(Housekeeping.Due(ref writes, 3));
            Assert.IsFalse(Housekeeping.Due(ref writes, 3));
            Assert.IsTrue(Housekeeping.Due(ref writes, 3));
            Assert.IsFalse(Housekeeping.Due(ref writes, 3));
        }

        [Test]
        public void SelectKeysToEvict_NamesTheLeastRecentlyTouchedFirst()
        {
            var stamps = new[]
            {
                new System.Collections.Generic.KeyValuePair<string, long>("a", 5),
                new System.Collections.Generic.KeyValuePair<string, long>("b", 1),
                new System.Collections.Generic.KeyValuePair<string, long>("c", 3),
                new System.Collections.Generic.KeyValuePair<string, long>("d", 2)
            };

            Assert.That(SessionRecency.SelectKeysToEvict(stamps, 2), Is.EqualTo(new[] { "b", "d" }));
            Assert.IsEmpty(SessionRecency.SelectKeysToEvict(stamps, 4));
            Assert.IsEmpty(SessionRecency.SelectKeysToEvict(stamps, 10));
        }

        [Test]
        public void Evict_KeepsTheSessionsThatAreStillInUse()
        {
            var recency = new SessionRecency();
            var map = new ConcurrentDictionary<string, string>();
            for (var i = 0; i < 5; i++)
            {
                map["s" + i] = "v";
                recency.Touch("s" + i);
            }

            recency.Touch("s0");

            Assert.AreEqual(2, recency.Evict(map, 3));
            Assert.That(map.Keys.OrderBy(key => key), Is.EqualTo(new[] { "s0", "s3", "s4" }), "a session touched again is not the oldest");
            Assert.AreEqual(0, recency.Evict(map, 3));
        }

        [Test]
        public void Evict_BoundsAMapThatGrowsWithEverySession()
        {
            var recency = new SessionRecency();
            var map = new ConcurrentDictionary<string, long>();
            for (var i = 0; i < 1000; i++)
            {
                map["session-" + i] = i;
                recency.Touch("session-" + i);
                recency.Evict(map, SessionRecency.MaxSessions);
            }

            Assert.AreEqual(SessionRecency.MaxSessions, map.Count);
            Assert.IsTrue(map.ContainsKey("session-999"));
            Assert.IsFalse(map.ContainsKey("session-0"));
        }
    }
}
