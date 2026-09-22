// Copyright (C) KitWright. All rights reserved.

using System.Collections.Generic;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace KitWright.Editor.Tests
{
    public sealed class UIControlFunctionsTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private GameObject _parent;
        private EventSystem _preexistingEventSystem;

        [SetUp]
        public void SetUp()
        {
            // The tool creates one on demand; without this the fixture would leave it behind in
            // whatever scene the suite happens to run in.
            _preexistingEventSystem = Object.FindAnyObjectByType<EventSystem>();

            _parent = new GameObject("__KwTestUiParent", typeof(RectTransform));
            _created.Add(_parent);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _created)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
            _created.Clear();

            var eventSystem = Object.FindAnyObjectByType<EventSystem>();
            if (eventSystem != null && eventSystem != _preexistingEventSystem)
                Object.DestroyImmediate(eventSystem.gameObject);
        }

        private GameObject Create(string kind, string name = null)
        {
            var message = UIFunctions.CreateUiElement(kind, name, _parent.name);
            StringAssert.DoesNotContain("\"error\"", message);

            var go = Selection.activeGameObject;
            Assert.IsNotNull(go, message);
            _created.Add(go);
            return go;
        }

        // The point of routing through DefaultControls instead of hand-building children: a Slider is
        // only a Slider once Background, Fill and Handle exist under it.
        [Test]
        public void CreateUiElement_SliderComesWithItsWholeChildTree()
        {
            var slider = Create("slider", "__KwTestSlider");

            Assert.IsNotNull(slider.GetComponent<Slider>());
            Assert.AreEqual(3, slider.transform.childCount);
            Assert.IsNotNull(slider.transform.Find("Background"));
            Assert.IsNotNull(slider.transform.Find("Fill Area/Fill"));
            Assert.IsNotNull(slider.transform.Find("Handle Slide Area/Handle"));
        }

        // Sprites come from Unity's builtin extra resources. If those paths ever stop resolving the
        // controls still build, just as untextured white boxes — which no other assertion would catch.
        [Test]
        public void CreateUiElement_BuiltinSkinSpritesResolve()
        {
            var handle = Create("slider").transform.Find("Handle Slide Area/Handle");

            Assert.IsNotNull(handle.GetComponent<Image>().sprite,
                "Builtin UI skin sprite did not resolve; the control would render untextured.");
        }

        [Test]
        public void CreateUiElement_DropdownTemplateIsBuiltAndLeftInactive()
        {
            var template = Create("dropdown").transform.Find("Template");

            Assert.IsNotNull(template);
            Assert.IsFalse(template.gameObject.activeSelf, "Unity ships the Dropdown template disabled.");
        }

        [Test]
        public void CreateUiElement_UnknownKindIsAnErrorNotAnEmptyObject()
        {
            var message = UIFunctions.CreateUiElement("carousel", "__KwTestBogus", _parent.name);

            StringAssert.Contains("UNKNOWN_UI_KIND", message);
            Assert.AreEqual(0, _parent.transform.childCount);
        }

        [Test]
        public void CreateUiElement_MissingParentIsReportedInsteadOfCreatingAStrayObject()
        {
            var message = UIFunctions.CreateUiElement("toggle", "__KwTestToggle", "__KwNoSuchParent");

            StringAssert.Contains("PARENT_NOT_FOUND", message);
        }
    }
}
