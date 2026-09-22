// Copyright (C) KitWright. All rights reserved.

using System.Linq;
using KitWright.Editor.Tools;
using NUnit.Framework;
using UnityEngine;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The uGUI builders: a canvas and the button, text and image that go under it, plus the rect
    /// transform tool that positions them. None had a test, and between them they are how an agent
    /// builds a screen - a canvas made without its scaler, or a child anchored to nothing, is a screen
    /// that looks right in the inspector and wrong on a device.
    /// The types are matched by name rather than referenced, so this compiles with or without a direct
    /// reference to UnityEngine.UI.
    /// </summary>
    public sealed class UICreationToolsTests
    {
        private const string Canvas = "KwUiCanvas";
        private const string Button = "KwUiButton";
        private const string Label = "KwUiLabel";
        private const string Image = "KwUiImage";

        [SetUp]
        public void RequireTheUiTools()
        {
            if (ToolRegistry.GetMethod("create_canvas") == null)
                Assert.Ignore("The uGUI tools are compiled out without the com.unity.ugui package.");
        }

        [TearDown]
        public void DestroyTheCanvas()
        {
            var canvas = GameObject.Find(Canvas);
            if (canvas != null)
                Object.DestroyImmediate(canvas);
        }

        private static GameObject Find(string name)
        {
            var found = GameObject.Find(name);
            Assert.IsNotNull(found, $"'{name}' is not in the scene.");
            return found;
        }

        private static Component ComponentNamed(GameObject go, string typeName)
        {
            var component = go.GetComponents<Component>()
                .FirstOrDefault(c => c != null && c.GetType().Name == typeName);

            Assert.IsNotNull(component,
                $"'{go.name}' carries [{string.Join(", ", go.GetComponents<Component>().Select(c => c == null ? "null" : c.GetType().Name))}], no {typeName}.");
            return component;
        }

        private static string TextOf(Component component) =>
            component.GetType().GetProperty("text")?.GetValue(component) as string;

        [Test]
        public void CreateCanvasMakesAScalerAndARaycasterTooBecauseAScreenNeedsBoth()
        {
            Ok("create_canvas", "name", Canvas);

            var canvas = Find(Canvas);
            ComponentNamed(canvas, "Canvas");
            ComponentNamed(canvas, "CanvasScaler");
            ComponentNamed(canvas, "GraphicRaycaster");
        }

        [Test]
        public void ButtonTextAndImageAllLandUnderTheCanvasTheyNamed()
        {
            Ok("create_canvas", "name", Canvas);

            Ok("create_button", "name", Button, "text", "Play", "parent_name", Canvas, "size", "200,50");
            var button = Find(Button);
            Assert.AreEqual(Canvas, button.transform.parent.name, "The button belongs under the canvas it named.");
            ComponentNamed(button, "Button");
            Assert.AreEqual("Play", TextOf(ComponentNamed(button.transform.GetChild(0).gameObject, "Text")),
                "The label the caller asked for has to end up in the button's own text child.");

            Ok("create_text", "name", Label, "text", "Score: 0", "parent_name", Canvas, "font_size", "24");
            Assert.AreEqual("Score: 0", TextOf(ComponentNamed(Find(Label), "Text")));

            Ok("create_image", "name", Image, "parent_name", Canvas, "color", "1,0,0,1", "size", "64,64");
            var image = ComponentNamed(Find(Image), "Image");
            Assert.AreEqual(Color.red, (Color)image.GetType().GetProperty("color").GetValue(image));

            Refused("create_button", "name", "KwOrphan", "text", "x", "parent_name", "KwNoSuchCanvas");
        }

        [Test]
        public void SetRectTransformAppliesTheAnchorPositionAndSizeItWasGiven()
        {
            Ok("create_canvas", "name", Canvas);
            Ok("create_image", "name", Image, "parent_name", Canvas);

            Ok("set_rect_transform",
                "target", Image, "anchor", "top-left", "anchored_position", "12,-34", "size", "80,90",
                "pivot", "0,1");

            var rect = (RectTransform)Find(Image).transform;
            Assert.AreEqual(new Vector2(0f, 1f), rect.anchorMin, "top-left anchors both corners to the top left.");
            Assert.AreEqual(new Vector2(0f, 1f), rect.anchorMax);
            Assert.AreEqual(new Vector2(12f, -34f), rect.anchoredPosition);
            Assert.AreEqual(new Vector2(80f, 90f), rect.sizeDelta);
            Assert.AreEqual(new Vector2(0f, 1f), rect.pivot);

            Refused("set_rect_transform", "target", "KwNothingCalledThis", "size", "10,10");
        }
    }
}
