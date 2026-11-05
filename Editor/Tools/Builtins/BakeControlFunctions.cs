// Copyright (C) KitWright. Licensed under MIT.
using System;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// Cancel and clear for the two asynchronous bakes, and the occlusion bake's start and status. bake_lightmaps
    /// starts a lightmap bake and get_lighting_settings reports it; the occlusion bake is started here, since nothing else does.
    /// </summary>
    [ToolProvider("Lighting")]
    [InitializeOnLoad]
    internal static class BakeControlFunctions
    {
        private const float UnsetFloat = float.MinValue;

        internal static DateTime? LightmapBakeCompletedUtc { get; private set; }

        static BakeControlFunctions()
        {
            Lightmapping.bakeCompleted += () => LightmapBakeCompletedUtc = DateTime.UtcNow;
        }

        [Description("Cancel the running lightmap bake. Answers NO_BAKE_RUNNING when none is running.")]
        public static object CancelLightingBake()
        {
            if (!Lightmapping.isRunning)
                return Response.Error("NO_BAKE_RUNNING");

            Lightmapping.Cancel();
            return Response.Success("Cancelled the lightmap bake.");
        }

        [Description("Clear the baked lightmap data of the open scene(s). Destructive and not undoable: requires confirm=true. " +
                     "Edit Mode only. include_disk_cache also clears the GI disk cache.")]
        public static object ClearBakedLighting(
            [ToolParam("Must be true to actually clear", Required = false)] bool confirm = false,
            [ToolParam("Also clear the GI disk cache. Default false.", Required = false)] bool include_disk_cache = false)
        {
            if (EditorApplication.isPlaying)
                return Response.Error("EDIT_MODE_REQUIRED", hint: "Call exit_play_mode first, then retry clear_baked_lighting.");

            if (Lightmapping.isRunning)
                return Response.Error("BAKE_RUNNING", hint: "Cancel it with cancel_lighting_bake first.");

            if (!confirm)
                return Response.Error("CONFIRM_REQUIRED",
                    hint: "Clearing baked lighting cannot be undone. Pass confirm=true to proceed.");

            Lightmapping.Clear();
            if (include_disk_cache)
                Lightmapping.ClearDiskCache();

            return Response.Success(include_disk_cache
                ? "Cleared the baked lightmaps and the GI disk cache."
                : "Cleared the baked lightmaps.");
        }

        [Description("Start an asynchronous occlusion-culling bake of the open scene(s) (StaticOcclusionCulling.GenerateInBackground). " +
                     "Returns at once; poll occlusion_bake_status. An omitted size keeps Unity's current value. Edit Mode only.")]
        public static object BakeOcclusionCulling(
            [ToolParam("Smallest object that occludes others, in meters (omitted: unchanged)", Required = false)] float smallest_occluder = UnsetFloat,
            [ToolParam("Smallest gap the view can see through, in meters (omitted: unchanged)", Required = false)] float smallest_hole = UnsetFloat,
            [ToolParam("Backface threshold, 1 to 100; lower trims more backfaces (omitted: unchanged)", Required = false)] float backface_threshold = UnsetFloat)
        {
            if (EditorApplication.isPlaying)
                return Response.Error("BAKE_REQUIRES_EDIT_MODE", hint: "Call exit_play_mode first, then retry bake_occlusion_culling.");

            if (StaticOcclusionCulling.isRunning)
                return Response.Error("BAKE_ALREADY_RUNNING", hint: "Poll occlusion_bake_status, or cancel with cancel_occlusion_bake.");

            if (IsSet(smallest_occluder) && !(smallest_occluder > 0f) || IsSet(smallest_hole) && !(smallest_hole > 0f))
                return Response.Error("INVALID_PARAM", new { smallest_occluder, smallest_hole },
                    "smallest_occluder and smallest_hole must be finite numbers above 0.");

            if (IsSet(backface_threshold) && !(backface_threshold >= 1f && backface_threshold <= 100f))
                return Response.Error("INVALID_PARAM", new { param = "backface_threshold", provided = backface_threshold },
                    "backface_threshold must be a number from 1 to 100.");

            if (IsSet(smallest_occluder)) StaticOcclusionCulling.smallestOccluder = smallest_occluder;
            if (IsSet(smallest_hole)) StaticOcclusionCulling.smallestHole = smallest_hole;
            if (IsSet(backface_threshold)) StaticOcclusionCulling.backfaceThreshold = backface_threshold;

            if (!StaticOcclusionCulling.GenerateInBackground())
                return Response.Error("BAKE_NOT_STARTED",
                    hint: "Unity did not start the bake: the open scene may hold no Occluder Static geometry.");

            return Response.Success("Started the occlusion-culling bake.", new
            {
                started = true,
                smallestOccluder = StaticOcclusionCulling.smallestOccluder,
                smallestHole = StaticOcclusionCulling.smallestHole,
                backfaceThreshold = StaticOcclusionCulling.backfaceThreshold
            });
        }

        [Description("Report the occlusion bake state: 'baking' while one runs, else 'idle'.")]
        [ReadOnlyTool]
        public static object OcclusionBakeStatus()
        {
            var running = StaticOcclusionCulling.isRunning;
            return Response.Success(running ? "An occlusion bake is running." : "No occlusion bake is running.",
                new { state = running ? "baking" : "idle" });
        }

        [Description("Cancel the running occlusion bake. Answers NO_BAKE_RUNNING when none is running.")]
        public static object CancelOcclusionBake()
        {
            if (!StaticOcclusionCulling.isRunning)
                return Response.Error("NO_BAKE_RUNNING");

            StaticOcclusionCulling.Cancel();
            return Response.Success("Cancelled the occlusion bake.");
        }

        [Description("Clear the baked occlusion-culling data of the open scene(s). Destructive and not undoable: " +
                     "requires confirm=true. Edit Mode only.")]
        public static object ClearOcclusionCulling(
            [ToolParam("Must be true to actually clear", Required = false)] bool confirm = false)
        {
            if (EditorApplication.isPlaying)
                return Response.Error("EDIT_MODE_REQUIRED", hint: "Call exit_play_mode first, then retry clear_occlusion_culling.");

            if (StaticOcclusionCulling.isRunning)
                return Response.Error("BAKE_RUNNING", hint: "Cancel it with cancel_occlusion_bake first.");

            if (!confirm)
                return Response.Error("CONFIRM_REQUIRED",
                    hint: "Clearing occlusion data cannot be undone. Pass confirm=true to proceed.");

            StaticOcclusionCulling.Clear();
            return Response.Success("Cleared the baked occlusion-culling data.");
        }

        private static bool IsSet(float value) => value != UnsetFloat;
    }
}
