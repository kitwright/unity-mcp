// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    public sealed class SharedParsingTests
    {
        [TestCase("NaN,0,0")]
        [TestCase("0,Infinity,0")]
        [TestCase("0,0,-Infinity")]
        [TestCase("a,b,c")]
        [TestCase("1,2")]
        public void TryParseVector3_RefusesWhatUnityWouldDropOrCannotRead(string text)
        {
            Assert.IsFalse(ValueConverter.TryParseVector3(text, out _, out var error));
            Assert.IsNotEmpty(error);
        }

        [TestCase("NaN,0")]
        [TestCase("Infinity,1")]
        [TestCase("1")]
        public void TryParseVector2_RefusesWhatUnityWouldDropOrCannotRead(string text) =>
            Assert.IsFalse(ValueConverter.TryParseVector2(text, out _, out _));

        [Test]
        public void TryParseVector_ReadsPlainAndParenthesisedNumbers()
        {
            Assert.IsTrue(ValueConverter.TryParseVector3("(1, 2.5, -3)", out var v3, out _));
            Assert.AreEqual(new Vector3(1f, 2.5f, -3f), v3);
            Assert.IsTrue(ValueConverter.TryParseVector2("4,5", out var v2, out _));
            Assert.AreEqual(new Vector2(4f, 5f), v2);
        }

        [TestCase("Linear", true)]
        [TestCase("exponential", true)]
        [TestCase(" ExponentialSquared ", true)]
        [TestCase("Linear,Exponential", false)]
        [TestCase("1", false)]
        [TestCase("99", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("Banana", false)]
        public void TryParseEnum_TakesAMemberNameAndNothingElse(string text, bool expected) =>
            Assert.AreEqual(expected, ValueConverter.TryParseEnum<FogMode>(text, out _));

        [Test]
        public void TryParseEnum_ReturnsTheMember()
        {
            Assert.IsTrue(ValueConverter.TryParseEnum<FogMode>("exponential", out var mode));
            Assert.AreEqual(FogMode.Exponential, mode);
        }

        [Test]
        public void JsonParse_KeepsADateLikeStringAsTheClientWroteIt()
        {
            const string text = "2025-10-31T10:00:00";
            var obj = JsonParse.Object("{\"when\":\"" + text + "\"}");
            Assert.AreEqual(JTokenType.String, obj["when"].Type);
            Assert.AreEqual(text, (string)obj["when"]);

            var array = JsonParse.Array("[{\"args\":{\"when\":\"" + text + "\"}}]");
            Assert.AreEqual(text, (string)array[0]["args"]["when"]);
        }

        [TestCase("{\"a\":1} trailing")]
        [TestCase("{\"a\":1}{\"b\":2}")]
        [TestCase("")]
        public void JsonParse_RefusesAnythingButOneObject(string text) =>
            Assert.Throws<JsonReaderException>(() => JsonParse.Object(text));

        [TestCase("{\"a\":1} // note")]
        [TestCase("{\"a\":1} /* note */")]
        public void JsonParse_AcceptsATrailingComment(string text) =>
            Assert.AreEqual(1, (int)JsonParse.Object(text)["a"]);

        [Test]
        public void JsonParse_TrailingContentErrorSaysWhere()
        {
            var ex = Assert.Throws<JsonReaderException>(() => JsonParse.Object("{\"a\":1} x"));
            StringAssert.Contains("position", ex.Message);
        }

        [Test]
        public void JsonParse_TokenKeepsADateLikeStringAsWritten()
        {
            var token = JsonParse.Token("{\"data\":{\"when\":\"2025-10-31T10:00:00.50Z\"}}");
            Assert.AreEqual("2025-10-31T10:00:00.50Z", (string)token["data"]["when"]);
        }

        [Test]
        public void JsonParse_ArrayRefusesAnObject() =>
            Assert.Throws<JsonReaderException>(() => JsonParse.Array("{\"a\":1}"));

        [TestCase("NaN")]
        [TestCase("Infinity")]
        [TestCase("-Infinity")]
        public void Invoker_RefusesANonFiniteNumberForAnyFloatParameter(string value) =>
            Assert.AreEqual("INVALID_PARAM", Code("set_scene_view_camera", "size", value));
    }
}
