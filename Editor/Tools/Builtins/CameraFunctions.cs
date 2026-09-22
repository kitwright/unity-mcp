// Copyright (C) KitWright. All rights reserved.
using System.Text;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Camera")]
    internal static class CameraFunctions
    {
        [Description("Get all camera properties including type, FOV, clipping planes, background, and culling mask")]
        [ReadOnlyTool]
        public static string GetCameraProperties(
            [ToolParam("Camera GameObject name, hierarchy path, or instance ID (default: Main Camera). Finds inactive objects too.", Required = false)] string game_object_name = null)
        {
            var camera = FindCamera(game_object_name);
            if (camera == null)
                return ToolResultFormatter.Error("CAMERA_NOT_FOUND", new { game_object_name });

            var sb = new StringBuilder();
            sb.AppendLine($"Camera: {camera.gameObject.name}");
            sb.AppendLine($"  orthographic: {camera.orthographic}");
            if (camera.orthographic)
                sb.AppendLine($"  orthographicSize: {camera.orthographicSize}");
            else
                sb.AppendLine($"  fieldOfView: {camera.fieldOfView}");
            sb.AppendLine($"  nearClipPlane: {camera.nearClipPlane}");
            sb.AppendLine($"  farClipPlane: {camera.farClipPlane}");
            sb.AppendLine($"  backgroundColor: {camera.backgroundColor}");
            sb.AppendLine($"  clearFlags: {camera.clearFlags}");
            sb.AppendLine($"  depth: {camera.depth}");
            sb.AppendLine($"  cullingMask: {CullingMaskToString(camera.cullingMask)}");
            sb.AppendLine($"  rect: {camera.rect}");
            sb.AppendLine($"  renderingPath: {camera.renderingPath}");
            sb.AppendLine($"  targetDisplay: {camera.targetDisplay}");
            sb.AppendLine($"  position: {camera.transform.position}");
            sb.AppendLine($"  rotation: {camera.transform.eulerAngles}");
            return sb.ToString();
        }

        [Description("Set camera projection to orthographic or perspective with size/FOV")]
        public static string SetCameraProjection(
            [ToolParam("Projection type: 'orthographic' or 'perspective'")] string projection,
            [ToolParam("Orthographic size or FOV value", Required = false)] float size = -1f,
            [ToolParam("Camera GameObject name, hierarchy path, or instance ID. Finds inactive objects too.", Required = false)] string game_object_name = null)
        {
            var camera = FindCamera(game_object_name);
            if (camera == null)
                return ToolResultFormatter.Error("CAMERA_NOT_FOUND", new { game_object_name });

            // Anything that was not "ortho..." used to mean perspective, so a typo or a third value
            // silently switched the camera to the projection the caller did not ask for.
            var normalized = (projection ?? string.Empty).ToLowerInvariant();
            bool ortho = normalized.StartsWith("ortho");
            if (!ortho && !normalized.StartsWith("persp"))
                return ToolResultFormatter.Error("INVALID_PARAM",
                    new { param = "projection", provided = projection },
                    "Expected 'orthographic' or 'perspective'.");

            Undo.RecordObject(camera, "Set Camera Projection");
            camera.orthographic = ortho;

            if (size > 0f)
            {
                if (ortho)
                    camera.orthographicSize = size;
                else
                    camera.fieldOfView = Mathf.Clamp(size, 1f, 179f);
            }

            string sizeInfo = ortho ? $"orthographicSize={camera.orthographicSize}" : $"fov={camera.fieldOfView}";
            return $"Camera '{camera.gameObject.name}' set to {(ortho ? "orthographic" : "perspective")}, {sizeInfo}";
        }

        [Description("Set camera clipping planes, background color, and clear flags")]
        public static string SetCameraSettings(
            [ToolParam("Camera GameObject name, hierarchy path, or instance ID. Finds inactive objects too.", Required = false)] string game_object_name = null,
            [ToolParam("Near clip plane distance", Required = false)] float near = -1f,
            [ToolParam("Far clip plane distance", Required = false)] float far = -1f,
            [ToolParam("Background color as 'r,g,b,a' or hex", Required = false)] string background_color = null,
            [ToolParam("Clear flags: 'Skybox','SolidColor','Depth','Nothing'", Required = false)] string clear_flags = null,
            [ToolParam("Camera depth (render order)", Required = false)] float depth = float.MinValue)
        {
            var camera = FindCamera(game_object_name);
            if (camera == null)
                return ToolResultFormatter.Error("CAMERA_NOT_FOUND", new { game_object_name });

            Undo.RecordObject(camera, "Set Camera Settings");
            var changes = new StringBuilder();

            if (near > 0f)
            {
                camera.nearClipPlane = near;
                changes.Append($"near={near} ");
            }
            if (far > 0f)
            {
                camera.farClipPlane = far;
                changes.Append($"far={far} ");
            }
            if (!string.IsNullOrEmpty(background_color))
            {
                camera.backgroundColor = ValueConverter.ParseColor(background_color, Color.black);
                changes.Append($"bg={background_color} ");
            }
            if (!string.IsNullOrEmpty(clear_flags))
            {
                switch (clear_flags.ToLowerInvariant())
                {
                    case "skybox": camera.clearFlags = CameraClearFlags.Skybox; break;
                    case "solidcolor": case "solid": camera.clearFlags = CameraClearFlags.SolidColor; break;
                    case "depth": camera.clearFlags = CameraClearFlags.Depth; break;
                    case "nothing": case "none": camera.clearFlags = CameraClearFlags.Nothing; break;
                }
                changes.Append($"clearFlags={camera.clearFlags} ");
            }
            if (depth > float.MinValue)
            {
                camera.depth = depth;
                changes.Append($"depth={depth} ");
            }

            return changes.Length > 0
                ? $"Camera '{camera.gameObject.name}' updated: {changes}"
                : "No changes applied (no valid parameters provided)";
        }

        [Description("Set the camera culling mask to show/hide specific layers")]
        public static string SetCameraCullingMask(
            [ToolParam("Comma-separated layer names to show (e.g. 'Default,UI,Water')")] string layers,
            [ToolParam("Camera GameObject name, hierarchy path, or instance ID. Finds inactive objects too.", Required = false)] string game_object_name = null,
            [ToolParam("Mode: 'set' replaces mask, 'add' adds layers, 'remove' removes layers", Required = false)] string mode = "set")
        {
            var camera = FindCamera(game_object_name);
            if (camera == null)
                return ToolResultFormatter.Error("CAMERA_NOT_FOUND", new { game_object_name });

            Undo.RecordObject(camera, "Set Camera Culling Mask");

            var layerNames = layers.Split(',');
            int mask = 0;
            foreach (var layerName in layerNames)
            {
                int layer = LayerMask.NameToLayer(layerName.Trim());
                if (layer >= 0)
                    mask |= 1 << layer;
            }

            switch (mode.ToLowerInvariant())
            {
                case "add":
                    camera.cullingMask |= mask;
                    break;
                case "remove":
                    camera.cullingMask &= ~mask;
                    break;
                default:
                    camera.cullingMask = mask;
                    break;
            }

            return $"Camera '{camera.gameObject.name}' cullingMask updated: {CullingMaskToString(camera.cullingMask)}";
        }

        // --- Helpers ---

        private static Camera FindCamera(string name)
        {
            if (string.IsNullOrEmpty(name))
                return Camera.main ?? Object.FindAnyObjectByType<Camera>();

            var go = ObjectsHelper.FindTarget(name);
            if (go == null) return null;
            return go.GetComponent<Camera>();
        }

        private static string CullingMaskToString(int mask)
        {
            if (mask == -1) return "Everything";
            if (mask == 0) return "Nothing";

            var sb = new StringBuilder();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    var layerName = LayerMask.LayerToName(i);
                    if (!string.IsNullOrEmpty(layerName))
                    {
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(layerName);
                    }
                }
            }
            return sb.ToString();
        }

    }
}
