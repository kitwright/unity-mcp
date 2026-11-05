// Copyright (C) KitWright. Licensed under MIT.

// com.unity.modules.ai is optional; without it these tools disappear instead of breaking the build.
#if KITWRIGHT_AI
using System;
using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.AI;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>The NavMesh tools under the Unity CLI's spelling (navmesh, one word); KitWright's own are bake_nav_mesh and its family.</summary>
    [ToolProvider("NavMesh")]
    internal static class UnityCliNavMeshFunctions
    {
        // Legacy agent settings live in the NavMeshProjectSettings page, whose first entry is the default agent.
        private static readonly (string name, string path)[] Fields =
        {
            ("agentRadius", "m_Settings.Array.data[0].agentRadius"),
            ("agentHeight", "m_Settings.Array.data[0].agentHeight"),
            ("agentSlope", "m_Settings.Array.data[0].agentSlope"),
            ("agentClimb", "m_Settings.Array.data[0].agentClimb"),
            ("minRegionArea", "m_Settings.Array.data[0].minRegionArea"),
            ("voxelSize", "m_Settings.Array.data[0].cellSize")
        };

        [Description("Bake the legacy scene NavMesh, like bake_nav_mesh.")]
        [LongRunningTool(900)]
        public static object BakeNavmesh() => NavMeshFunctions.BakeNavMesh();

        [Description("Clear the baked legacy NavMesh of the scene, like clear_nav_mesh.")]
        public static object ClearNavmesh() => NavMeshFunctions.ClearNavMesh();

        [Description("Report whether a legacy NavMesh bake is running: baking or idle.")]
        [ReadOnlyTool]
        public static object NavmeshBakeStatus() => NavMeshBakeFunctions.NavMeshBakeStatus();

        [Description("Cancel a running legacy NavMesh bake.")]
        public static object CancelNavmeshBake() => NavMeshBakeFunctions.CancelNavMeshBake();

        [Description("Read the legacy NavMesh bake settings of the agents (the first is the default agent).")]
        [ReadOnlyTool]
        public static object GetNavmeshSettings() => NavMeshBakeFunctions.GetNavMeshSettings();

        [Description("Apply a subset of the legacy NavMesh bake settings to the default agent: settings is a JSON object with agentRadius, " +
                     "agentHeight, agentSlope, agentClimb, minRegionArea and/or voxelSize. Reports {applied, unknown}. dry_run validates only.")]
        public static object SetNavmeshSettings(
            [ToolParam("JSON object of the fields to set")] string settings,
            [ToolParam("Validate without writing", Required = false)] bool dry_run = false)
        {
            if (!UnityCliSettingsFunctions.TryParse(settings, out var json, out var error))
                return error;

            var target = Unsupported.GetSerializedAssetInterfaceSingleton("NavMeshProjectSettings");
            if (target == null)
                return Response.Error("SETTINGS_SINGLETON_NOT_FOUND", new { singleton = "NavMeshProjectSettings" });

            var so = new SerializedObject(target);
            var applied = new List<string>();
            var unknown = new List<string>();
            var writes = new List<Action>();

            foreach (var property in json.Properties())
            {
                var field = Fields.FirstOrDefault(f => f.name == property.Name);
                var serialized = field.path == null ? null : so.FindProperty(field.path);
                if (serialized == null) { unknown.Add(property.Name); continue; }

                if (property.Value.Type != JTokenType.Float && property.Value.Type != JTokenType.Integer)
                {
                    unknown.Add(property.Name + " (expected a number)");
                    continue;
                }

                var value = property.Value.Value<float>();
                var isVoxel = property.Name == "voxelSize";
                writes.Add(() =>
                {
                    serialized.floatValue = value;
                    if (isVoxel)
                        so.FindProperty("m_Settings.Array.data[0].manualCellSize").intValue = 1;
                });
                applied.Add(property.Name);
            }

            if (!dry_run && writes.Count > 0)
            {
                foreach (var write in writes) write();
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
            }

            return Response.Success(dry_run ? "Validated; nothing was written." : $"Applied {applied.Count} NavMesh setting(s).", new { applied, unknown, dry_run });
        }
    }
}
#endif
