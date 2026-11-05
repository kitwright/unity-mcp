// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Lighting")]
    internal static class LightingFunctions
    {
        [Description("Read the active scene's environment lighting: ambient (mode/light/intensity), skybox material, " +
                     "fog (enabled/mode/color/density), the active LightingSettings asset (if any), whether a lightmap bake is running " +
                     "(is_running), and last_completed_utc: when the last lightmap bake finished in this editor session (null if none did).")]
        [ReadOnlyTool]
        public static object GetLightingSettings()
        {
            try
            {
                return Response.Success("Lighting settings.", BuildLightingData());
            }
            catch (Exception ex)
            {
                return Response.Error("LIGHTING_READ_FAILED", new { message = ex.Message });
            }
        }

        [Description("Set scene environment lighting. Only the parameters you provide are applied; the rest are left untouched. " +
                     "Marks the active scene dirty (save the scene to persist). In Play Mode the change is discarded when it ends. " +
                     "Returns a full read-back of the resulting settings.")]
        public static object SetLightingSettings(
            [ToolParam("Ambient source mode: Skybox, Trilight, Flat, or Custom.", Required = false)] string ambient_mode = null,
            [ToolParam("Ambient light color as 'r,g,b[,a]' (0-1 floats) or #hex.", Required = false)] string ambient_light = null,
            [ToolParam("Enable (true) or disable (false) fog. Omit to leave unchanged.", Required = false)] bool? fog = null,
            [ToolParam("Fog color as 'r,g,b[,a]' (0-1 floats) or #hex.", Required = false)] string fog_color = null,
            [ToolParam("Fog density for exponential fog modes (>= 0). -1 leaves it unchanged.", Required = false)] float fog_density = -1f)
        {
            var applied = new List<string>();
            UnityEngine.Rendering.AmbientMode? parsedAmbientMode = null;
            Color? parsedAmbientLight = null;
            Color? parsedFogColor = null;

            if (!string.IsNullOrEmpty(ambient_mode))
            {
                if (!ValueConverter.TryParseEnum<UnityEngine.Rendering.AmbientMode>(ambient_mode, out var mode))
                    return Response.Error("INVALID_PARAM",
                        new { param = "ambient_mode", provided = ambient_mode, expected = "Skybox|Trilight|Flat|Custom" });
                parsedAmbientMode = mode;
            }

            if (!string.IsNullOrEmpty(ambient_light))
            {
                if (!TryParseColor(ambient_light, out var col, out var err))
                    return Response.Error("INVALID_PARAM",
                        new { param = "ambient_light", provided = ambient_light, expected = "'r,g,b[,a]' (0-1) or #hex", detail = err });
                parsedAmbientLight = col;
            }

            if (!string.IsNullOrEmpty(fog_color))
            {
                if (!TryParseColor(fog_color, out var col, out var err))
                    return Response.Error("INVALID_PARAM",
                        new { param = "fog_color", provided = fog_color, expected = "'r,g,b[,a]' (0-1) or #hex", detail = err });
                parsedFogColor = col;
            }

            if (float.IsNaN(fog_density) || float.IsInfinity(fog_density) || (fog_density < 0f && fog_density != -1f))
                return Response.Error("INVALID_PARAM",
                    new { param = "fog_density", provided = fog_density.ToString(), expected = "a finite number >= 0, or -1 to leave unchanged" });

            if (!parsedAmbientMode.HasValue && !parsedAmbientLight.HasValue && !fog.HasValue &&
                !parsedFogColor.HasValue && fog_density < 0f)
                return Response.Error("NO_PARAMS",
                    new { hint = "Provide at least one of ambient_mode, ambient_light, fog, fog_color, fog_density." });

            // Apply only after every provided value has passed validation, so an invalid later
            // parameter cannot leave RenderSettings partially modified.
            if (parsedAmbientMode.HasValue)
            {
                RenderSettings.ambientMode = parsedAmbientMode.Value;
                applied.Add($"ambientMode={parsedAmbientMode.Value}");
            }
            if (parsedAmbientLight.HasValue)
            {
                RenderSettings.ambientLight = parsedAmbientLight.Value;
                applied.Add("ambientLight");
            }
            if (fog.HasValue)
            {
                RenderSettings.fog = fog.Value;
                applied.Add($"fog={fog.Value}");
            }
            if (parsedFogColor.HasValue)
            {
                RenderSettings.fogColor = parsedFogColor.Value;
                applied.Add("fogColor");
            }
            if (fog_density >= 0f)
            {
                RenderSettings.fogDensity = fog_density;
                applied.Add($"fogDensity={fog_density}");
            }

            // RenderSettings has no discrete Undo target (it is scene-global state); mark the scene dirty so
            // the change persists on save.
            var scene = SceneManager.GetActiveScene();
            if (scene.IsValid() && !EditorApplication.isPlaying)
                EditorSceneManager.MarkSceneDirty(scene);

            var notes = EffectNotes(parsedAmbientLight.HasValue, RenderSettings.ambientMode, fog_density >= 0f, RenderSettings.fogMode,
                parsedFogColor.HasValue, RenderSettings.fog);
            return Response.Success(AppliedMessage(string.Join(", ", applied), EditorApplication.isPlaying) + notes,
                BuildLightingData());
        }

        // A value Unity stores but does not use in the current mode is not "applied" in any way the scene shows.
        internal static string EffectNotes(bool ambientLightSet, UnityEngine.Rendering.AmbientMode ambientMode, bool fogDensitySet, FogMode fogMode,
            bool fogColorSet = false, bool fogOn = true)
        {
            var notes = string.Empty;
            if (ambientLightSet && ambientMode != UnityEngine.Rendering.AmbientMode.Flat)
                notes += $" ambient_light is stored but only shows with ambient_mode Flat (the mode is {ambientMode}).";
            if (fogDensitySet && fogMode == FogMode.Linear)
                notes += " fog_density is stored but Linear fog ignores it (it applies to Exponential and ExponentialSquared).";
            if (!fogOn && (fogDensitySet || fogColorSet))
                notes += " fog is off, so nothing shows until fog=true (the fog values are stored).";
            return notes;
        }

        internal static string AppliedMessage(string applied, bool playing)
        {
            return $"Applied lighting changes: {applied}. " + (playing ? SceneFunctions.DiscardedWithPlayMode : "Save the scene to persist.");
        }

        [Description("Start an asynchronous lightmap bake of the current scene. Low value for a 2D project. " +
                     "Fails if a bake is already running; poll get_lighting_settings (is_running) to track progress.")]
        public static object BakeLightmaps()
        {
            if (EditorApplication.isPlaying)
                return Response.Error("BAKE_REQUIRES_EDIT_MODE",
                    new { hint = "Lightmap baking only runs in Edit Mode. Call exit_play_mode first, then retry bake_lightmaps." });

            if (Lightmapping.isRunning)
                return Response.Error("BAKE_ALREADY_RUNNING",
                    new { hint = "A lightmap bake is already in progress. Poll get_lighting_settings for is_running." });

            if (!Lightmapping.BakeAsync())
                return Response.Error("BAKE_NOT_STARTED",
                    new { hint = "Unity did not start the bake (for example the scene has nothing to bake, or lightmapping is disabled in the lighting settings)." });

            return Response.Success("Started asynchronous lightmap bake.",
                new { started = true, hint = "poll get_lighting_settings for isRunning" });
        }

        // --- Helpers ---

        private static object BuildLightingData()
        {
            var skybox = RenderSettings.skybox;
            object skyboxData = null;
            if (skybox != null)
            {
                var path = AssetDatabase.GetAssetPath(skybox);
                skyboxData = new { name = skybox.name, path = string.IsNullOrEmpty(path) ? null : path };
            }

            // Lightmapping.lightingSettings throws when the scene has no active LightingSettings asset -> guard it.
            object lightingSettings = null;
            string lightingSettingsError = null;
            try
            {
                var ls = Lightmapping.lightingSettings;
                if (ls != null)
                {
                    var lsPath = AssetDatabase.GetAssetPath(ls);
                    lightingSettings = new { name = ls.name, path = string.IsNullOrEmpty(lsPath) ? null : lsPath };
                }
            }
            catch (Exception ex)
            {
                lightingSettingsError = ex.Message;
            }

            return new
            {
                ambient_mode = RenderSettings.ambientMode.ToString(),
                ambient_light = ColorData(RenderSettings.ambientLight),
                ambient_intensity = RenderSettings.ambientIntensity,
                skybox = skyboxData,
                fog = RenderSettings.fog,
                fog_mode = RenderSettings.fogMode.ToString(),
                fog_color = ColorData(RenderSettings.fogColor),
                fog_density = RenderSettings.fogDensity,
                lighting_settings = lightingSettings,
                lighting_settings_error = lightingSettingsError,
                is_running = Lightmapping.isRunning,
                last_completed_utc = BakeControlFunctions.LightmapBakeCompletedUtc?.ToString("o"),
            };
        }

        private static object ColorData(Color c)
        {
            return new { r = c.r, g = c.g, b = c.b, a = c.a, hex = "#" + ColorUtility.ToHtmlStringRGBA(c) };
        }

        private static bool TryParseColor(string value, out Color color, out string error)
        {
            color = Color.black;
            error = null;

            if (string.IsNullOrEmpty(value))
            {
                error = "empty value";
                return false;
            }

            value = value.Trim();
            if (value.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(value, out color))
                    return true;
                error = "invalid hex color";
                return false;
            }

            var cleaned = value.Trim('(', ')', ' ');
            var parts = cleaned.Split(',');
            if (parts.Length < 3 || parts.Length > 4)
            {
                error = $"expected 'r,g,b' or 'r,g,b,a' or '#hex', got {parts.Length} component(s)";
                return false;
            }

            if (!ValueConverter.TryFiniteFloat(parts[0], out var r) ||
                !ValueConverter.TryFiniteFloat(parts[1], out var g) ||
                !ValueConverter.TryFiniteFloat(parts[2], out var b))
            {
                error = "non-numeric or non-finite r/g/b component";
                return false;
            }

            float a = 1f;
            if (parts.Length >= 4 && !ValueConverter.TryFiniteFloat(parts[3], out a))
            {
                error = "non-numeric or non-finite alpha component";
                return false;
            }

            color = new Color(r, g, b, a);
            return true;
        }
    }
}
