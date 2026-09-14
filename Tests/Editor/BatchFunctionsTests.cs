// Copyright (C) KitWright. Licensed under MIT.

using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class BatchFunctionsTests
    {
        [Test]
        public void IsSuccess_ImageDataUri_CountsAsSuccess()
        {
            const string raw = "data:image/png;base64,iVBORw0KGgo=";

            Assert.IsTrue(BatchFunctions.IsSuccess(raw, raw),
                "a screenshot never carries a success field, and stop_on_error must not read that as a failure");
        }

        [Test]
        public void IsSuccess_ReadsTheEnvelopeForEverythingElse()
        {
            Assert.IsTrue(BatchFunctions.IsSuccess("{\"success\":true}", JToken.Parse("{\"success\":true}")));
            Assert.IsFalse(BatchFunctions.IsSuccess("{\"success\":false}", JToken.Parse("{\"success\":false}")));
            Assert.IsFalse(BatchFunctions.IsSuccess("not json", "not json"));
        }
    }
}
