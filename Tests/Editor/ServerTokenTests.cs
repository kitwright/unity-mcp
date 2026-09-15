// Copyright (C) KitWright. Licensed under MIT.

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
            Assert.AreEqual(ServerToken.Verdict.Ok, ServerToken.Check($"/p/abcd1234/t/{Token.ToUpperInvariant()}/", Token));
        }

        [Test]
        public void AMissingTokenAndAWrongOneAreDifferentAnswers()
        {
            // They get different treatment: a config written before tokens existed carries none and
            // is still served with a warning, while a token that is not this project's is refused.
            Assert.AreEqual(ServerToken.Verdict.Missing, ServerToken.Check("/p/abcd1234/", Token));
            Assert.AreEqual(ServerToken.Verdict.Mismatch, ServerToken.Check("/p/abcd1234/t/deadbeef/", Token));
        }

        [Test]
        public void AServerWithNoTokenAsksForNone()
        {
            Assert.AreEqual(ServerToken.Verdict.Ok, ServerToken.Check("/", string.Empty));
            Assert.AreEqual(ServerToken.Verdict.Ok, ServerToken.Check("/p/abcd1234/t/anything/", null));
        }
    }
}
