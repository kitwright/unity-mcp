// Copyright (C) KitWright. Licensed under MIT.

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using static KitWright.Editor.Tests.ToolCall;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;

namespace KitWright.Editor.Tests
{
    public sealed class UiToolFixesTests
    {
        private sealed class TitledWindow : EditorWindow
        {
        }

        private readonly List<string> _names = new List<string>();
        private readonly List<GameObject> _hiddenEventSystems = new List<GameObject>();
        private EventSystem[] _eventSystemsBefore;
        private UnityEngine.Object[] _selectionBefore;

        [SetUp]
        public void HideTheScenesEventSystem()
        {
            if (ToolRegistry.GetMethod("create_canvas") == null)
                Assert.Ignore("The uGUI tools are compiled out without the com.unity.ugui package.");

            _selectionBefore = Selection.objects;
            _eventSystemsBefore = ObjectsHelper.FindObjectsByTypeUnsorted<EventSystem>(FindObjectsInactive.Include);

            // The creators only look for an active EventSystem, so hiding it makes "none in the scene" deterministic.
            foreach (var eventSystem in ObjectsHelper.FindObjectsByTypeUnsorted<EventSystem>(FindObjectsInactive.Exclude))
            {
                eventSystem.gameObject.SetActive(false);
                _hiddenEventSystems.Add(eventSystem.gameObject);
            }
        }

        [TearDown]
        public void LeaveNothingBehind()
        {
            foreach (var name in _names)
            {
                for (var go = GameObject.Find(name); go != null; go = GameObject.Find(name))
                    UnityEngine.Object.DestroyImmediate(go);
            }
            _names.Clear();

            if (_eventSystemsBefore != null)
            {
                foreach (var eventSystem in ObjectsHelper.FindObjectsByTypeUnsorted<EventSystem>(FindObjectsInactive.Include))
                {
                    if (!_eventSystemsBefore.Contains(eventSystem))
                        UnityEngine.Object.DestroyImmediate(eventSystem.gameObject);
                }
            }

            foreach (var go in _hiddenEventSystems)
            {
                if (go != null)
                    go.SetActive(true);
            }
            _hiddenEventSystems.Clear();

            if (_selectionBefore != null)
                Selection.objects = _selectionBefore;
        }

        private string Name(string stem)
        {
            var name = stem + "_" + System.Guid.NewGuid().ToString("N");
            _names.Add(name);
            return name;
        }

        private GameObject MakeCanvas()
        {
            var name = Name("KwUiCanvas");
            Ok("create_canvas", "name", name);
            return GameObject.Find(name);
        }

        private static RectTransform RectOf(string name)
        {
            var go = GameObject.Find(name);
            Assert.IsNotNull(go, $"'{name}' was not created.");
            return (RectTransform)go.transform;
        }

        [Test]
        public void ChooseInputModuleType_UsesTheInputSystemModuleOnceTheNewHandlerIsActive()
        {
            var inputSystemModule = typeof(BaseInputModule);

            Assert.AreEqual(typeof(StandaloneInputModule), UIFunctions.ChooseInputModuleType(0, inputSystemModule));
            Assert.AreEqual(inputSystemModule, UIFunctions.ChooseInputModuleType(1, inputSystemModule));
            Assert.AreEqual(inputSystemModule, UIFunctions.ChooseInputModuleType(2, inputSystemModule));
            Assert.AreEqual(typeof(StandaloneInputModule), UIFunctions.ChooseInputModuleType(1, null),
                "Without the Input System package there is nothing else to add.");
        }

        [Test]
        public void KeyCodeForChar_MapsDigitsToAlphaKeysInsteadOfBackspaceAndTab()
        {
            Assert.AreEqual(KeyCode.Alpha8, EditorWindowInteractionFunctions.KeyCodeForChar('8'));
            Assert.AreEqual(KeyCode.Alpha9, EditorWindowInteractionFunctions.KeyCodeForChar('9'));
            Assert.AreEqual(KeyCode.Alpha0, EditorWindowInteractionFunctions.KeyCodeForChar('0'));
            Assert.AreEqual(KeyCode.A, EditorWindowInteractionFunctions.KeyCodeForChar('a'));
            Assert.AreEqual(KeyCode.Z, EditorWindowInteractionFunctions.KeyCodeForChar('Z'));
            Assert.AreEqual(KeyCode.None, EditorWindowInteractionFunctions.KeyCodeForChar(' '));
            Assert.AreEqual(KeyCode.None, EditorWindowInteractionFunctions.KeyCodeForChar('.'));
        }

        [TestCase("8")]
        [TestCase("999")]
        [TestCase("8,9")]
        public void SimulateEditorWindowKey_RefusesNumericKeyStringsBeforeLookingForTheWindow(string key)
        {
            Assert.AreEqual("KEY_NOT_RECOGNIZED", Code("simulate_editor_window_key", "window", Name("KwNoWindow"), "key", key));
            Assert.AreEqual("WINDOW_NOT_FOUND", Code("simulate_editor_window_key", "window", Name("KwNoWindow"), "key", "Return"),
                "A real key name is valid, so the call gets as far as the missing window.");
        }

        [TestCase("None")]
        [TestCase("none")]
        public void SimulateEditorWindowKey_RefusesTheKeyNamedNoneBeforeLookingForTheWindow(string key)
        {
            Assert.AreEqual("KEY_NOT_RECOGNIZED", Code("simulate_editor_window_key", "window", Name("KwNoWindow"), "key", key));
        }

        [Test]
        public void SimulateEditorWindowKey_RefusesTextAndKeyTogether()
        {
            Assert.AreEqual("INVALID_INPUT",
                Code("simulate_editor_window_key", "window", Name("KwNoWindow"), "text", "a", "key", "Return"));
        }

        [TestCase("0")]
        [TestCase("-1")]
        [TestCase("11")]
        [TestCase("2000000000")]
        public void SimulateEditorWindowClick_RefusesAClickCountOutsideTheCapBeforeSendingAnything(string clickCount)
        {
            Assert.AreEqual("INVALID_PARAM",
                Code("simulate_editor_window_click", "window", Name("KwNoWindow"), "x", "1", "y", "1", "click_count", clickCount));
            Assert.AreEqual("WINDOW_NOT_FOUND",
                Code("simulate_editor_window_click", "window", Name("KwNoWindow"), "x", "1", "y", "1", "click_count", "2"));
        }

        [TestCase("rigth")]
        [TestCase("r")]
        [TestCase("4")]
        public void SimulateEditorWindowClick_RefusesAnUnknownButtonInsteadOfClickingLeft(string button)
        {
            Assert.AreEqual("INVALID_PARAM",
                Code("simulate_editor_window_click", "window", Name("KwNoWindow"), "x", "1", "y", "1", "button", button));
            Assert.AreEqual("WINDOW_NOT_FOUND",
                Code("simulate_editor_window_click", "window", Name("KwNoWindow"), "x", "1", "y", "1", "button", "Right"));
        }

        [Test]
        public void TryParseButton_KnowsThreeButtonsAndTreatsBlankAsLeft()
        {
            Assert.IsTrue(EditorWindowInteractionFunctions.TryParseButton(null, out var button));
            Assert.AreEqual(0, button);
            Assert.IsTrue(EditorWindowInteractionFunctions.TryParseButton("", out button));
            Assert.AreEqual(0, button);
            Assert.IsTrue(EditorWindowInteractionFunctions.TryParseButton(" Right ", out button));
            Assert.AreEqual(1, button);
            Assert.IsTrue(EditorWindowInteractionFunctions.TryParseButton("middle", out button));
            Assert.AreEqual(2, button);
            Assert.IsFalse(EditorWindowInteractionFunctions.TryParseButton("rigth", out _));
        }

        [Test]
        public void ResolveEditorWindow_RefusesSeveralEqualMatchesAndStillTakesTheUniqueOne()
        {
            var title = Name("KwWin");
            var first = MakeWindow(title);
            var second = MakeWindow(title);
            var longer = MakeWindow(title + "_longer");
            try
            {
                var windows = new EditorWindow[] { first, second, longer };

                Assert.IsNull(EditorWindowInteractionFunctions.ResolveEditorWindow(windows, title, out var ambiguous));
                CollectionAssert.AreEquivalent(new[] { first, second }, ambiguous);

                Assert.AreSame(longer, EditorWindowInteractionFunctions.ResolveEditorWindow(windows, title + "_lo", out ambiguous));
                Assert.IsNull(ambiguous);

                Assert.IsNull(EditorWindowInteractionFunctions.ResolveEditorWindow(windows, Name("KwNothing"), out ambiguous));
                Assert.IsNull(ambiguous, "No match at all is 'not found', not 'ambiguous'.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
                UnityEngine.Object.DestroyImmediate(longer);
            }
        }

        [Test]
        public void ResolveEditorWindow_TheFocusedWindowAmongSeveralMatchesWinsAndAnotherFocusDoesNot()
        {
            var title = Name("KwWin");
            var first = MakeWindow(title);
            var second = MakeWindow(title);
            var other = MakeWindow(Name("KwOther"));
            try
            {
                var windows = new EditorWindow[] { first, second, other };

                Assert.AreSame(second, EditorWindowInteractionFunctions.ResolveEditorWindow(windows, title, out var ambiguous, second));
                Assert.IsNull(ambiguous);

                Assert.IsNull(EditorWindowInteractionFunctions.ResolveEditorWindow(windows, title, out ambiguous, other));
                CollectionAssert.AreEquivalent(new[] { first, second }, ambiguous);

                Assert.IsNull(EditorWindowInteractionFunctions.ResolveEditorWindow(windows, title, out ambiguous, null));
                CollectionAssert.AreEquivalent(new[] { first, second }, ambiguous);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
                UnityEngine.Object.DestroyImmediate(other);
            }
        }

        private static EditorWindow MakeWindow(string title)
        {
            var window = ScriptableObject.CreateInstance<TitledWindow>();
            window.titleContent = new GUIContent(title);
            return window;
        }

        [Test]
        public void TryParseRenderMode_AcceptsTheEnumNamesWithoutSeparatorsAndNothingElse()
        {
            Assert.IsTrue(UIFunctions.TryParseRenderMode("world_space", out var mode));
            Assert.AreEqual(RenderMode.WorldSpace, mode);
            Assert.IsTrue(UIFunctions.TryParseRenderMode("Screen Space-Camera", out mode));
            Assert.AreEqual(RenderMode.ScreenSpaceCamera, mode);
            Assert.IsTrue(UIFunctions.TryParseRenderMode("", out mode));
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, mode);

            foreach (var bad in new[] { "overlay", "camera", "sideways", "1", "0,1" })
                Assert.IsFalse(UIFunctions.TryParseRenderMode(bad, out _), bad);
        }

        [Test]
        public void CreateCanvas_RefusesAnUnknownRenderModeBeforeCreatingAnythingAndReportsTheRealOne()
        {
            var refused = Name("KwUiBadMode");
            var answer = Refused("create_canvas", "name", refused, "render_mode", "sideways");
            Assert.AreEqual("INVALID_PARAM", (string)answer["code"]);
            CollectionAssert.Contains(answer["data"]["valid"].Select(v => (string)v).ToArray(), "WorldSpace");
            Assert.IsNull(GameObject.Find(refused));

            var world = Name("KwUiWorld");
            var created = Ok("create_canvas", "name", world, "render_mode", "world_space");
            Assert.AreEqual(RenderMode.WorldSpace, GameObject.Find(world).GetComponent<Canvas>().renderMode);
            StringAssert.Contains("WorldSpace", (string)created["message"]);
        }

        [Test]
        public void CreateCanvas_PutsTheCanvasOnTheUiLayer()
        {
            Assert.AreEqual(LayerMask.NameToLayer("UI"), MakeCanvas().layer);
        }

        [Test]
        public void UiCreators_GiveTheNewElementItsParentsLayer()
        {
            var canvas = MakeCanvas();
            canvas.layer = 8;

            var button = Name("KwUiButton");
            var label = Name("KwUiLabel");
            var image = Name("KwUiImage");
            var slider = Name("KwUiSlider");
            Ok("create_button", "name", button, "text", "Go", "parent_name", canvas.name);
            Ok("create_text", "name", label, "text", "Hi", "parent_name", canvas.name);
            Ok("create_image", "name", image, "parent_name", canvas.name);
            Ok("create_ui_element", "kind", "slider", "name", slider, "parent_name", canvas.name);

            foreach (var root in new[] { button, label, image, slider })
            {
                foreach (var t in GameObject.Find(root).GetComponentsInChildren<Transform>(true))
                    Assert.AreEqual(8, t.gameObject.layer, $"{t.name} under {root}");
            }
        }

        [Test]
        public void UiCreators_RefuseAParentThatIsNotUnderACanvas()
        {
            var plain = new GameObject(Name("KwNotUi"));
            var withRect = new GameObject(Name("KwRectOnly"), typeof(RectTransform));
            var calls = new[]
            {
                new[] { "create_button", "text", "x" },
                new[] { "create_text", "text", "x" },
                new[] { "create_image" },
                new[] { "create_ui_element", "kind", "panel" },
            };

            foreach (var parent in new[] { plain, withRect })
            {
                foreach (var call in calls)
                {
                    var name = Name("KwNeverMade");
                    var args = call.Skip(1).Concat(new[] { "name", name, "parent_name", parent.name }).ToArray();

                    var answer = Refused(call[0], args);

                    Assert.AreEqual("NOT_UNDER_CANVAS", (string)answer["code"], $"{call[0]} under {parent.name}: {answer}");
                    Assert.AreEqual(0, parent.transform.childCount, call[0]);
                    Assert.IsNull(GameObject.Find(name), call[0]);
                }
            }
        }

        [Test]
        public void UiCreators_RefuseAParentWithoutARectTransformUnderACanvas()
        {
            var canvas = MakeCanvas();
            var plain = new GameObject(Name("KwPlainChild"));
            plain.transform.SetParent(canvas.transform, false);
            Assume.That(plain.transform is RectTransform, Is.False, "Unity converted the child to a RectTransform on its own.");

            var name = Name("KwNeverMade");
            var answer = Refused("create_button", "name", name, "text", "x", "parent_name", plain.name);

            Assert.AreEqual("NO_RECT_TRANSFORM", (string)answer["code"], answer.ToString());
            Assert.IsNull(GameObject.Find(name));
        }

        [Test]
        public void CreateButton_AddsAnEventSystemLikeTheOtherInteractiveCreators()
        {
            var canvas = new GameObject(Name("KwUiBareCanvas"), typeof(Canvas));
            Assert.IsNull(UnityEngine.Object.FindAnyObjectByType<EventSystem>(), "The fixture hides the scene's EventSystem.");

            Ok("create_button", "name", Name("KwUiButton"), "text", "Go", "parent_name", canvas.name);

            var eventSystem = UnityEngine.Object.FindAnyObjectByType<EventSystem>();
            Assert.IsNotNull(eventSystem, "A Button with no EventSystem is dead in Play Mode.");
            Assert.IsNotNull(eventSystem.GetComponent<BaseInputModule>());
        }

        [Test]
        public void CreateUiElement_PanelWithASizeAndNoAnchorIsThatSizeNotTheParentsPlusIt()
        {
            var canvas = MakeCanvas();

            var sized = Name("KwUiPanel");
            Ok("create_ui_element", "kind", "panel", "name", sized, "parent_name", canvas.name, "size", "300,200");
            var rect = RectOf(sized);
            Assert.AreEqual(rect.anchorMin, rect.anchorMax, "Stretch anchors would make sizeDelta a margin.");
            Assert.AreEqual(new Vector2(300f, 200f), rect.sizeDelta);

            var unsized = Name("KwUiPanelFill");
            Ok("create_ui_element", "kind", "panel", "name", unsized, "parent_name", canvas.name);
            Assert.AreEqual(Vector2.zero, RectOf(unsized).anchorMin, "Without a size the panel keeps Unity's fill-the-parent default.");
            Assert.AreEqual(Vector2.one, RectOf(unsized).anchorMax);
        }

        [Test]
        public void SetRectTransform_RefusesACallThatChangesNothing()
        {
            var canvas = MakeCanvas();
            var image = Name("KwUiImage");
            Ok("create_image", "name", image, "parent_name", canvas.name);
            var before = RectOf(image).sizeDelta;

            Assert.AreEqual("INVALID_PARAM", Code("set_rect_transform", "target", image));
            Assert.AreEqual("INVALID_PARAM",
                Code("set_rect_transform", "target", image, "anchor", "", "anchored_position", "", "size", "", "pivot", ""));
            Assert.AreEqual(before, RectOf(image).sizeDelta);

            Ok("set_rect_transform", "target", image, "size", "10,20");
            Assert.AreEqual(new Vector2(10f, 20f), RectOf(image).sizeDelta);
        }

        [Test]
        public void SetRectTransform_DescriptionSaysAnAnchorPresetChangesThePivotAndStretchResetsOffsets()
        {
            var description = typeof(UIFunctions).GetMethod("SetRectTransform")
                .GetCustomAttribute<DescriptionAttribute>().Description;

            StringAssert.Contains("also sets the pivot", description);
            StringAssert.Contains("stretch-*", description);
        }

        [Test]
        public void UiCreators_AnEmptySizeMeansTheDefaultSizeNotZero()
        {
            var canvas = MakeCanvas();
            var button = Name("KwUiButton");
            var label = Name("KwUiLabel");
            var image = Name("KwUiImage");

            Ok("create_button", "name", button, "text", "Go", "parent_name", canvas.name, "size", "");
            Ok("create_text", "name", label, "text", "Hi", "parent_name", canvas.name, "size", "");
            Ok("create_image", "name", image, "parent_name", canvas.name, "size", "");

            Assert.AreEqual(new Vector2(160f, 40f), RectOf(button).sizeDelta);
            Assert.AreEqual(new Vector2(300f, 60f), RectOf(label).sizeDelta);
            Assert.AreEqual(new Vector2(100f, 100f), RectOf(image).sizeDelta);
        }

        [TestCase("0")]
        [TestCase("-5")]
        [TestCase("1001")]
        [TestCase("2000000000")]
        public void CreateText_RefusesAFontSizeOutsideOneToAThousand(string fontSize)
        {
            var canvas = MakeCanvas();
            var name = Name("KwUiLabel");

            Assert.AreEqual("INVALID_FONT_SIZE",
                Code("create_text", "name", name, "text", "x", "parent_name", canvas.name, "font_size", fontSize));
            Assert.IsNull(GameObject.Find(name));

            Ok("create_text", "name", name, "text", "x", "parent_name", canvas.name, "font_size", "1000");
        }

        [Test]
        public void UiCreators_RefuseAbsurdFiniteSizesPositionsAndOutOfRangeColors()
        {
            var canvas = MakeCanvas();
            var name = Name("KwUiImage");

            foreach (var bad in new[] { new[] { "size", "3e38,3e38" }, new[] { "position", "1e7,0" }, new[] { "pivot", "0.5,-1e9" } })
            {
                var answer = Refused("create_image", "name", name, "parent_name", canvas.name, bad[0], bad[1]);
                Assert.AreEqual("INVALID_PARAM", (string)answer["code"], answer.ToString());
                Assert.AreEqual(bad[0], (string)answer["data"]["param"], answer.ToString());
            }

            var color = Refused("create_image", "name", name, "parent_name", canvas.name, "color", "255,128,0");
            Assert.AreEqual("INVALID_PARAM", (string)color["code"], color.ToString());
            Assert.IsNull(GameObject.Find(name), "A refused call must not leave an object.");

            Ok("create_image", "name", name, "parent_name", canvas.name, "color", "1,0.5,0", "size", "1000000,5");
        }

        [Test]
        public void IsUnitColor_RejectsAnyComponentOutsideZeroToOne()
        {
            Assert.IsTrue(UIFunctions.IsUnitColor(new Color(1f, 0.5f, 0f, 1f)));
            Assert.IsFalse(UIFunctions.IsUnitColor(new Color(255f, 128f, 0f, 1f)));
            Assert.IsFalse(UIFunctions.IsUnitColor(new Color(-0.1f, 0f, 0f, 1f)));
            Assert.IsFalse(UIFunctions.IsUnitColor(new Color(0f, 0f, 0f, 1.5f)));
            Assert.IsFalse(UIFunctions.IsUnitColor(new Color(float.NaN, 0f, 0f, 1f)));
        }

        [Test]
        public void UnitColorDetail_NamesZeroToOneAndMentions255OnlyForAnRgbThatLooksLikeIt()
        {
            StringAssert.Contains("within 0-1", UIFunctions.UnitColorDetail(new Color(0f, 0f, 0f, 1.5f)));
            StringAssert.DoesNotContain("0-255", UIFunctions.UnitColorDetail(new Color(0f, 0f, 0f, 1.5f)));
            StringAssert.DoesNotContain("0-255", UIFunctions.UnitColorDetail(new Color(-0.1f, 0f, 0f, 1f)));
            StringAssert.DoesNotContain("0-255", UIFunctions.UnitColorDetail(new Color(300f, 0f, 0f, 1f)));
            StringAssert.Contains("0-255", UIFunctions.UnitColorDetail(new Color(255f, 128f, 0f, 1f)));
        }

        [Test]
        public void ParentNotFoundHint_OffersCreateCanvasOnlyWhenThatToolIsExposed()
        {
            StringAssert.Contains("Create a Canvas first", UIFunctions.ParentNotFoundHint(true));
            StringAssert.DoesNotContain("Create a Canvas", UIFunctions.ParentNotFoundHint(false));
            StringAssert.Contains("parent_name", UIFunctions.ParentNotFoundHint(false));
        }
    }
}
