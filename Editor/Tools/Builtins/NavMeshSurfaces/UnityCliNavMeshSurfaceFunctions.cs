// Copyright (C) KitWright. Licensed under MIT.

// com.unity.modules.ai is optional; without it this tool disappears instead of breaking the build.
#if KITWRIGHT_AI
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>bake_nav_mesh_surfaces under the Unity CLI's spelling.</summary>
    [ToolProvider("UnityCli")]
    internal static class UnityCliNavMeshSurfaceFunctions
    {
        [Description("Bake every NavMeshSurface in the loaded scenes (AI Navigation package), like bake_nav_mesh_surfaces. Waits for the bakes.")]
        [LongRunningTool(900)]
        public static Task<object> BakeNavmeshSurfaces() => NavMeshSurfaceFunctions.BakeNavMeshSurfaces();
    }
}
#endif
