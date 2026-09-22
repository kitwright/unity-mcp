// Copyright (C) KitWright. All rights reserved.

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using KitWright.Editor.Tools.Scripting;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// Source scan (no server, no license needed): SECURITY.md publishes guarantees a report can
    /// hold us to. PathSafetyTests already covers project containment; these cover the other two,
    /// which had nothing failing when they broke.
    ///
    /// Strings, chars and comments are masked before matching, so prose naming a forbidden API does
    /// not fail the scan and a real call cannot hide inside a literal.
    /// </summary>
    public sealed class SecurityGuaranteeGuardTests
    {
        // "Loopback only. The HTTP transport binds IPAddress.Loopback and the keepalive broker
        // listens on 127.0.0.1. There is no LAN bind option, so anything reachable off-host is a bug."
        private static readonly string[] OffHostBinds =
        {
            @"IPAddress\.Any",
            @"IPAddress\.IPv6Any",
            @"IPAddress\.Broadcast",
            // A parsed address is one config value away from a LAN bind.
            @"IPAddress\.Parse\s*\(",
        };

        // The token lives in exactly three files: the broker validates it, the process manager
        // spawns with it, the client transport sends it as a header.
        private static readonly string[] TokenBearingSources =
        {
            "Editor/MCP/Server/Broker/keepalive-broker.cs.txt",
            "Editor/MCP/Server/Broker/MCPBrokerProcessManager.cs",
            "Editor/MCP/Server/Broker/MCPBrokerClientTransport.cs",
        };

        [Test]
        public void ShippedListenerBinds_AreLoopbackOnly()
        {
            var violations = new List<string>();

            foreach (var source in ShippedSources())
            {
                var lines = source.Masked.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (var pattern in OffHostBinds)
                        if (Regex.IsMatch(lines[i], pattern))
                            violations.Add($"{source.Name}:{i + 1} binds off-host -- {lines[i].Trim()}");

                    // A listener built from a variable hides which address it was handed, so the
                    // address has to be written at the construction site. The optional qualifier
                    // matters: MCPServerService spells it System.Net.Sockets.TcpListener.
                    if (Regex.IsMatch(lines[i], @"new\s+(?:[\w.]+\.)?TcpListener\s*\(") &&
                        !lines[i].Contains("IPAddress.Loopback"))
                        violations.Add(
                            $"{source.Name}:{i + 1} constructs a TcpListener with no IPAddress.Loopback " +
                            $"on the same line -- {lines[i].Trim()}");
                }
            }

            Assert.IsEmpty(violations,
                "SECURITY.md promises loopback only, so anything reachable off-host is a bug:\n"
                + string.Join("\n", violations));
        }

        [Test]
        public void ShippedCode_IntroducesNoHttpListener()
        {
            // http.sys binds by URL prefix rather than by IPAddress, so a migration would route
            // around ShippedListenerBinds_AreLoopbackOnly and leave it green while the guarantee
            // went unchecked. Adding one means teaching that test to read Prefixes first.
            foreach (var source in ShippedSources())
                Assert.That(source.Masked, Does.Not.Match(@"new\s+(?:[\w.]+\.)?HttpListener\s*\("),
                    source.Name + " introduces HttpListener, whose bind address "
                    + nameof(ShippedListenerBinds_AreLoopbackOnly) + " cannot see.");
        }

        [Test]
        public void NoLogCall_CarriesTheBrokerToken()
        {
            var violations = new List<string>();

            foreach (var relative in TokenBearingSources)
            {
                var path = Path.Combine(OptionalModuleGuardTests.PackageRoot(), relative);
                Assert.IsTrue(File.Exists(path),
                    "A token-bearing source moved, so this scan no longer covers it: " + path);

                var lines = CSharpMemberEditor.Mask(File.ReadAllText(path)).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!Regex.IsMatch(lines[i], @"\b(Debug\.Log\w*|Log)\s*\("))
                        continue;
                    // Arguments is the spawn command line, which carries --token. The lookbehind
                    // keeps the CancellationToken idiom out: cts.Token is qualified, every real
                    // token identifier here (Token, _token, token, spawnToken) is not.
                    if (!Regex.IsMatch(lines[i],
                            @"(?<![\w.])_?[Tt]oken\b|BuildSpawnArguments|\bArguments\b"))
                        continue;

                    violations.Add($"{Path.GetFileName(path)}:{i + 1} -- {lines[i].Trim()}");
                }
            }

            Assert.IsEmpty(violations,
                "SECURITY.md promises broker tokens never reach a log. Log the pid and the port "
                + "instead:\n" + string.Join("\n", violations));
        }

        // Editor/ plus the broker text: that is the shipped server. Tests/ stands up listeners of
        // its own on purpose - HttpMCPTransportLifecycleTests fakes a server to hold a port.
        private static IEnumerable<(string Name, string Masked)> ShippedSources()
        {
            var root = OptionalModuleGuardTests.PackageRoot();

            foreach (var file in Directory.GetFiles(
                         Path.Combine(root, "Editor"), "*.cs", SearchOption.AllDirectories))
                yield return (Path.GetFileName(file), CSharpMemberEditor.Mask(File.ReadAllText(file)));

            var broker = Path.Combine(root, "Editor/MCP/Server/Broker/keepalive-broker.cs.txt");
            Assert.IsTrue(File.Exists(broker),
                "The broker source moved, so the listener scan no longer covers it: " + broker);
            yield return (Path.GetFileName(broker), CSharpMemberEditor.Mask(File.ReadAllText(broker)));
        }
    }
}
