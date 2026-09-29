// Copyright (C) KitWright. Licensed under MIT.

using KitWright.Editor.MCP.Server;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor
{
    public sealed class PowerThrottlingTests
    {
        [Test]
        public void TheEditorOptsOutOfPowerThrottling()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("Power throttling is a Windows feature.");

            PowerThrottling.OptOut();

            Assert.IsTrue(PowerThrottling.IsOptedOut(), "Windows still decides whether to throttle the editor.");
        }
    }
}
