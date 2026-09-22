// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.MCP.Server;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public class ProjectIdentityTests
    {
        [Test]
        public void Pin_DiffersForProjectsSharingAProductName()
        {
            Assert.AreNotEqual(
                ProjectIdentity.PinFromProjectPath(@"C:\Unity\PROJECT_TEST"),
                ProjectIdentity.PinFromProjectPath(@"C:\Unity\clone\PROJECT_TEST"));
        }

        [Test]
        public void Pin_IgnoresSeparatorStyleCaseAndTrailingSlash()
        {
            var expected = ProjectIdentity.PinFromProjectPath(@"C:\Unity\PROJECT_TEST");

            Assert.AreEqual(expected, ProjectIdentity.PinFromProjectPath(@"C:/Unity/PROJECT_TEST"));
            Assert.AreEqual(expected, ProjectIdentity.PinFromProjectPath(@"c:\unity\project_test"));
            Assert.AreEqual(expected, ProjectIdentity.PinFromProjectPath(@"C:\Unity\PROJECT_TEST\"));
        }

        // 10 apart so a fall-forward scan (which probes basePort..basePort+9) cannot land on the
        // next project's reserved default and displace it in turn.
        [Test]
        public void PortOffset_SitsOnASlotTenApartAndIsStableForAPath()
        {
            const string path = @"C:\Unity\PROJECT_TEST";
            var offset = ProjectIdentity.PortOffsetFromProjectPath(path);

            Assert.AreEqual(offset, ProjectIdentity.PortOffsetFromProjectPath(@"c:/unity/project_test/"));
            Assert.AreEqual(0, offset % 10);
            Assert.That(offset, Is.InRange(0, 990));
        }

        [Test]
        public void Pin_IsThePrefixOfTheFullIdentity()
        {
            const string path = @"C:\Unity\PROJECT_TEST";
            var pin = ProjectIdentity.PinFromProjectPath(path);

            Assert.AreEqual(ProjectIdentity.PinLength, pin.Length);
            StringAssert.StartsWith(pin, ProjectIdentity.FromProjectPath(path));
        }
    }
}
