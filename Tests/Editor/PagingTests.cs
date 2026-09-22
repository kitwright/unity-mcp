// Copyright (C) KitWright. All rights reserved.

using System.Collections.Generic;
using System.Linq;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class PagingTests
    {
        private static List<int> Numbers(int count) => Enumerable.Range(0, count).ToList();

        [Test]
        public void PagesJoinBackIntoTheWholeList([Values(1, 3, 7, 10)] int pageSize)
        {
            var source = Numbers(10);
            var walked = new List<int>();
            var cursor = 0;
            var guard = 0;

            do
            {
                var page = Paging.Page(source, cursor, pageSize);
                walked.AddRange(page);
                cursor = Paging.Next(cursor, page.Count, source.Count);
                Assert.Less(guard++, 20, "the cursor stopped advancing");
            } while (cursor > 0);

            CollectionAssert.AreEqual(source, walked);
        }

        [Test]
        public void NextCursorIsZeroOnTheLastPage()
        {
            Assert.AreEqual(0, Paging.Next(5, Paging.Page(Numbers(10), 5, 5).Count, 10));
            Assert.AreEqual(9, Paging.Next(5, Paging.Page(Numbers(10), 5, 4).Count, 10),
                "one item still left, so the walk is not over");
        }

        [Test]
        public void CursorPastTheEndYieldsAnEmptyPageRatherThanWrapping()
        {
            var page = Paging.Page(Numbers(3), 99, 10);

            CollectionAssert.IsEmpty(page);
            Assert.AreEqual(0, Paging.Next(99, page.Count, 3));
        }

        [Test]
        public void NegativeCursorAndZeroPageSizeAreClamped()
        {
            var page = Paging.Page(Numbers(3), -5, 0);

            CollectionAssert.AreEqual(new[] { 0 }, page, "a page size below one must still make progress");
            Assert.AreEqual(1, Paging.Next(-5, page.Count, 3));
        }

        [Test]
        public void SuffixIsEmptyWhenEverythingFitsOnPageOne()
        {
            Assert.AreEqual(string.Empty, Paging.Suffix(cursor: 0, shown: 3, total: 3));
        }

        [Test]
        public void SuffixNamesTheCursorToPassBack()
        {
            var suffix = Paging.Suffix(cursor: 0, shown: 50, total: 200);

            StringAssert.Contains("Showing 1-50 of 200", suffix);
            StringAssert.Contains("cursor=50", suffix);
        }

        [Test]
        public void SuffixSaysWhereTheWalkEnded()
        {
            var suffix = Paging.Suffix(cursor: 50, shown: 10, total: 60);

            StringAssert.Contains("Showing 51-60 of 60", suffix);
            StringAssert.Contains("end of the list", suffix);
            Assert.That(suffix, Does.Not.Contain("pass cursor="));
        }

        // Page clamps internally and says nothing about it, so a caller that echoed the raw cursor
        // into the text printed "Showing 0-" or worse. Two tools guarded it by hand; eight did not.
        [Test]
        public void SuffixClampsANegativeCursorInsteadOfCountingFromIt()
        {
            StringAssert.Contains("Showing 1-2 of 9", Paging.Suffix(cursor: -5, shown: 2, total: 9));
        }

        [Test]
        public void SuffixExplainsACursorPastTheEnd()
        {
            StringAssert.Contains("past the end", Paging.Suffix(cursor: 99, shown: 0, total: 3));
        }
    }
}
