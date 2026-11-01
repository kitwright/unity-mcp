// Copyright (C) KitWright. Licensed under MIT.

// com.unity.modules.particlesystem is optional; without it these tools disappear instead of breaking the build.
#if KITWRIGHT_PARTICLES
using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Particle")]
    internal static class ParticleFunctions
    {
        private const float MaxSimulateSeconds = 60f;

        [Description("Control a ParticleSystem's playback for deterministic VFX inspection/screenshots. " +
                     "action='simulate' fast-forwards the system to a fixed time (useful in Edit Mode to freeze a VFX " +
                     "at a repeatable frame for capture); 'play'/'pause'/'stop'/'restart' drive the matching playback state. " +
                     "Uses the ParticleSystem on the target GameObject; when the target has none (a VFX container), acts on " +
                     "every top-level ParticleSystem under it that is active in the hierarchy (inactive ones are named in 'inactiveSkipped'). " +
                     "Returns the resulting playback state (isPlaying/isEmitting/isStopped/particleCount, combined over those " +
                     "systems), the first system's main-module loop/duration, and 'systems' naming the objects acted on.")]
        public static object ParticleControl(
            [ToolParam("Identifier of the GameObject holding the ParticleSystem, or a container of them (instance id, name, or hierarchy path). Inactive objects are found.")] string target,
            [ToolParam("Action: 'simulate' | 'play' | 'pause' | 'stop' | 'restart'", Required = false)] string action = "simulate",
            [ToolParam("Time in seconds to fast-forward to, 0 to 60; anything else is refused (only used by action='simulate')", Required = false)] float time = 0.5f,
            [ToolParam("Apply the action to the child ParticleSystems of each system as well", Required = false)] bool with_children = true)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null)
                return ObjectsHelper.NotFound("target", target);

            var systems = ResolveSystems(go, out var inactiveSkipped);
            if (systems.Count == 0)
                return Response.Error("PARTICLE_SYSTEM_NOT_FOUND",
                    new
                    {
                        target,
                        gameObject = go.name,
                        inactiveSkipped,
                        hint = inactiveSkipped.Count > 0
                            ? "Every ParticleSystem under the target is inactive in the hierarchy, so none was acted on. Activate the objects first."
                            : "No ParticleSystem on the target or its children."
                    });

            var first = systems[0];
            var normalized = string.IsNullOrEmpty(action) ? "simulate" : action.Trim().ToLowerInvariant();

            // These are transient playback-state controls, not serialized-field mutations, so there is no
            // meaningful Undo entry to record (Undo.RecordObject would only dirty the scene without capturing state).
            switch (normalized)
            {
                case "simulate":
                    if (!(time >= 0f && time <= MaxSimulateSeconds))
                        return Response.Error("INVALID_PARAM",
                            new { param = "time", provided = time, expected = "a finite number from 0 to 60 seconds" });
                    // fixedTimeStep=false does one variable step, so a large time can't churn tens of
                    // thousands of fixed sub-steps on the main thread and freeze the editor.
                    foreach (var ps in systems)
                        ps.Simulate(time, with_children, true, false);
                    break;
                case "play":
                    foreach (var ps in systems)
                        ps.Play(with_children);
                    break;
                case "pause":
                    foreach (var ps in systems)
                        ps.Pause(with_children);
                    break;
                case "stop":
                    foreach (var ps in systems)
                        ps.Stop(with_children);
                    break;
                case "restart":
                    // Play() on a paused system (e.g. after Simulate) resumes at its old time and on a
                    // playing one does nothing; stopping first resets time so t=0 bursts fire again.
                    foreach (var ps in systems)
                    {
                        ps.Stop(with_children, ParticleSystemStopBehavior.StopEmittingAndClear);
                        ps.Play(with_children);
                    }
                    break;
                default:
                    return Response.Error("UNKNOWN_ACTION",
                        new { action, expected = "simulate|play|pause|stop|restart" });
            }

            var main = first.main;
            var subject = systems.Count == 1
                ? $"ParticleSystem '{first.name}'"
                : $"{systems.Count} ParticleSystems under '{go.name}'";
            var particleCount = systems.Sum(s => s.particleCount);
            var message = normalized == "simulate"
                ? $"Simulated {subject} to {time}s (particleCount={particleCount})."
                : $"{subject} action '{normalized}' applied.";
            if (inactiveSkipped.Count > 0)
                message += $" Skipped {inactiveSkipped.Count} inactive system(s).";

            return Response.Success(message, new
            {
                action = normalized,
                gameObject = go.name,
                instanceId = ObjectIdCodec.GetSerializableId(go),
                systems = systems.Select(s => new { name = s.gameObject.name, instanceId = ObjectIdCodec.GetSerializableId(s.gameObject) }).ToArray(),
                inactiveSkipped,
                isPlaying = systems.Any(s => s.isPlaying),
                isEmitting = systems.Any(s => s.isEmitting),
                isStopped = systems.All(s => s.isStopped),
                particleCount,
                loop = main.loop,
                duration = main.duration
            });
        }

        // The system on the target itself, else every system under it that has no other system between it
        // and the target (a VFX container holds Fire/Smoke/Sparks side by side). Inactive ones do nothing and
        // would pollute the combined state, so they are named in inactiveSkipped instead.
        internal static List<ParticleSystem> ResolveSystems(GameObject go, out List<string> inactiveSkipped)
        {
            inactiveSkipped = new List<string>();
            var own = go.GetComponent<ParticleSystem>();
            if (own != null)
                return new List<ParticleSystem> { own };

            var roots = new List<ParticleSystem>();
            foreach (var candidate in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                var nested = false;
                for (var parent = candidate.transform.parent; parent != null && parent != go.transform; parent = parent.parent)
                {
                    if (parent.GetComponent<ParticleSystem>() == null) continue;
                    nested = true;
                    break;
                }

                if (nested) continue;
                if (candidate.gameObject.activeInHierarchy)
                    roots.Add(candidate);
                else
                    inactiveSkipped.Add(candidate.gameObject.name);
            }

            return roots;
        }
    }
}
#endif
