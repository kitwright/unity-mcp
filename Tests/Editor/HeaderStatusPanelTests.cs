// Copyright (C) KitWright. Licensed under MIT.

using KitWright.Editor.MCP.Server;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace KitWright.Editor.Tests
{
    // The Connection row is the only part of the server panel that stays visible when the foldout
    // is collapsed, so what it says about a broken transport is what most users will ever see.
    public sealed class HeaderStatusPanelTests
    {
        private const string Warn = "⚠";

        // The six cases below test the wording; this one tests that the label reaches the row at
        // all. Without it, deleting the Add or the RefreshAlert call leaves every wording test
        // green while the header silently stops reporting anything.
        [Test]
        public void AlertLabelIsMountedAheadOfTheStatusAndStartsHidden()
        {
            var row = new VisualElement();
            var host = new VisualElement();
            row.Add(host);

            new HeaderStatusPanel(null, null).AddTo(row, host);

            var labels = host.Query<Label>().ToList();
            Assert.GreaterOrEqual(labels.Count, 2,
                "the host should carry the alert label as well as the status label");

            var alert = labels[0];
            Assert.AreEqual(DisplayStyle.None, alert.resolvedStyle.display,
                "nothing is wrong yet, so the alert must not take space in the row");
            Assert.AreEqual(TextAnchor.MiddleRight, labels[1].style.unityTextAlign.value,
                "the status stays last and right-aligned, so the alert sits in the gap before it");
        }

        [Test]
        public void HealthyServerShowsNothing()
        {
            Assert.IsNull(HeaderStatusPanel.DescribeProblem(
                isRunning: true, isTransitioning: false, brokerMode: true, brokerRunning: true,
                brokerError: null, configuredPort: 8765, boundPort: 8765));
        }

        // "Stopped" and "Connecting..." already read on the right of the same row and on the button.
        [Test]
        public void StoppedOrTransitioningShowsNothing()
        {
            Assert.IsNull(HeaderStatusPanel.DescribeProblem(
                isRunning: false, isTransitioning: false, brokerMode: true, brokerRunning: false,
                brokerError: "boom", configuredPort: 8765, boundPort: 0));

            Assert.IsNull(HeaderStatusPanel.DescribeProblem(
                isRunning: true, isTransitioning: true, brokerMode: true, brokerRunning: false,
                brokerError: "boom", configuredPort: 8765, boundPort: 0));
        }

        [Test]
        public void BrokerDownIsReportedWithItsReasonWhenThereIsOne()
        {
            var withReason = HeaderStatusPanel.DescribeProblem(
                isRunning: true, isTransitioning: false, brokerMode: true, brokerRunning: false,
                brokerError: "mono is missing", configuredPort: 8765, boundPort: 8765);
            StringAssert.StartsWith(Warn, withReason);
            StringAssert.Contains("mono is missing", withReason);

            var withoutReason = HeaderStatusPanel.DescribeProblem(
                isRunning: true, isTransitioning: false, brokerMode: true, brokerRunning: false,
                brokerError: null, configuredPort: 8765, boundPort: 8765);
            StringAssert.StartsWith(Warn, withoutReason);
            StringAssert.Contains("Broker not running", withoutReason);
        }

        // Direct HTTP has no broker to be down, so a stale LastError from an earlier broker run
        // must not surface as a problem the user cannot act on.
        [Test]
        public void DirectTransportIgnoresBrokerState()
        {
            Assert.IsNull(HeaderStatusPanel.DescribeProblem(
                isRunning: true, isTransitioning: false, brokerMode: false, brokerRunning: false,
                brokerError: "stale error from a previous broker mode run",
                configuredPort: 8765, boundPort: 8765));
        }

        // The port field shows the configured port, so a start that fell forward leaves every
        // client config pointing at nothing while the row still reads as running.
        [Test]
        public void PortFallForwardNamesBothPorts()
        {
            var message = HeaderStatusPanel.DescribeProblem(
                isRunning: true, isTransitioning: false, brokerMode: true, brokerRunning: true,
                brokerError: null, configuredPort: 8765, boundPort: 8767);

            StringAssert.StartsWith(Warn, message);
            StringAssert.Contains("8765", message);
            StringAssert.Contains("8767", message);
        }

        [Test]
        public void BrokerDownOutranksAMovedPort()
        {
            var message = HeaderStatusPanel.DescribeProblem(
                isRunning: true, isTransitioning: false, brokerMode: true, brokerRunning: false,
                brokerError: null, configuredPort: 8765, boundPort: 8767);

            StringAssert.Contains("Broker not running", message);
            StringAssert.DoesNotContain("8767", message);
        }
    }
}
