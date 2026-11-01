// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Reflection;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Timeline")]
    [RequiresPackage("com.unity.modules.director")]
    internal static class TimelineFunctions
    {
        // Resolved by FullName over the target's components so this file compiles WITHOUT an
        // asmdef reference to com.unity.timeline / UnityEngine.Playables.
        private const string PlayableDirectorTypeName = "UnityEngine.Playables.PlayableDirector";
        private const double MaxEvaluateSeconds = 1000000;

        [Description("Set a PlayableDirector's playback time and Evaluate() it — samples the bound Timeline at that time in the editor. Fully reflection-based, compiles without a com.unity.timeline reference. Reports the director's duration and PlayState; refuses a director with no Playable asset assigned (NO_PLAYABLE_ASSET). WARNING: Evaluate() destructively scrubs the sampled pose into the bound scene objects (Transforms/components); those writes are NOT individually undoable, so the owning scene is marked dirty and you should discard/reload the scene without saving if you only wanted a transient sample.")]
        public static object DirectorEvaluate(
            [ToolParam("Identifier of the GameObject holding a PlayableDirector (instance id, name, or path). Finds inactive objects too.")] string target,
            [ToolParam("Playback time in seconds to set before evaluating, 0 to 1000000 (default 0).", Required = false)] double time = 0)
        {
            if (!(time >= 0 && time <= MaxEvaluateSeconds))
                return Response.Error("INVALID_PARAM",
                    new { param = "time", provided = time, expected = "a finite number from 0 to 1000000 seconds" });

            var go = ObjectsHelper.FindTarget(target);
            if (go == null)
                return ObjectsHelper.NotFound("target", target);

            // Find the PlayableDirector purely by reflected type name so no compile-time dependency
            // on the Playables/Timeline assemblies is required.
            Component director = null;
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue; // missing script slot
                if (comp.GetType().FullName == PlayableDirectorTypeName)
                {
                    director = comp;
                    break;
                }
            }

            if (director == null)
                return Response.Error("PLAYABLE_DIRECTOR_NOT_FOUND",
                    new { target, hint = "target has no PlayableDirector component" });

            try
            {
                var directorType = director.GetType();

                var timeProp = directorType.GetProperty("time");
                if (timeProp == null || !timeProp.CanWrite)
                    return Response.Error("PLAYABLE_DIRECTOR_API_MISMATCH",
                        new { hint = "PlayableDirector.time property not found or not settable via reflection" });

                var evaluate = directorType.GetMethod("Evaluate", Type.EmptyTypes);
                if (evaluate == null)
                    return Response.Error("PLAYABLE_DIRECTOR_API_MISMATCH",
                        new { hint = "PlayableDirector.Evaluate() method not found via reflection" });

                UnityEngine.Object asset = null;
                var assetProp = directorType.GetProperty("playableAsset");
                if (assetProp != null)
                {
                    asset = assetProp.GetValue(director) as UnityEngine.Object;
                    if (asset == null)
                        return Response.Error("NO_PLAYABLE_ASSET",
                            new { target, hint = "the director has no Playable asset assigned, so there is nothing to sample" });
                }

                Undo.RecordObject(director, "Director Evaluate");

                // Evaluate() writes the sampled pose into the bound scene objects, which are not
                // captured by the director-only Undo above. Mark the owning scene dirty first so the
                // change is visibly unsaved even when Evaluate throws after a partial write.
                if (go.scene.IsValid())
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);

                timeProp.SetValue(director, time);
                evaluate.Invoke(director, null);

                // Read back the applied time; fall back to the requested value if unreadable.
                double appliedTime = time;
                var readTime = timeProp.GetValue(director);
                if (readTime is double rt) appliedTime = rt;

                double duration = 0;
                var durationProp = directorType.GetProperty("duration");
                if (durationProp != null && durationProp.GetValue(director) is double d) duration = d;

                string state = null;
                var stateProp = directorType.GetProperty("state");
                if (stateProp != null) state = stateProp.GetValue(director)?.ToString();

                return Response.Success(
                    $"Evaluated PlayableDirector on '{go.name}' at time {appliedTime}.",
                    new
                    {
                        gameObject = go.name,
                        instanceId = ObjectIdCodec.GetSerializableId(director),
                        playableAsset = asset != null ? asset.name : null,
                        playableAssetPath = asset != null ? AssetDatabase.GetAssetPath(asset) : null,
                        time = appliedTime,
                        duration,
                        state
                    });
            }
            catch (Exception ex)
            {
                // TargetInvocationException wraps the real fault thrown inside Evaluate/SetValue.
                var real = (ex as TargetInvocationException)?.InnerException ?? ex;
                return Response.Error("DIRECTOR_EVALUATE_FAILED",
                    new { message = real.Message, hint = "Reflection call into PlayableDirector failed" });
            }
        }
    }
}
