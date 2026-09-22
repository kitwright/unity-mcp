// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.MCP.Server;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    // Regression for the "spaces in the path crash the client" class seen in competing Unity MCP
    // servers: the broker executable lives under a path like C:\Program Files\..., so its spawn
    // argument must stay a single quoted token or the process launcher splits it at the space.
    public sealed class MCPBrokerSpawnArgumentsTests
    {
        [Test]
        public void BuildSpawnArguments_QuotesAnExecutablePathContainingSpaces()
        {
            var args = MCPBrokerProcessManager.BuildSpawnArguments(
                @"C:\Program Files\KitWright\broker.exe", 8766, "tok", "pin123");

            Assert.That(args, Does.StartWith("\"C:\\Program Files\\KitWright\\broker.exe\""),
                "The executable path must be one quoted token so a space does not split it.");
        }

        [Test]
        public void BuildSpawnArguments_CarriesPortTokenPinAndProtocol()
        {
            var args = MCPBrokerProcessManager.BuildSpawnArguments("broker.exe", 8766, "tok", "pin123");

            Assert.That(args, Does.Contain("--port 8766"));
            Assert.That(args, Does.Contain("--token tok"));
            Assert.That(args, Does.Contain("--pin pin123"));
            Assert.That(args, Does.Contain("--protocol "));
        }
    }
}
