// Copyright (C) KitWright. Licensed under MIT.

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

            FrameKeepingSelection(view, go);
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

        [Description("Manually set the Scene View camera pivot, rotation (euler degrees), and orbit size (distance). Any omitted value is kept, " +
                     "so pivot alone centres the view on a world point at its current angle. Use for precise framing.")]
        public static object SetSceneViewCamera(
            [ToolParam("Pivot (look-at center) as 'x,y,z'", Required = false)] string pivot = null,
            [ToolParam("Rotation euler degrees as 'x,y,z'", Required = false)] string rotation = null,
            [ToolParam("Orbit size / distance from pivot", Required = false)] float size = -1f)
        {
            if (!TryGetSceneView(out var view, out var noView)) return noView;

            var p = default(Vector3);
            var r = default(Vector3);
            var hasPivot = !string.IsNullOrEmpty(pivot);
            var hasRotation = !string.IsNullOrEmpty(rotation);
            if (hasPivot && !ValueConverter.TryParseVector3(pivot, out p, out _))
                return Response.Error("INVALID_VECTOR", new { pivot });
            if (hasRotation && !ValueConverter.TryParseVector3(rotation, out r, out _))
                return Response.Error("INVALID_VECTOR", new { rotation });

            if (hasPivot)
                view.pivot = p;
            if (hasRotation)
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

        // FrameSelected frames what is selected, so the target has to be selected for the call. The whole
        // selection goes back afterwards: a multi-selection, or an asset picked in the Project window,
        // has no activeGameObject to restore.
        internal static void FrameKeepingSelection(SceneView view, GameObject go)
        {
            var previous = Selection.objects;
            try
            {
                Selection.activeGameObject = go;
                view.FrameSelected();
            }
            finally
            {
                Selection.objects = previous;
            }
        }

        internal const string NoSceneViewHint = "No open Scene View window. Open one via Window > General > Scene.";

        internal static bool TryGetSceneView(out SceneView view, out object error)
        {
            error = null;
            view = SceneView.lastActiveSceneView;
            if (view == null && SceneView.sceneViews != null && SceneView.sceneViews.Count > 0)
                view = SceneView.sceneViews[0] as SceneView;
            if (view == null)
            {
                error = Response.Error("NO_SCENE_VIEW", new { hint = NoSceneViewHint });
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
