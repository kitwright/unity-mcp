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
