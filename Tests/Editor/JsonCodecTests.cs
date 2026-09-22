// Copyright (C) KitWright. All rights reserved.

using System.Collections.Generic;
using KitWright.Editor.MCP.Server;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class JsonCodecTests
    {
        [Test]
        public void Serialize_WritesAnonymousTypesAsObjectsNotAsQuotedBlobs()
        {
            var json = JsonCodec.Serialize(new { jsonrpc = "2.0", id = 7, done = true });

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"id\":7,\"done\":true}", json);
            Assert.That(json, Does.Not.Contain("="),
                "An anonymous type that reaches ToString() arrives as \"{ jsonrpc = 2.0 }\".");
        }

        [Test]
        public void Serialize_RecursesThroughNestedAnonymousTypesAndCollections()
        {
            var json = JsonCodec.Serialize(new
            {
                method = "notifications/message",
                @params = new { level = "error", tags = new List<string> { "a", "b" } }
            });

            Assert.AreEqual(
                "{\"method\":\"notifications/message\"," +
                "\"params\":{\"level\":\"error\",\"tags\":[\"a\",\"b\"]}}",
                json);
        }

        [Test]
        public void Serialize_KeepsTheStringFallbackForTypesUnityFormatsItself()
        {
            Assert.AreEqual("\"Warning\"", JsonCodec.Serialize(LogType.Warning));

            var vector = JsonCodec.Serialize(new Vector3(1f, 2f, 3f));
            Assert.That(vector, Does.StartWith("\"(").And.EndWith(")\""));
            Assert.That(vector, Does.Not.Contain("normalized"),
                "Reflecting over a Vector3 would recurse forever through its normalized property.");
        }

        [Test]
        public void Serialize_HandlesDictionariesListsAndScalars()
        {
            Assert.AreEqual("null", JsonCodec.Serialize(null));
            Assert.AreEqual("\"a\\\"b\"", JsonCodec.Serialize("a\"b"));
            Assert.AreEqual("[1,2]", JsonCodec.Serialize(new List<object> { 1, 2 }));
            Assert.AreEqual("{\"k\":\"v\"}",
                JsonCodec.Serialize(new Dictionary<string, object> { ["k"] = "v" }));
        }

        [Test]
        public void Serialize_EscapesControlCharsSoAReflectedNullByteStaysValidJson()
        {
            Assert.AreEqual("\"a\\u0000b\"", JsonCodec.Serialize("a\0b"));
            Assert.AreEqual("\"\\u001f\"", JsonCodec.Serialize("\u001f"));
            Assert.AreEqual("\"\\n\\t\"", JsonCodec.Serialize("\n\t"),
                "Named escapes must not regress to \\u form.");
        }

        [Test]
        public void Serialize_DropsNonFiniteFloatsToNullSoTheResponseStaysValidJson()
        {
            Assert.AreEqual("null", JsonCodec.Serialize(double.PositiveInfinity));
            Assert.AreEqual("null", JsonCodec.Serialize(double.NegativeInfinity));
            Assert.AreEqual("null", JsonCodec.Serialize(double.NaN));
            Assert.AreEqual("null", JsonCodec.Serialize(float.PositiveInfinity));
            Assert.AreEqual("1.5", JsonCodec.Serialize(1.5), "Finite floats must still serialize.");
        }

        [Test]
        public void Serialize_EmitsBareNumbersForEveryIntegerType()
        {
            // Only int/long were caught before; the rest fell through to the string fallback and
            // reached the client as a quoted string ("42") instead of a number.
            Assert.AreEqual("42", JsonCodec.Serialize((uint)42));
            Assert.AreEqual("42", JsonCodec.Serialize((ulong)42));
            Assert.AreEqual("5", JsonCodec.Serialize((byte)5));
            Assert.AreEqual("-5", JsonCodec.Serialize((sbyte)-5));
            Assert.AreEqual("3", JsonCodec.Serialize((short)3));
            Assert.AreEqual("7", JsonCodec.Serialize((ushort)7));
            Assert.AreEqual("1.5", JsonCodec.Serialize(1.5m));
        }

        [Test]
        public void Serialize_ThrowsInsteadOfOverflowingTheStackOnDeepNesting()
        {
            object nested = "leaf";
            for (int i = 0; i < 200; i++)
                nested = new List<object> { nested };

            Assert.That(() => JsonCodec.Serialize(nested),
                Throws.Exception.With.Message.Contains("too deep"),
                "A deep or cyclic graph must fail catchably, not StackOverflow the editor.");
        }

        [Test]
        public void Deserialize_KeepsBraceAndBracketCharactersInsideNestedStringValues()
        {
            // FindValueEnd counted braces/brackets without skipping string bodies, so a nested value
            // like {"opts":{"note":"a}b"}} was truncated at the brace inside the string -- the codec
            // could not even round-trip its own output.
            var obj = (Dictionary<string, object>)JsonCodec.Deserialize("{\"opts\":{\"note\":\"a}b\"}}");
            var inner = (Dictionary<string, object>)obj["opts"];
            Assert.AreEqual("a}b", inner["note"]);

            var arr = (Dictionary<string, object>)JsonCodec.Deserialize("{\"arr\":[\"x]y\"]}");
            var list = (List<object>)arr["arr"];
            Assert.AreEqual("x]y", list[0]);

            var original = new Dictionary<string, object>
            {
                ["opts"] = new Dictionary<string, object> { ["note"] = "close } here" }
            };
            var roundTripped = (Dictionary<string, object>)JsonCodec.Deserialize(JsonCodec.Serialize(original));
            Assert.AreEqual("close } here", ((Dictionary<string, object>)roundTripped["opts"])["note"]);
        }
    }
}
