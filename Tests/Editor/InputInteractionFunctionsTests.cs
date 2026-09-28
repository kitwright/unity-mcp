// Copyright (C) KitWright. All rights reserved.

using System.Collections;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace KitWright.Editor.Tests
{
    public sealed class InputInteractionFunctionsTests
    {
        // Static, not a captured local: a lambda's closure is allocated when the method starts,
        // before EnterPlayMode reloads the domain, and the restored iterator gets it back as null.
        private static int s_clicks;

        private static void CountClick() => s_clicks++;

        // One Play Mode session for every case: simulate_mouse_click refuses outside it, and each
        // entry costs seconds. The canvas sorts above anything the open scene brings along.
        [UnityTest]
        public IEnumerator SimulateMouseClick_PressesAButtonOnceAndOnlyWhenUguiWould()
        {
            yield return new EnterPlayMode();
            s_clicks = 0;

            if (EventSystem.current == null)
                new GameObject("KwClickEventSystem", typeof(EventSystem));

            var canvasGo = new GameObject("KwClickCanvas", typeof(Canvas), typeof(GraphicRaycaster), typeof(CanvasGroup));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            var group = canvasGo.GetComponent<CanvasGroup>();

            var buttonGo = new GameObject("KwClickButton", typeof(RectTransform), typeof(Image), typeof(Button));
            Stretch(buttonGo, canvasGo.transform);
            var button = buttonGo.GetComponent<Button>();
            button.onClick.AddListener(CountClick);

            var labelGo = new GameObject("KwClickLabel", typeof(RectTransform), typeof(Text));
            Stretch(labelGo, buttonGo.transform);
            var label = labelGo.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.text = "Label";
            label.raycastTarget = false;

            // One frame, so the canvas has been drawn and its graphics have a depth to raycast by.
            yield return null;

            int x = Screen.width / 2, y = Screen.height / 2;

            var answer = InputInteractionFunctions.SimulateMouseClick(x, y);
            if (Application.isBatchMode && !answer.Contains("EventSystem: clicked on"))
                Assert.Ignore("A batchmode run can have no screen for an overlay canvas to fill, so nothing " +
                              "is under the point to click: " + answer);

            StringAssert.Contains("clicked on 'KwClickButton'", answer);
            Assert.AreEqual(1, s_clicks, "A hit on the Button's own Image fires onClick once. " + answer);

            foreach (var other in new[] { "right", "middle" })
            {
                answer = InputInteractionFunctions.SimulateMouseClick(x, y, other);
                Assert.AreEqual(1, s_clicks, $"A uGUI Button does not react to the {other} button. " + answer);
                StringAssert.Contains("left button only", answer);
            }

            label.raycastTarget = true;
            answer = InputInteractionFunctions.SimulateMouseClick(x, y);
            StringAssert.Contains("clicked on 'KwClickLabel'", answer);
            Assert.AreEqual(2, s_clicks, "A hit on the child Text bubbles to the Button and fires it once. " + answer);

            label.raycastTarget = false;
            button.interactable = false;
            answer = InputInteractionFunctions.SimulateMouseClick(x, y);
            Assert.AreEqual(2, s_clicks, "A non-interactable Button is not pressed. " + answer);
            StringAssert.Contains("'KwClickButton' is not interactable", answer);

            button.interactable = true;
            group.interactable = false;
            yield return null;

            answer = InputInteractionFunctions.SimulateMouseClick(x, y);
            Assert.AreEqual(2, s_clicks, "A CanvasGroup that is not interactable disables the Button under it. " + answer);
            StringAssert.Contains("'KwClickButton' is not interactable", answer);

            yield return new ExitPlayMode();
        }

        // The fallback answers when no raycast did, so it has to stand in for one: the button on top,
        // measured with its canvas's camera, and nothing when something covers it.
        [UnityTest]
        public IEnumerator DirectButtonFallback_PressesOnlyTheButtonARaycastWouldHaveHit()
        {
            yield return new EnterPlayMode();

            var cameraGo = new GameObject("KwFallbackCamera", typeof(Camera));
            var cameraCanvas = NewCanvas("KwCameraCanvas", RenderMode.ScreenSpaceCamera, 0);
            cameraCanvas.worldCamera = cameraGo.GetComponent<Camera>();
            NewButton("KwCameraButton", cameraCanvas);
            yield return null;

            var point = new Vector2(Screen.width / 2f, Screen.height / 2f);
            var answer = Fallback(point);
            StringAssert.Contains("invoked 'KwCameraButton'", answer,
                "a camera canvas's rect is in world space, so it has to be measured with that camera");

            NewButton("KwUnder", NewCanvas("KwUnderCanvas", RenderMode.ScreenSpaceOverlay, 32000));
            NewButton("KwOver", NewCanvas("KwOverCanvas", RenderMode.ScreenSpaceOverlay, 32001));
            yield return null;

            answer = Fallback(point);
            StringAssert.Contains("invoked 'KwOver'", answer, "the button drawn on top, not the first one the scene lists");

            var cover = new GameObject("KwCover", typeof(RectTransform), typeof(Image));
            Stretch(cover, NewCanvas("KwCoverCanvas", RenderMode.ScreenSpaceOverlay, 32002).transform);
            yield return null;

            answer = Fallback(point);
            StringAssert.Contains("'KwOver' is covered by 'KwCover', not invoked", answer);

            yield return new ExitPlayMode();
        }

        private static string Fallback(Vector2 point)
        {
            var results = new System.Text.StringBuilder();
            InputInteractionFunctions.AppendDirectButtonFallback(results, point, PointerEventData.InputButton.Left);
            return results.ToString();
        }

        private static Canvas NewCanvas(string name, RenderMode mode, int order)
        {
            var canvas = new GameObject(name, typeof(Canvas)).GetComponent<Canvas>();
            canvas.renderMode = mode;
            canvas.sortingOrder = order;
            return canvas;
        }

        private static void NewButton(string name, Canvas canvas)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            Stretch(go, canvas.transform);
        }

        private static void Stretch(GameObject child, Transform parent)
        {
            var rect = child.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
        }
    }
}
