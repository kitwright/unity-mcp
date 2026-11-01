// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
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
        private const float MaxOrthographicSize = 100000f;

        [Description("Get a camera's type, FOV or size, clipping planes, background, clear flags, depth, culling mask, viewport rect, rendering path, HDR/MSAA, target display and texture, enabled state and transform. " +
                     "Does not report URP camera data (render type, camera stack, volume mask).")]
        [ReadOnlyTool]
        public static string GetCameraProperties(
            [ToolParam("Camera GameObject name, hierarchy path, or instance ID (default: Main Camera). Finds inactive objects too.", Required = false)] string game_object_name = null)
        {
            if (!TryFindCamera(game_object_name, out var camera, out var cameraError))
                return cameraError;

            var sb = new StringBuilder();
            sb.AppendLine($"Camera: {camera.gameObject.name}");
            sb.AppendLine($"  enabled: {camera.enabled}, activeInHierarchy: {camera.gameObject.activeInHierarchy}");
            sb.AppendLine($"  orthographic: {camera.orthographic}");
            if (camera.orthographic)
                sb.AppendLine($"  orthographicSize: {camera.orthographicSize}");
            else
            {
                sb.AppendLine($"  fieldOfView: {camera.fieldOfView}");
                sb.AppendLine($"  usePhysicalProperties: {camera.usePhysicalProperties}");
            }
            sb.AppendLine($"  nearClipPlane: {camera.nearClipPlane}");
            sb.AppendLine($"  farClipPlane: {camera.farClipPlane}");
            sb.AppendLine($"  backgroundColor: {camera.backgroundColor}");
            sb.AppendLine($"  clearFlags: {camera.clearFlags}");
            sb.AppendLine($"  depth: {camera.depth}");
            sb.AppendLine($"  cullingMask: {CullingMaskToString(camera.cullingMask)} (value {camera.cullingMask})");
            sb.AppendLine($"  rect: {camera.rect}");
            sb.AppendLine($"  renderingPath: {camera.renderingPath}");
            sb.AppendLine($"  allowHDR: {camera.allowHDR}, allowMSAA: {camera.allowMSAA}");
            sb.AppendLine($"  targetTexture: {(camera.targetTexture != null ? camera.targetTexture.name : "none")}");
            sb.AppendLine($"  targetDisplay: {camera.targetDisplay}");
            sb.AppendLine($"  position: {camera.transform.position}");
            sb.AppendLine($"  rotation: {camera.transform.eulerAngles}");
            return sb.ToString();
        }

        [Description("Set camera projection to orthographic or perspective with size/FOV")]
        public static string SetCameraProjection(
            [ToolParam("Projection type: 'orthographic' or 'perspective'")] string projection,
            [ToolParam("Orthographic size (> 0), or FOV in degrees for perspective (clamped to 1-179)", Required = false)] float? size = null,
            [ToolParam("Camera GameObject name, hierarchy path, or instance ID. Finds inactive objects too.", Required = false)] string game_object_name = null)
        {
            if (!TryFindCamera(game_object_name, out var camera, out var cameraError))
                return cameraError;

            // Anything that was not "ortho..." used to mean perspective, so a typo or a third value
            // silently switched the camera to the projection the caller did not ask for.
            bool ortho;
            switch ((projection ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "orthographic": case "ortho": ortho = true; break;
                case "perspective": case "persp": ortho = false; break;
                default:
                    return ToolResultFormatter.Error("INVALID_PARAM",
                        new { param = "projection", provided = projection },
                        "Expected 'orthographic' or 'perspective'.");
            }

            if (size.HasValue && (size.Value <= 0f || (ortho && size.Value > MaxOrthographicSize)))
                return ToolResultFormatter.Error("INVALID_PARAM",
                    new { param = "size", provided = size.Value },
                    ortho ? $"Orthographic size must be > 0 and at most {MaxOrthographicSize}." : "FOV must be > 0 (it is clamped to 1-179).");

            if (!ortho && camera.nearClipPlane <= 0f)
                return ToolResultFormatter.Error("INVALID_PARAM",
                    new { param = "near", provided = camera.nearClipPlane, expected = "> 0 for a perspective camera" },
                    "A perspective camera needs a near clip plane > 0 and this camera's is not. Set near > 0 with set_camera_settings first, then switch the projection.");

            if (camera.orthographic != ortho || size.HasValue)
            {
                Undo.RecordObject(camera, "Set Camera Projection");
                camera.orthographic = ortho;
                if (size.HasValue)
                {
                    if (ortho)
                        camera.orthographicSize = size.Value;
                    else
                        camera.fieldOfView = Mathf.Clamp(size.Value, 1f, 179f);
                }
            }

            string sizeInfo = ortho ? $"orthographicSize={camera.orthographicSize}" : $"fov={camera.fieldOfView}";
            return $"Camera '{camera.gameObject.name}' set to {(ortho ? "orthographic" : "perspective")}, {sizeInfo}";
        }

        [Description("Set camera clipping planes, background color, clear flags and depth. Values are validated before anything is applied; the reply reads them back from the camera. " +
                     "background_color takes 0-1 floats ('r,g,b[,a]'), #hex or a color name (create_texture uses 0-255 for the same spelling).")]
        public static string SetCameraSettings(
            [ToolParam("Camera GameObject name, hierarchy path, or instance ID. Finds inactive objects too.", Required = false)] string game_object_name = null,
            [ToolParam("Near clip plane distance (> 0 for a perspective camera, must stay below far)", Required = false)] float? near = null,
            [ToolParam("Far clip plane distance (must stay above near)", Required = false)] float? far = null,
            [ToolParam("Background color: 0-1 floats 'r,g,b[,a]', #hex or a color name", Required = false)] string background_color = null,
            [ToolParam("Clear flags: 'Skybox','SolidColor','Depth','Nothing'", Required = false)] string clear_flags = null,
            [ToolParam("Camera depth (render order)", Required = false)] float? depth = null)
        {
            if (!TryFindCamera(game_object_name, out var camera, out var cameraError))
                return cameraError;

            var hasColor = !string.IsNullOrEmpty(background_color);
            var backgroundColor = Color.black;
            string colorError = null;
            if (hasColor && (string.IsNullOrWhiteSpace(background_color) ||
                             !ValueConverter.TryParseColor(background_color, Color.black, out backgroundColor, out colorError)))
                return ToolResultFormatter.Error("INVALID_PARAM",
                    new { param = "background_color", provided = background_color, detail = colorError ?? "the color is blank" });

            CameraClearFlags? flags = null;
            if (!string.IsNullOrEmpty(clear_flags))
            {
                flags = ParseClearFlags(clear_flags);
                if (flags == null)
                    return ToolResultFormatter.Error("INVALID_PARAM",
                        new { param = "clear_flags", provided = clear_flags, expected = "Skybox, SolidColor, Depth or Nothing" });
            }

            if (near.HasValue && !camera.orthographic && near.Value <= 0f)
                return ToolResultFormatter.Error("INVALID_PARAM",
                    new { param = "near", provided = near.Value, expected = "> 0 for a perspective camera" });
            if ((near.HasValue || far.HasValue) && (far ?? camera.farClipPlane) <= (near ?? camera.nearClipPlane))
                return ToolResultFormatter.Error("INVALID_PARAM",
                    new { param = far.HasValue ? "far" : "near", near = near ?? camera.nearClipPlane, far = far ?? camera.farClipPlane },
                    "The far clip plane must be greater than the near clip plane.");

            if (!near.HasValue && !far.HasValue && !hasColor && flags == null && !depth.HasValue)
                return ToolResultFormatter.Error("NO_PARAMS",
                    new { hint = "Provide at least one of near, far, background_color, clear_flags, depth." });

            Undo.RecordObject(camera, "Set Camera Settings");
            var changes = new StringBuilder();

            if (near.HasValue)
                camera.nearClipPlane = near.Value;
            if (far.HasValue)
                camera.farClipPlane = far.Value;
            if (near.HasValue || far.HasValue)
                changes.Append($"near={camera.nearClipPlane} far={camera.farClipPlane} ");
            if (hasColor)
            {
                camera.backgroundColor = backgroundColor;
                changes.Append($"bg={camera.backgroundColor} ");
            }
            if (flags.HasValue)
            {
                camera.clearFlags = flags.Value;
                changes.Append($"clearFlags={camera.clearFlags} ");
            }
            if (depth.HasValue)
            {
                camera.depth = depth.Value;
                changes.Append($"depth={camera.depth} ");
            }

            return $"Camera '{camera.gameObject.name}' updated: {changes}";
        }

        [Description("Set the camera culling mask to show/hide specific layers. Every name must be an existing layer (or Everything / Nothing); nothing is applied otherwise.")]
        public static string SetCameraCullingMask(
            [ToolParam("Comma-separated layer names to show (e.g. 'Default,UI,Water'), or 'Everything' / 'Nothing'")] string layers,
            [ToolParam("Camera GameObject name, hierarchy path, or instance ID. Finds inactive objects too.", Required = false)] string game_object_name = null,
            [ToolParam("Mode: 'set' replaces mask, 'add' adds layers, 'remove' removes layers", Required = false)] string mode = "set")
        {
            if (!TryFindCamera(game_object_name, out var camera, out var cameraError))
                return cameraError;

            var operation = (mode ?? "set").Trim().ToLowerInvariant();
            if (operation != "set" && operation != "add" && operation != "remove")
                return ToolResultFormatter.Error("INVALID_PARAM",
                    new { param = "mode", provided = mode },
                    "Expected 'set', 'add' or 'remove'.");

            if (!TryParseLayerNames(layers, out var mask, out var invalid))
                return ToolResultFormatter.Error("INVALID_PARAM",
                    new { param = "layers", provided = layers, invalid, valid = NamedLayers() },
                    "Pass comma-separated names of existing layers (see get_layers), or Everything / Nothing. Nothing was changed.");

            Undo.RecordObject(camera, "Set Camera Culling Mask");
            switch (operation)
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

        // An unknown or empty name used to be skipped (and "" resolved to the first blank built-in
        // slot), so a typo became a different mask under a success reply.
        internal static bool TryParseLayerNames(string layers, out int mask, out string[] invalid)
        {
            mask = 0;
            var bad = new List<string>();
            foreach (var raw in (layers ?? string.Empty).Split(','))
            {
                var token = raw.Trim();
                var layer = token.Length > 0 ? LayerMask.NameToLayer(token) : -1;
                if (layer >= 0)
                    mask |= 1 << layer;
                else if (token.Equals("Everything", StringComparison.OrdinalIgnoreCase))
                    mask = -1;
                else if (!token.Equals("Nothing", StringComparison.OrdinalIgnoreCase))
                    bad.Add(token.Length > 0 ? token : "<empty>");
            }
            invalid = bad.ToArray();
            return invalid.Length == 0;
        }

        internal static CameraClearFlags? ParseClearFlags(string text)
        {
            var normalized = text.Trim().ToLowerInvariant();
            if (normalized == "solid") return CameraClearFlags.SolidColor;
            if (normalized == "none") return CameraClearFlags.Nothing;
            return ValueConverter.TryParseEnum<CameraClearFlags>(normalized, out var flags) ? flags : (CameraClearFlags?)null;
        }

        private static string[] NamedLayers() =>
            Enumerable.Range(0, 32).Select(i => LayerMask.LayerToName(i)).Where(n => !string.IsNullOrEmpty(n)).ToArray();

        // No name: the Main Camera, else the only camera in the scene. Falling back to "any camera"
        // edited an arbitrary one (an overlay or UI camera) when the Main Camera was inactive.
        internal static Camera PickDefaultCamera(Camera main, Camera[] all) =>
            main != null ? main : all.Length == 1 ? all[0] : null;

        private static bool TryFindCamera(string name, out Camera camera, out string error)
        {
            camera = null;
            error = null;

            if (string.IsNullOrEmpty(name))
            {
                var all = ObjectsHelper.FindObjectsByTypeUnsorted<Camera>(FindObjectsInactive.Include);
                camera = PickDefaultCamera(Camera.main, all);
                if (camera != null)
                    return true;

                error = all.Length == 0
                    ? ToolResultFormatter.Error("CAMERA_NOT_FOUND", new { game_object_name = name })
                    : ToolResultFormatter.Error("AMBIGUOUS_TARGET",
                        new
                        {
                            target = "(default camera)",
                            match_count = all.Length,
                            candidates = all.Select(c => new
                            {
                                instanceId = ObjectIdCodec.GetSerializableId(c.gameObject),
                                path = ObjectsHelper.GetGameObjectPath(c.gameObject)
                            }).ToArray()
                        },
                        "No enabled camera is tagged MainCamera. Pass one of the candidates as game_object_name.");
                return false;
            }

            var go = ObjectsHelper.FindTarget(name);
            if (go == null)
            {
                error = ToolResultFormatter.Error("CAMERA_NOT_FOUND", new { game_object_name = name });
                return false;
            }

            camera = go.GetComponent<Camera>();
            if (camera == null)
            {
                error = ToolResultFormatter.Error("NO_CAMERA_ON_TARGET",
                    new { game_object_name = name, resolved = go.name },
                    $"'{go.name}' exists but has no Camera component.");
                return false;
            }

            return true;
        }

        internal static string CullingMaskToString(int mask)
        {
            if (mask == -1) return "Everything";
            if (mask == 0) return "Nothing";

            var sb = new StringBuilder();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    var layerName = LayerMask.LayerToName(i);
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(string.IsNullOrEmpty(layerName) ? $"layer {i}" : layerName);
                }
            }
            return sb.ToString();
        }

    }
}
