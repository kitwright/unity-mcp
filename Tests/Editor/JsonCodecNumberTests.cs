// Copyright (C) KitWright. Licensed under MIT.

using System.Collections.Generic;
using KitWright.Editor.MCP.Server;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class JsonCodecNumberTests
    {
        private static object Parse(string literal) =>
            ((Dictionary<string, object>)JsonCodec.Deserialize("{\"n\":" + literal + "}"))["n"];

        [Test]
        public void Deserialize_KeepsAnIntegerBeyondDoublePrecisionExact()
        {
            var value = Parse("9007199254740993");

            Assert.IsInstanceOf<long>(value, "a double rounds this id to ...992");
            Assert.AreEqual(9007199254740993L, (long)value);
            Assert.AreEqual("{\"n\":9007199254740993}",
                JsonCodec.Serialize(new Dictionary<string, object> { ["n"] = value }));
        }

        [TestCase("-1", -1L)]
        [TestCase("0", 0L)]
        [TestCase("42", 42L)]
        [TestCase("-9223372036854775808", long.MinValue)]
        [TestCase("9223372036854775807", long.MaxValue)]
        public void Deserialize_ReadsAnIntegerLiteralAsLongAndWritesItBackUnchanged(string literal, long expected)
        {
            var value = Parse(literal);

            Assert.IsInstanceOf<long>(value);
            Assert.AreEqual(expected, (long)value);
            Assert.AreEqual(literal, JsonCodec.Serialize(value));
        }

        [Test]
        public void Deserialize_ReadsAnIntegerThatFitsOnlyUlongAsUlong()
        {
            var value = Parse("18446744073709551615");

            Assert.IsInstanceOf<ulong>(value);
            Assert.AreEqual(ulong.MaxValue, (ulong)value);
            Assert.AreEqual("18446744073709551615", JsonCodec.Serialize(value));
        }

        [TestCase("18446744073709551616")]
        [TestCase("-9223372036854775809")]
        [TestCase("123456789012345678901234567890")]
        public void Deserialize_ReadsAnIntegerBeyondUlongAsDoubleAsBefore(string literal)
        {
            Assert.IsInstanceOf<double>(Parse(literal));
        }

        [TestCase("1.5", 1.5)]
        [TestCase("1e3", 1000d)]
        [TestCase("1E3", 1000d)]
        [TestCase("-0.25", -0.25)]
        [TestCase("2.0", 2d)]
        public void Deserialize_KeepsAFractionOrExponentAsDouble(string literal, double expected)
        {
            var value = Parse(literal);

            Assert.IsInstanceOf<double>(value);
            Assert.AreEqual(expected, (double)value);
        }

        [Test]
        public void RoundTrip_WritesIntegersWithoutADecimalPointOrExponent()
        {
            var parsed = JsonCodec.Deserialize("{\"a\":9007199254740993,\"b\":-1,\"c\":1.5,\"d\":1e3}");

            Assert.AreEqual("{\"a\":9007199254740993,\"b\":-1,\"c\":1.5,\"d\":1000}", JsonCodec.Serialize(parsed));
        }

        [Test]
        public void Deserialize_ReadsIntegersInsideAnArrayAndAtTopLevel()
        {
            var list = (List<object>)JsonCodec.Deserialize("[1, 2.5, 9007199254740993]");

            Assert.IsInstanceOf<long>(list[0]);
            Assert.IsInstanceOf<double>(list[1]);
            Assert.AreEqual(9007199254740993L, (long)list[2]);
            Assert.IsInstanceOf<long>(JsonCodec.Deserialize("7"));
        }

        [Test]
        public void Deserialize_LeavesTextThatIsNotANumberAlone()
        {
            Assert.AreEqual("-", JsonCodec.Deserialize("-"));
            Assert.AreEqual("abc", JsonCodec.Deserialize("abc"));
        }
    }
}
