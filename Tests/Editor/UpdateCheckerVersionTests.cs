// Copyright (C) KitWright. All rights reserved.

using System;
using KitWright.Editor.MCP.Server;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class UpdateCheckerVersionTests
    {
        [Test]
        public void ParseComparableVersion_ReadsTagsWithAndWithoutTheVPrefix()
        {
            Assert.AreEqual(new Version(1, 2, 3), UpdateChecker.ParseComparableVersion("1.2.3"));
            Assert.AreEqual(new Version(1, 2, 3), UpdateChecker.ParseComparableVersion("v1.2.3"));
            Assert.AreEqual(new Version(1, 2, 3), UpdateChecker.ParseComparableVersion("V1.2.3"));
            Assert.AreEqual(new Version(1, 2, 3), UpdateChecker.ParseComparableVersion("  v1.2.3  "));
        }

        [Test]
        public void ParseComparableVersion_CutsPreReleaseAndBuildSuffixes()
        {
            Assert.AreEqual(new Version(1, 2, 3), UpdateChecker.ParseComparableVersion("1.2.3-rc1"));
            Assert.AreEqual(new Version(1, 2, 3), UpdateChecker.ParseComparableVersion("v1.2.3-beta.4"));
            Assert.AreEqual(new Version(1, 2, 3), UpdateChecker.ParseComparableVersion("1.2.3+build.77"));
        }

        [Test]
        public void ParseComparableVersion_FallsBackToZeroInsteadOfThrowing()
        {
            var zero = new Version(0, 0, 0);
            Assert.AreEqual(zero, UpdateChecker.ParseComparableVersion(null));
            Assert.AreEqual(zero, UpdateChecker.ParseComparableVersion(""));
            Assert.AreEqual(zero, UpdateChecker.ParseComparableVersion("   "));
            Assert.AreEqual(zero, UpdateChecker.ParseComparableVersion("not-a-version"));
            Assert.AreEqual(zero, UpdateChecker.ParseComparableVersion("v"));
        }

        [Test]
        public void ParseComparableVersion_OrdersByNumberNotByText()
        {
            // The reason this cannot be a string compare: "10" sorts before "9" as text.
            Assert.IsTrue(UpdateChecker.ParseComparableVersion("v0.10.0") >
                          UpdateChecker.ParseComparableVersion("v0.9.0"));

            Assert.IsTrue(UpdateChecker.ParseComparableVersion("v2.0.0") >
                          UpdateChecker.ParseComparableVersion("v1.99.99"));

            Assert.IsTrue(UpdateChecker.ParseComparableVersion("v1.2.10") >
                          UpdateChecker.ParseComparableVersion("v1.2.9"));

            Assert.AreEqual(0, UpdateChecker.ParseComparableVersion("v1.2.3")
                .CompareTo(UpdateChecker.ParseComparableVersion("1.2.3")));

            // A release candidate is not newer than the release it precedes.
            Assert.AreEqual(0, UpdateChecker.ParseComparableVersion("v1.2.3-rc1")
                .CompareTo(UpdateChecker.ParseComparableVersion("v1.2.3")));

            // Unparseable never wins, so a bad tag cannot advertise itself as an update.
            Assert.IsTrue(UpdateChecker.ParseComparableVersion("v0.0.1") >
                          UpdateChecker.ParseComparableVersion("garbage"));
        }
    }
}
