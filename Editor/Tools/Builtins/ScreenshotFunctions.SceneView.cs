// Copyright (C) KitWright. All rights reserved.
using System;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    internal static partial class ScreenshotFunctions
    {
        [Description("Capture a screenshot of the Scene View (the editor's scene camera perspective). Returns a base64-encoded PNG image, " +
                     "or a saved file path when save_to_file=true.")]
        [ReadOnlyTool]
        public static string CaptureSceneView(
            [ToolParam("Width of the screenshot in pixels", Required = false)] int width = 0,
            [ToolParam("Height of the screenshot in pixels", Required = false)] int height = 0,
            [ToolParam(SaveToFileParamDescription, Required = false)] bool save_to_file = false,
            [ToolParam(OutputPathParamDescription, Required = false)] string output_path = null)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return ToolResultFormatter.Error("SCENE_VIEW_NOT_OPEN", new { hint = "Open a Scene View window first." });

            var camera = sceneView.camera;
            if (camera == null)
                return ToolResultFormatter.Error("SCENE_VIEW_CAMERA_UNAVAILABLE");

            if (width <= 0 || height <= 0)
            {
                width = Mathf.RoundToInt(camera.pixelWidth);
                height = Mathf.RoundToInt(camera.pixelHeight);

                var cap = ResolveDefaultScreenshotSize();
                var longest = Mathf.Max(width, height);
                if (cap > 0 && longest > cap)
                {
                    var scale = cap / (float)longest;
                    width = Mathf.RoundToInt(width * scale);
                    height = Mathf.RoundToInt(height * scale);
                }
            }

            width = Mathf.Clamp(width, 64, 4096);
            height = Mathf.Clamp(height, 64, 4096);

            try
            {
                return FinishCapture(CaptureFromCameraPngBytes(camera, width, height), save_to_file, output_path, "scene-view");
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SCREENSHOT_CAPTURE_FAILED", new { message = ex.Message });
            }
        }
    }
}
