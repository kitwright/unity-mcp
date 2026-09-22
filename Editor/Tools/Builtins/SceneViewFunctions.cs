// Copyright (C) KitWright. All rights reserved.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("SceneView")]
    internal static class SceneViewFunctions
    {
        [Description("Frame the Scene View camera on a GameObject (like pressing F with it selected), so it fills the view. Call before capture_scene_view to look at a specific object.")]
        public static object FrameObject(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target)
        {
            if (!TryGetSceneView(out var view, out var noView)) return noView;

            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            var prev = Selection.activeGameObject;
            Selection.activeGameObject = go;
            view.FrameSelected();
            Selection.activeGameObject = prev;
            view.Repaint();

            return Response.Success($"Framed Scene View on '{go.name}'.", DescribeView(view));
        }

        [Description("Align the Scene View camera to match a camera or GameObject's position and orientation (like GameObject > Align View to Selected).")]
        public static object AlignViewToObject(
            [ToolParam("GameObject name, hierarchy path, or instance ID (a camera works best)")] string target)
        {
            if (!TryGetSceneView(out var view, out var noView)) return noView;

            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            var t = go.transform;
            view.AlignViewToObject(t);
            view.Repaint();

            return Response.Success($"Aligned Scene View to '{go.name}'.", DescribeView(view));
        }

        [Description("Point the Scene View camera at a world position without changing distance much. Rotates the view to look toward the given point.")]
        public static object LookAtPoint(
            [ToolParam("World position to look at as 'x,y,z'")] string point,
            [ToolParam("Optional camera distance from the point", Required = false)] float distance = -1f)
        {
            if (!TryGetSceneView(out var view, out var noView)) return noView;
            if (!ValueConverter.TryParseVector3(point, out var p, out _)) return Response.Error("INVALID_VECTOR", new { point });

            float size = distance > 0f ? distance : view.size;
            view.LookAt(p, view.rotation, size);
            view.Repaint();

            return Response.Success($"Scene View looking at {p}.", DescribeView(view));
        }

        [Description("Manually set the Scene View camera pivot, rotation (euler degrees), and orbit size (distance). Any omitted value is kept. Use for precise framing.")]
        public static object SetSceneViewCamera(
            [ToolParam("Pivot (look-at center) as 'x,y,z'", Required = false)] string pivot = null,
            [ToolParam("Rotation euler degrees as 'x,y,z'", Required = false)] string rotation = null,
            [ToolParam("Orbit size / distance from pivot", Required = false)] float size = -1f)
        {
            if (!TryGetSceneView(out var view, out var noView)) return noView;

            if (!string.IsNullOrEmpty(pivot) && ValueConverter.TryParseVector3(pivot, out var p, out _))
                view.pivot = p;
            if (!string.IsNullOrEmpty(rotation) && ValueConverter.TryParseVector3(rotation, out var r, out _))
                view.rotation = Quaternion.Euler(r);
            if (size > 0f)
                view.size = size;

            view.Repaint();
            return Response.Success("Scene View camera updated.", DescribeView(view));
        }

        [Description("Get the current Scene View camera state: pivot, rotation, size, orthographic, camera world position.")]
        [ReadOnlyTool]
        public static object GetSceneViewCamera()
        {
            if (!TryGetSceneView(out var view, out var noView)) return noView;
            return Response.Success("Scene View camera state.", DescribeView(view));
        }

        private static bool TryGetSceneView(out SceneView view, out object error)
        {
            error = null;
            view = SceneView.lastActiveSceneView;
            if (view == null && SceneView.sceneViews != null && SceneView.sceneViews.Count > 0)
                view = SceneView.sceneViews[0] as SceneView;
            if (view == null)
            {
                error = Response.Error("NO_SCENE_VIEW", new { hint = "No open Scene View window. Open one via Window > General > Scene." });
                return false;
            }
            return true;
        }

        private static object DescribeView(SceneView view)
        {
            var cam = view.camera;
            return new
            {
                pivot = new { x = view.pivot.x, y = view.pivot.y, z = view.pivot.z },
                rotation = new { x = view.rotation.eulerAngles.x, y = view.rotation.eulerAngles.y, z = view.rotation.eulerAngles.z },
                size = view.size,
                orthographic = view.orthographic,
                cameraPosition = cam != null
                    ? new { x = cam.transform.position.x, y = cam.transform.position.y, z = cam.transform.position.z }
                    : null
            };
        }
    }
}
