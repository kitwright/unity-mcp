// Copyright (C) KitWright. Licensed under MIT.

// com.unity.modules.ai is optional; without it these tools disappear instead of breaking the build.
#if KITWRIGHT_AI
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEngine.AI;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("NavMesh")]
    internal static class NavMeshBakeFunctions
    {
        [Description("Report whether a legacy NavMesh bake is running ('baking' or 'idle'). bake_nav_mesh blocks until it " +
                     "finishes, so this matters for a bake started from the Navigation window.")]
        [ReadOnlyTool]
        public static object NavMeshBakeStatus()
        {
#pragma warning disable CS0618 // No drop-in replacement for the editor NavMesh bake state
            var running = UnityEditor.AI.NavMeshBuilder.isRunning;
#pragma warning restore CS0618
            return Response.Success(running ? "A NavMesh bake is running." : "No NavMesh bake is running.",
                new { state = running ? "baking" : "idle" });
        }

        [Description("Cancel a running legacy NavMesh bake. Answers NO_BAKE_RUNNING when none is running.")]
        public static object CancelNavMeshBake()
        {
#pragma warning disable CS0618
            if (!UnityEditor.AI.NavMeshBuilder.isRunning)
                return Response.Error("NO_BAKE_RUNNING");

            UnityEditor.AI.NavMeshBuilder.Cancel();
#pragma warning restore CS0618
            return Response.Success("Cancelled the NavMesh bake.");
        }

        [Description("Read the legacy NavMesh bake settings of every agent type: radius, height, max slope, step height, " +
                     "minimum region area and voxel size. The first entry is the default agent (Humanoid).")]
        [ReadOnlyTool]
        public static object GetNavMeshSettings()
        {
            var count = NavMesh.GetSettingsCount();
            var agents = new object[count];
            for (var i = 0; i < count; i++)
            {
                var s = NavMesh.GetSettingsByIndex(i);
                agents[i] = new
                {
                    agentTypeId = s.agentTypeID,
                    name = NavMesh.GetSettingsNameFromID(s.agentTypeID),
                    agentRadius = s.agentRadius,
                    agentHeight = s.agentHeight,
                    agentSlope = s.agentSlope,
                    agentClimb = s.agentClimb,
                    minRegionArea = s.minRegionArea,
                    overrideVoxelSize = s.overrideVoxelSize,
                    voxelSize = s.voxelSize
                };
            }

            return Response.Success($"Read the NavMesh settings of {count} agent type(s).", new { agents });
        }
    }
}
#endif
