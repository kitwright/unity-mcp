// Copyright (C) KitWright. Licensed under MIT.

using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class EditorStateFunctionsTests
    {
        // get_time_scale was removed in favour of this field. Without it there is no tool that
        // reports the time scale at all, and nothing else would fail if it were dropped again.
        [Test]
        public void GetEditorState_ReportsTheTimeScale()
        {
            var response = JObject.FromObject(EditorStateFunctions.GetEditorState());

            Assert.IsTrue(response.Value<bool>("success"), response.ToString());
            var timeScale = response["data"]["timeScale"];
            Assert.IsNotNull(timeScale, "get_editor_state is the only reader of Time.timeScale now.");
            Assert.AreEqual(Time.timeScale, timeScale.Value<float>(), 0.0001f);
        }

        // Several editors can hold the same project name, so an agent confirming which server it
        // reached needs the path as well.
        [Test]
        public void GetEditorState_IdentifiesTheProjectByPathNotJustName()
        {
            var data = JObject.FromObject(EditorStateFunctions.GetEditorState())["data"];

            Assert.IsFalse(string.IsNullOrEmpty(data.Value<string>("projectName")));
            Assert.IsFalse(string.IsNullOrEmpty(data.Value<string>("projectPath")));
        }
    }
}
