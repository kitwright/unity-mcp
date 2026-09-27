// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using KitWright.Editor.MCP.Server;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class ServerTokenTests
    {
        // Built rather than written out: a 32-char hex literal reads as a real leaked key to the
        // secret scanner, and a test fixture is not worth teaching it to ignore.
        private static readonly string Token = new string('a', ServerToken.Length);

        [Test]
        public void AMintedTokenIsLongEnoughToBeWorthPresenting()
        {
            var first = ServerToken.Mint();
            var second = ServerToken.Mint();

            Assert.AreEqual(ServerToken.Length, first.Length);
            Assert.AreNotEqual(first, second, "Two mints must not collide.");
        }

        [Test]
        public void TheTokenIsReadOutOfThePathBesideTheProjectPin()
        {
            Assert.AreEqual(Token, ServerToken.ExtractToken($"/p/abcd1234/t/{Token}/"));
            Assert.AreEqual(Token, ServerToken.ExtractToken($"/p/abcd1234/t/{Token}/?x=1"));
            Assert.AreEqual(Token, ServerToken.ExtractToken($"/T/{Token}"),
                "The marker matches case-insensitively, like the pin's does.");

            Assert.AreEqual(string.Empty, ServerToken.ExtractToken("/p/abcd1234/"));
            Assert.AreEqual(string.Empty, ServerToken.ExtractToken("/"));
            Assert.AreEqual(string.Empty, ServerToken.ExtractToken(null));
            Assert.AreEqual(string.Empty, ServerToken.ExtractToken("/t"), "A marker with nothing after it is no token.");
        }

        [Test]
        public void APathCarryingTheRightTokenPasses()
        {
            Assert.AreEqual(ServerToken.Verdict.Ok, ServerToken.Check($"/p/abcd1234/t/{Token}/", Token));
            Assert.AreEqual(ServerToken.Verdict.Mismatch, ServerToken.Check($"/p/abcd1234/t/{Token.ToUpperInvariant()}/", Token),
                "The token is compared exactly, as the broker compares it.");
            Assert.AreEqual(ServerToken.Verdict.Mismatch, ServerToken.Check($"/t/{Token}a/", Token));
        }

        [Test]
        public void AMissingTokenAndAWrongOneAreDifferentAnswers()
        {
            // Both are refused, but they need different advice: a missing token is a config written
            // before tokens existed, a wrong one is a config left over from a rotated token.
            Assert.AreEqual(ServerToken.Verdict.Missing, ServerToken.Check("/p/abcd1234/", Token));
            Assert.AreEqual(ServerToken.Verdict.Mismatch, ServerToken.Check("/p/abcd1234/t/deadbeef/", Token));
            Assert.AreNotEqual(ServerToken.RefusalMessage(ServerToken.Verdict.Missing),
                ServerToken.RefusalMessage(ServerToken.Verdict.Mismatch));
        }

        [Test]
        public void ALoggedUrlDoesNotCarryTheToken()
        {
            var redacted = ServerToken.Redact($"http://127.0.0.1:8765/p/abcd1234/t/{Token}/");

            Assert.AreEqual("http://127.0.0.1:8765/p/abcd1234/t/<token>/", redacted);
            Assert.AreEqual("http://127.0.0.1:8765/p/abcd1234/", ServerToken.Redact("http://127.0.0.1:8765/p/abcd1234/"));
        }

        [Test]
        public void GitWouldCommitAConfigUnlessItIsIgnoredAndUntracked()
        {
            var repo = Path.Combine(Path.GetTempPath(), "kw-token-git-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repo);
            var config = Path.Combine(repo, "config.toml");
            File.WriteAllText(config, "x");

            try
            {
                Assume.That(RunGit(repo, "rev-parse --git-dir"), Is.Not.EqualTo(0).And.Not.EqualTo(-1),
                    "git on PATH and a temp folder outside any repository are required.");
                Assert.IsFalse(ServerToken.GitWouldCommit(config), "outside a repository there is nothing to commit into");

                Assert.AreEqual(0, RunGit(repo, "init -q"));
                Assert.IsTrue(ServerToken.GitWouldCommit(config),
                    "an untracked file nothing ignores goes in with the next `git add .`");

                File.WriteAllText(Path.Combine(repo, ".gitignore"), "config.toml\n");
                Assert.IsFalse(ServerToken.GitWouldCommit(config));

                Assert.AreEqual(0, RunGit(repo, "add -f config.toml"));
                Assert.IsTrue(ServerToken.GitWouldCommit(config), "a tracked file is committed whatever .gitignore says");
            }
            finally
            {
                // git marks its object files read-only, which Directory.Delete refuses on Windows.
                foreach (var file in Directory.GetFiles(repo, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(repo, true);
            }
        }

        private static int RunGit(string directory, string arguments)
        {
            var start = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            try
            {
                using (var git = Process.Start(start))
                {
                    git.WaitForExit();
                    return git.ExitCode;
                }
            }
            catch (Win32Exception)
            {
                return -1;
            }
        }

        [Test]
        public void AServerWithNoTokenAsksForNone()
        {
            Assert.AreEqual(ServerToken.Verdict.Ok, ServerToken.Check("/", string.Empty));
            Assert.AreEqual(ServerToken.Verdict.Ok, ServerToken.Check("/p/abcd1234/t/anything/", null));
        }
    }
}
