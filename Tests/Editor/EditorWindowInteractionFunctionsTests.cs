// Copyright (C) KitWright. Licensed under MIT.

using System.Collections;
using System.Linq;
using System.Reflection;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace KitWright.Editor.Tests
{
    public sealed class EditorWindowInteractionFunctionsTests
    {
        private const string ProbeTitle = "KitWrightClickProbe";

        private sealed class ClickProbeWindow : EditorWindow
        {
            internal int Clicks;

            public void CreateGUI()
            {
                var button = new Button(() => Clicks++) { text = "probe" };
                button.style.position = Position.Absolute;
                button.style.left = 0;
                button.style.top = 0;
                button.style.width = 280;
                button.style.height = 40;
                rootVisualElement.Add(button);

                // Both bands are deep enough that the tool's pixelsPerPoint division cannot land a
                // click in the wrong one on a HiDPI editor.
                var imgui = new IMGUIContainer(() => GUILayout.Label("imgui"));
                imgui.style.position = Position.Absolute;
                imgui.style.left = 0;
                imgui.style.top = 60;
                imgui.style.width = 280;
                imgui.style.height = 140;
                rootVisualElement.Add(imgui);
            }
        }

        // The tool used to post a legacy Event into the window's GUIView, which never reaches the
        // window's UI Toolkit panel: every click reported success and did nothing.
        [UnityTest]
        public IEnumerator SimulateEditorWindowClick_ActivatesAUIToolkitButton_AndRefusesIMGUI()
        {
            var window = ScriptableObject.CreateInstance<ClickProbeWindow>();
            try
            {
                window.titleContent = new GUIContent(ProbeTitle);
                window.position = new Rect(120, 120, 300, 300);
                window.Show();
                window.Repaint();

                // The panel lays the probe out on an editor tick, and a click before that would be
                // picked against a root that is still zero-sized.
                yield return null;
                yield return null;

                if (window.rootVisualElement?.panel == null)
                    Assert.Ignore("This editor session has no UI Toolkit panel to dispatch into.");

                var clicked = EditorWindowInteractionFunctions.SimulateEditorWindowClick(ProbeTitle, 40, 20);
                Assert.AreEqual(1, window.Clicks, clicked);

                var refused = EditorWindowInteractionFunctions.SimulateEditorWindowClick(ProbeTitle, 40, 180);
                StringAssert.Contains("IMGUI_CLICK_UNSUPPORTED", refused);
                Assert.AreEqual(1, window.Clicks, "A refused click must not dispatch anything either.");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void SimulateEditorWindowClick_ExposesWindowAndPixelParameters()
        {
            var method = typeof(EditorWindowInteractionFunctions).GetMethod(
                "SimulateEditorWindowClick",
                BindingFlags.Public | BindingFlags.Static);

            Assert.IsNotNull(method);
            var names = method.GetParameters().Select(p => p.Name).ToArray();
            CollectionAssert.Contains(names, "window");
            CollectionAssert.Contains(names, "x");
            CollectionAssert.Contains(names, "y");
        }

        [Test]
        public void EditorWindowInteractionTools_AreNotReadOnly()
        {
            Assert.IsFalse(ToolRegistry.IsReadOnly(typeof(EditorWindowInteractionFunctions).GetMethod("SimulateEditorWindowClick")));
            Assert.IsFalse(ToolRegistry.IsReadOnly(typeof(EditorWindowInteractionFunctions).GetMethod("SimulateEditorWindowKey")));
        }
    }
}
