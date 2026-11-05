// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Build")]
    internal static class BuildInfoFunctions
    {
        [Description("List the BuildTarget values with their platform group, whether build support is installed for each " +
                     "(supported) and which one is active. Obsolete targets are left out. switch_build_target takes the names.")]
        [ReadOnlyTool]
        public static object ListBuildTargets()
        {
            var active = EditorUserBuildSettings.activeBuildTarget;
            var seen = new HashSet<BuildTarget>();
            var targets = new List<object>();

            foreach (var name in Enum.GetNames(typeof(BuildTarget)))
            {
                var field = typeof(BuildTarget).GetField(name);
                if (field.IsDefined(typeof(ObsoleteAttribute), false))
                    continue;

                var target = (BuildTarget)Enum.Parse(typeof(BuildTarget), name);
                if (!seen.Add(target))
                    continue;

#pragma warning disable CS0618 // BuildTargetGroup is still what IsBuildTargetSupported takes
                var group = BuildPipeline.GetBuildTargetGroup(target);
                if (group == BuildTargetGroup.Unknown)
                    continue;

                targets.Add(new
                {
                    target = target.ToString(),
                    group = group.ToString(),
                    supported = BuildPipeline.IsBuildTargetSupported(group, target),
                    active = target == active
                });
#pragma warning restore CS0618
            }

            return Response.Success($"{targets.Count} build target(s); active is {active}.", new { active = active.ToString(), targets });
        }

        [Description("List the Build Profile assets in the project and which one is active. Unity 6 only; " +
                     "answers FEATURE_UNAVAILABLE on earlier versions.")]
        [ReadOnlyTool]
        public static object ListBuildProfiles()
        {
#if UNITY_6000_0_OR_NEWER
            var activePath = UnityEditor.Build.Profile.BuildProfile.GetActiveBuildProfile() is UnityEngine.Object active
                ? AssetDatabase.GetAssetPath(active)
                : null;

            var profiles = AssetDatabase.FindAssets("t:BuildProfile")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(path => new { name = System.IO.Path.GetFileNameWithoutExtension(path), path, active = path == activePath })
                .ToArray();

            return Response.Success($"{profiles.Length} build profile(s).", new { activePath, profiles });
#else
            return Response.Error("FEATURE_UNAVAILABLE", hint: "Build Profiles exist from Unity 6 on.");
#endif
        }
    }
}
