// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("UI")]
    internal static class UIFunctions
    {
        [Description("Create a Canvas in the scene (required for UI elements)")]
        public static string CreateCanvas(
            [ToolParam("Name for the Canvas", Required = false)] string name = "Canvas",
            [ToolParam("Render mode: ScreenSpaceOverlay, ScreenSpaceCamera, WorldSpace", Required = false)] string render_mode = "ScreenSpaceOverlay")
        {
            var canvasGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(canvasGo, $"Create Canvas {name}");

            var canvas = canvasGo.AddComponent<Canvas>();
            switch (render_mode.ToLowerInvariant())
            {
                case "screenspacecamera": canvas.renderMode = RenderMode.ScreenSpaceCamera; break;
                case "worldspace": canvas.renderMode = RenderMode.WorldSpace; break;
                default: canvas.renderMode = RenderMode.ScreenSpaceOverlay; break;
            }

            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            // Ensure EventSystem exists
            if (UnityEngine.Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                var esGo = new GameObject("EventSystem");
                esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                esGo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                Undo.RegisterCreatedObjectUndo(esGo, "Create EventSystem");
            }

            Selection.activeGameObject = canvasGo;
            return $"Created Canvas '{name}' with {render_mode} render mode";
        }

        [Description("Create a UI Button")]
        public static string CreateButton(
            [ToolParam("Name for the button")] string name,
            [ToolParam("Button label text")] string text,
            [ToolParam("Parent Canvas/UI element name, hierarchy path, or instance ID (finds inactive too)", Required = false)] string parent_name = "Canvas",
            [ToolParam("Anchored position as 'x,y'", Required = false)] string position = "0,0",
            [ToolParam("Size as 'width,height'", Required = false)] string size = "160,40",
            [ToolParam("Anchor preset: 'top-left','top-center','top-right','middle-left','center','middle-right','bottom-left','bottom-center','bottom-right','stretch-horizontal','stretch-vertical','stretch-full'", Required = false)] string anchor = null,
            [ToolParam("Pivot as 'x,y' (0-1)", Required = false)] string pivot = null)
        {
            var parent = FindParent(parent_name);
            if (parent == null)
                return ToolResultFormatter.Error("PARENT_NOT_FOUND", new { parent_name, hint = "Create a Canvas first." });

            var buttonGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(buttonGo, $"Create Button {name}");
            buttonGo.transform.SetParent(parent, false);

            var rect = buttonGo.AddComponent<RectTransform>();
            rect.sizeDelta = ValueConverter.ParseVector2(size, Vector2.zero);
            rect.anchoredPosition = ValueConverter.ParseVector2(position, Vector2.zero);
            ApplyAnchorPreset(rect, anchor, pivot);

            var image = buttonGo.AddComponent<Image>();
            image.color = new Color(0.2f, 0.5f, 0.9f, 1f);
            buttonGo.AddComponent<Button>();

            // Text child
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(buttonGo.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;

            var textComp = textGo.AddComponent<Text>();
            textComp.text = text;
            textComp.alignment = TextAnchor.MiddleCenter;
            textComp.color = Color.white;
            textComp.fontSize = 16;
            textComp.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            Selection.activeGameObject = buttonGo;
            return $"Created UI Button '{name}' with text '{text}'";
        }

        [Description("Create a UI Text element")]
        public static string CreateText(
            [ToolParam("Name for the text element")] string name,
            [ToolParam("Text content")] string text,
            [ToolParam("Parent Canvas/UI element name, hierarchy path, or instance ID (finds inactive too)", Required = false)] string parent_name = "Canvas",
            [ToolParam("Font size", Required = false)] string font_size = "20",
            [ToolParam("Anchored position as 'x,y'", Required = false)] string position = "0,0",
            [ToolParam("Size as 'width,height'", Required = false)] string size = "300,60",
            [ToolParam("Anchor preset: 'top-left','top-center','top-right','middle-left','center','middle-right','bottom-left','bottom-center','bottom-right','stretch-horizontal','stretch-vertical','stretch-full'", Required = false)] string anchor = null,
            [ToolParam("Pivot as 'x,y' (0-1)", Required = false)] string pivot = null)
        {
            var parent = FindParent(parent_name);
            if (parent == null)
                return ToolResultFormatter.Error("PARENT_NOT_FOUND", new { parent_name, hint = "Create a Canvas first." });

            var textGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(textGo, $"Create Text {name}");
            textGo.transform.SetParent(parent, false);

            var rect = textGo.AddComponent<RectTransform>();
            rect.sizeDelta = ValueConverter.ParseVector2(size, Vector2.zero);
            rect.anchoredPosition = ValueConverter.ParseVector2(position, Vector2.zero);
            ApplyAnchorPreset(rect, anchor, pivot);

            var textComp = textGo.AddComponent<Text>();
            textComp.text = text;
            textComp.alignment = TextAnchor.MiddleCenter;
            textComp.color = Color.white;
            textComp.fontSize = int.Parse(font_size);
            textComp.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            Selection.activeGameObject = textGo;
            return $"Created UI Text '{name}'";
        }

        [Description("Create a UI Image element")]
        public static string CreateImage(
            [ToolParam("Name for the image element")] string name,
            [ToolParam("Parent Canvas/UI element name, hierarchy path, or instance ID (finds inactive too)", Required = false)] string parent_name = "Canvas",
            [ToolParam("Color as 'r,g,b,a' or hex", Required = false)] string color = "1,1,1,1",
            [ToolParam("Size as 'width,height'", Required = false)] string size = "100,100",
            [ToolParam("Anchored position as 'x,y'", Required = false)] string position = "0,0",
            [ToolParam("Anchor preset: 'top-left','top-center','top-right','middle-left','center','middle-right','bottom-left','bottom-center','bottom-right','stretch-horizontal','stretch-vertical','stretch-full'", Required = false)] string anchor = null,
            [ToolParam("Pivot as 'x,y' (0-1)", Required = false)] string pivot = null)
        {
            var parent = FindParent(parent_name);
            if (parent == null)
                return ToolResultFormatter.Error("PARENT_NOT_FOUND", new { parent_name, hint = "Create a Canvas first." });

            var imageGo = CreateImageObject(parent, name, ValueConverter.ParseVector2(size, Vector2.zero), ValueConverter.ParseVector2(position, Vector2.zero), anchor, pivot);
            var image = imageGo.GetComponent<Image>();
            image.color = ValueConverter.ParseColor(color, Color.white);

            Selection.activeGameObject = imageGo;
            return $"Created UI Image '{name}'";
        }

        // Shared sprite-placement helper for CreateImage.
        internal static GameObject CreateImageObject(Transform parent, string name, Vector2 size, Vector2 position, string anchor, string pivot)
        {
            var imageGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(imageGo, $"Create Image {name}");
            imageGo.transform.SetParent(parent, false);

            var rect = imageGo.AddComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            ApplyAnchorPreset(rect, anchor, pivot);

            imageGo.AddComponent<Image>();
            return imageGo;
        }

        [Description("Set RectTransform layout on a UI element: anchor preset, anchored position, size, and pivot. " +
                     "anchor presets: top-left/top-center/top-right/middle-left/center/middle-right/bottom-left/bottom-center/bottom-right/stretch-horizontal/stretch-vertical/stretch-full. " +
                     "Any omitted value is kept.")]
        public static string SetRectTransform(
            [ToolParam("UI GameObject name, hierarchy path, or instance ID")] string target,
            [ToolParam("Anchor preset (see description)", Required = false)] string anchor = null,
            [ToolParam("Anchored position as 'x,y'", Required = false)] string anchored_position = null,
            [ToolParam("Size delta (width,height) as 'w,h'", Required = false)] string size = null,
            [ToolParam("Pivot as 'x,y' (0-1)", Required = false)] string pivot = null)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null)
                return ObjectsHelper.NotFoundText("target", target);

            var rect = go.GetComponent<RectTransform>();
            if (rect == null)
                return ToolResultFormatter.Error("NO_RECT_TRANSFORM", new { target, hint = "Target is not a UI element (no RectTransform)." });

            Undo.RecordObject(rect, $"Set RectTransform of {go.name}");

            if (!string.IsNullOrEmpty(anchor) || !string.IsNullOrEmpty(pivot))
                ApplyAnchorPreset(rect, anchor, pivot);
            if (!string.IsNullOrEmpty(anchored_position))
                rect.anchoredPosition = ValueConverter.ParseVector2(anchored_position, Vector2.zero);
            if (!string.IsNullOrEmpty(size))
                rect.sizeDelta = ValueConverter.ParseVector2(size, Vector2.zero);

            return $"RectTransform on '{go.name}': anchoredPos={rect.anchoredPosition}, sizeDelta={rect.sizeDelta}, pivot={rect.pivot}";
        }

        internal static void ApplyAnchorPreset(RectTransform rect, string anchor, string pivot)
        {
            if (!string.IsNullOrEmpty(pivot))
            {
                var p = ValueConverter.ParseVector2(pivot, Vector2.zero);
                rect.pivot = p;
            }

            if (string.IsNullOrEmpty(anchor)) return;

            switch (anchor.ToLowerInvariant().Replace(" ", "").Replace("_", "-"))
            {
                case "top-left":
                    rect.anchorMin = new Vector2(0, 1);
                    rect.anchorMax = new Vector2(0, 1);
                    rect.pivot = new Vector2(0, 1);
                    break;
                case "top-center":
                    rect.anchorMin = new Vector2(0.5f, 1);
                    rect.anchorMax = new Vector2(0.5f, 1);
                    rect.pivot = new Vector2(0.5f, 1);
                    break;
                case "top-right":
                    rect.anchorMin = new Vector2(1, 1);
                    rect.anchorMax = new Vector2(1, 1);
                    rect.pivot = new Vector2(1, 1);
                    break;
                case "middle-left":
                    rect.anchorMin = new Vector2(0, 0.5f);
                    rect.anchorMax = new Vector2(0, 0.5f);
                    rect.pivot = new Vector2(0, 0.5f);
                    break;
                case "center":
                    rect.anchorMin = new Vector2(0.5f, 0.5f);
                    rect.anchorMax = new Vector2(0.5f, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    break;
                case "middle-right":
                    rect.anchorMin = new Vector2(1, 0.5f);
                    rect.anchorMax = new Vector2(1, 0.5f);
                    rect.pivot = new Vector2(1, 0.5f);
                    break;
                case "bottom-left":
                    rect.anchorMin = new Vector2(0, 0);
                    rect.anchorMax = new Vector2(0, 0);
                    rect.pivot = new Vector2(0, 0);
                    break;
                case "bottom-center":
                    rect.anchorMin = new Vector2(0.5f, 0);
                    rect.anchorMax = new Vector2(0.5f, 0);
                    rect.pivot = new Vector2(0.5f, 0);
                    break;
                case "bottom-right":
                    rect.anchorMin = new Vector2(1, 0);
                    rect.anchorMax = new Vector2(1, 0);
                    rect.pivot = new Vector2(1, 0);
                    break;
                case "stretch-horizontal":
                    rect.anchorMin = new Vector2(0, 0.5f);
                    rect.anchorMax = new Vector2(1, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.offsetMin = new Vector2(0, rect.offsetMin.y);
                    rect.offsetMax = new Vector2(0, rect.offsetMax.y);
                    break;
                case "stretch-vertical":
                    rect.anchorMin = new Vector2(0.5f, 0);
                    rect.anchorMax = new Vector2(0.5f, 1);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.offsetMin = new Vector2(rect.offsetMin.x, 0);
                    rect.offsetMax = new Vector2(rect.offsetMax.x, 0);
                    break;
                case "stretch-full":
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.offsetMin = Vector2.zero;
                    rect.offsetMax = Vector2.zero;
                    break;
            }

            // If user also specified a custom pivot, override the preset's pivot
            if (!string.IsNullOrEmpty(pivot))
                rect.pivot = ValueConverter.ParseVector2(pivot, Vector2.zero);
        }

        private static Transform FindParent(string name)
        {
            var go = ObjectsHelper.FindTarget(name);
            return go?.transform;
        }

        [Description("Diagnose what UI (or physics) elements a click/tap at a screen point would hit, using the live " +
                     "EventSystem's RaycastAll. Returns the full ordered hit chain -- for each hit: hierarchy path, the " +
                     "raycast-receiving Graphic and its raycastTarget flag, sorting info, and the IPointerClickHandler " +
                     "that would actually receive the click (resolved by bubbling up the hierarchy, exactly like uGUI does). " +
                     "The summary names the click receiver, or the element silently swallowing the click when the topmost " +
                     "hit has no click handler anywhere up its parent chain -- the classic 'invisible raycast blocker' bug. " +
                     "Requires a live EventSystem (Play Mode).")]
        [ReadOnlyTool]
        public static string RaycastAtPoint(
            [ToolParam("X coordinate of the point to test.")] float x,
            [ToolParam("Y coordinate of the point to test.")] float y,
            [ToolParam("Interpret x/y as normalized 0-1 viewport coordinates instead of pixels.", Required = false)] bool normalized = false,
            [ToolParam("Coordinate origin: 'bottom_left' (Unity screen space, default) or 'top_left' (screenshot/image space).", Required = false)] string origin = "bottom_left",
            [ToolParam("Maximum number of hits to include in the result. Default 20.", Required = false)] int max_results = 20)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                return ToolResultFormatter.Error("NO_EVENT_SYSTEM", new
                {
                    hint = "No live EventSystem found. Enter Play Mode (or ensure the scene has an enabled EventSystem) first."
                });
            }

            // Screen.width/height on the editor main thread report the focused editor
            // window, not the game render resolution the UI canvases are laid out in.
            // Resolve the real Game View target size (same reflection the screenshot
            // tools use) so normalized coordinates and the top_left flip match what
            // GraphicRaycaster actually raycasts against.
            var screenWidth = 0;
            var screenHeight = 0;
            if (!ScreenshotFunctions.TryResolveGameViewSize(ref screenWidth, ref screenHeight))
            {
                screenWidth = Screen.width;
                screenHeight = Screen.height;
            }

            var px = normalized ? x * screenWidth : x;
            var py = normalized ? y * screenHeight : y;
            if (string.Equals(origin?.Trim(), "top_left", StringComparison.OrdinalIgnoreCase))
                py = screenHeight - py;

            var pointerData = new PointerEventData(eventSystem) { position = new Vector2(px, py) };
            var results = new List<RaycastResult>();
            eventSystem.RaycastAll(pointerData, results);

            max_results = Mathf.Clamp(max_results, 1, 100);
            var hits = new List<object>(Mathf.Min(results.Count, max_results));
            foreach (var result in results.Take(max_results))
            {
                var go = result.gameObject;
                if (go == null)
                    continue;

                var graphic = go.GetComponent<Graphic>();
                var clickHandlerGo = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
                hits.Add(new
                {
                    path = ObjectsHelper.GetGameObjectPath(go),
                    raycast_component = graphic != null ? graphic.GetType().Name : DescribeNonGraphicRaycastReceiver(go),
                    raycast_target = graphic != null ? (bool?)graphic.raycastTarget : null,
                    raycaster = result.module != null ? result.module.GetType().Name : null,
                    sorting_layer = result.sortingLayer,
                    sorting_order = result.sortingOrder,
                    depth = result.depth,
                    distance = result.distance,
                    click_handler = DescribeClickHandler(clickHandlerGo)
                });
            }

            object clickReceiver = null;
            object swallowedBy = null;
            if (results.Count > 0 && results[0].gameObject != null)
            {
                var topHandlerGo = ExecuteEvents.GetEventHandler<IPointerClickHandler>(results[0].gameObject);
                if (topHandlerGo != null)
                    clickReceiver = DescribeClickHandler(topHandlerGo);
                else
                    swallowedBy = new
                    {
                        path = ObjectsHelper.GetGameObjectPath(results[0].gameObject),
                        hint = "The topmost hit has no IPointerClickHandler anywhere up its parent chain, so a click " +
                               "here is consumed without reaching anything -- check for a stray raycastTarget Graphic " +
                               "or a leftover full-screen shield."
                    };
            }

            return JsonConvert.SerializeObject(Response.Success(
                results.Count == 0
                    ? "No raycast hits at this point."
                    : $"Raycast hit {results.Count} element(s); showing {hits.Count}.",
                new
                {
                    screen_position = new { x = px, y = py },
                    screen_size = new { width = screenWidth, height = screenHeight },
                    hit_count = results.Count,
                    click_receiver = clickReceiver,
                    swallowed_by = swallowedBy,
                    hits
                }));
        }

        private static string DescribeNonGraphicRaycastReceiver(GameObject go)
        {
#if KITWRIGHT_PHYSICS
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                return collider.GetType().Name;
#endif
#if KITWRIGHT_PHYSICS2D
            var collider2D = go.GetComponent<Collider2D>();
            if (collider2D != null)
                return collider2D.GetType().Name;
#endif
            return "<none>";
        }

        private static object DescribeClickHandler(GameObject handlerGo)
        {
            if (handlerGo == null)
                return null;

            var handlerComponents = handlerGo.GetComponents<Component>()
                .Where(c => c is IPointerClickHandler)
                .Select(c => c.GetType().Name)
                .ToArray();

            var selectable = handlerGo.GetComponent<Selectable>();
            return new
            {
                path = ObjectsHelper.GetGameObjectPath(handlerGo),
                components = handlerComponents,
                interactable = selectable != null ? (bool?)selectable.IsInteractable() : null
            };
        }
    }
}
