// Copyright (C) KitWright. Licensed under MIT.

// com.unity.modules.ai is optional; without it this tool disappears instead of breaking the build.
#if KITWRIGHT_AI
using System;
using System.Linq;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using Unity.AI.Navigation;
using Unity.AI.Navigation.Editor;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// NavMeshSurface baking, in its own assembly so the package builds without com.unity.ai.navigation.
    /// bake_nav_mesh covers the legacy scene NavMesh; surfaces are baked through the package's own asset manager
    /// so the NavMeshData lands as an asset next to the scene, as the Bake button does.
    /// </summary>
    [ToolProvider("NavMesh")]
    internal static class NavMeshSurfaceFunctions
    {
        [Description("Bake every NavMeshSurface component in the loaded scenes (AI Navigation package), the way the Bake button " +
                     "does: the NavMeshData is saved as an asset next to the scene. Edit Mode only. A scene that was never saved " +
                     "is refused (SCENE_NOT_SAVED). With wait=true (default) the call returns when every surface is done, up to " +
                     "timeout_seconds; with wait=false it returns at once and the bake keeps running.")]
        [LongRunningTool(900)]
        public static async Task<object> BakeNavMeshSurfaces(
            [ToolParam("Wait for the bakes to finish. Default true.", Required = false)] bool wait = true,
            [ToolParam("Longest wait in seconds, 5 to 600. Default 300.", Required = false)] int timeout_seconds = 300)
        {
            if (EditorApplication.isPlaying)
                return Response.Error("BAKE_REQUIRES_EDIT_MODE", hint: "Call exit_play_mode first, then retry bake_nav_mesh_surfaces.");

            if (timeout_seconds < 5 || timeout_seconds > 600)
                return Response.Error("INVALID_PARAM", new { param = "timeout_seconds", provided = timeout_seconds }, "timeout_seconds must be from 5 to 600.");

            var surfaces = UnityEngine.Object.FindObjectsByType<NavMeshSurface>(FindObjectsSortMode.None)
                .Where(s => s.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(s))
                .ToArray();

            if (surfaces.Length == 0)
                return Response.Error("NO_NAVMESH_SURFACES", hint: "No NavMeshSurface component is active in the loaded scenes.");

            var unsaved = surfaces.Select(s => s.gameObject.scene).FirstOrDefault(scene => string.IsNullOrEmpty(scene.path));
            if (unsaved.IsValid())
                return Response.Error("SCENE_NOT_SAVED", new { scene = unsaved.name }, "The bake writes a NavMesh asset next to the scene. Save the scene with save_scene first.");

            var manager = NavMeshAssetManager.instance;
            manager.StartBakingSurfaces(surfaces.Cast<UnityEngine.Object>().ToArray());

            if (!wait)
                return Response.Success($"Started baking {surfaces.Length} NavMeshSurface(s).", new { started = surfaces.Length });

            var deadline = DateTime.UtcNow.AddSeconds(timeout_seconds);
            while (surfaces.Any(manager.IsSurfaceBaking))
            {
                if (DateTime.UtcNow >= deadline)
                    return Response.Error("BAKE_TIMEOUT", new { stillBaking = surfaces.Count(manager.IsSurfaceBaking) },
                        "The bake is still running in the editor. Check the Navigation window, or retry with a longer timeout_seconds.");

                await Task.Delay(250);
            }

            var baked = surfaces.Select(s => new
            {
                gameObject = s.gameObject.name,
                scene = s.gameObject.scene.path,
                agentType = NavMeshSurfaceAgentName(s),
                navMeshData = s.navMeshData != null ? AssetDatabase.GetAssetPath(s.navMeshData) : null
            }).ToArray();

            return Response.Success($"Baked {baked.Length} NavMeshSurface(s).", new { baked });
        }

        private static string NavMeshSurfaceAgentName(NavMeshSurface surface) =>
            UnityEngine.AI.NavMesh.GetSettingsNameFromID(surface.agentTypeID);
    }
}
#endif
