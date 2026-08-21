// Copyright (C) KitWright. Licensed under MIT.

// com.unity.modules.audio is optional; without it these tools disappear instead of breaking the build.
#if KITWRIGHT_AUDIO
using System;
using System.Reflection;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Audio")]
    internal static class AudioFunctions
    {
        [Description("Add and configure an AudioSource on a GameObject. Only the parameters you pass are applied. clip/mixer_group accept an asset path.")]
        public static object AddAudioSource(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target,
            [ToolParam("AudioClip asset path (e.g. 'Assets/Sfx/hit.wav')", Required = false)] string clip = null,
            [ToolParam("AudioMixerGroup asset path, or 'MixerAsset.aud/GroupName'", Required = false)] string mixer_group = null,
            [ToolParam("Volume 0..1", Required = false)] float? volume = null,
            [ToolParam("Pitch (0.1..3 typical)", Required = false)] float? pitch = null,
            [ToolParam("Loop the clip", Required = false)] bool? loop = null,
            [ToolParam("Play automatically when the scene starts", Required = false)] bool? play_on_awake = null,
            [ToolParam("Spatial blend 0 (2D) .. 1 (3D)", Required = false)] float? spatial_blend = null,
            [ToolParam("Min distance for 3D attenuation", Required = false)] float? min_distance = null,
            [ToolParam("Max distance for 3D attenuation", Required = false)] float? max_distance = null)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            var src = go.GetComponent<AudioSource>() ?? Undo.AddComponent<AudioSource>(go);
            Undo.RecordObject(src, "Configure AudioSource");

            if (clip != null)
            {
                var loaded = AssetDatabase.LoadAssetAtPath<AudioClip>(clip);
                if (loaded == null) return Response.Error("CLIP_NOT_FOUND", new { clip });
                src.clip = loaded;
            }
            if (mixer_group != null)
            {
                var group = LoadMixerGroup(mixer_group);
                if (group == null) return Response.Error("MIXER_GROUP_NOT_FOUND", new { mixer_group });
                src.outputAudioMixerGroup = group;
            }
            if (volume.HasValue) src.volume = Mathf.Clamp01(volume.Value);
            if (pitch.HasValue) src.pitch = pitch.Value;
            if (loop.HasValue) src.loop = loop.Value;
            if (play_on_awake.HasValue) src.playOnAwake = play_on_awake.Value;
            if (spatial_blend.HasValue) src.spatialBlend = Mathf.Clamp01(spatial_blend.Value);
            if (min_distance.HasValue) src.minDistance = Mathf.Max(0f, min_distance.Value);
            if (max_distance.HasValue) src.maxDistance = Mathf.Max(0f, max_distance.Value);

            EditorUtility.SetDirty(src);
            return Response.Success($"AudioSource configured on '{go.name}'.", Describe(src));
        }

        [Description("Set global audio settings: master volume (AudioListener.volume) and/or pause all audio (AudioListener.pause).")]
        public static object SetGlobalAudio(
            [ToolParam("Master volume 0..1", Required = false)] float? volume = null,
            [ToolParam("Pause/unpause all audio", Required = false)] bool? pause = null)
        {
            if (volume.HasValue) AudioListener.volume = Mathf.Clamp01(volume.Value);
            if (pause.HasValue) AudioListener.pause = pause.Value;
            return Response.Success("Global audio updated.", new { volume = AudioListener.volume, pause = AudioListener.pause });
        }

        [Description("Add an AudioListener to a GameObject (usually the main camera). Fails if the scene already has one elsewhere unless allow_multiple is true.")]
        public static object AddAudioListener(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target,
            [ToolParam("Add even if another AudioListener already exists in the scene", Required = false)] bool allow_multiple = false)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            if (!allow_multiple)
            {
#if UNITY_2023_1_OR_NEWER
                var existing = UnityEngine.Object.FindFirstObjectByType<AudioListener>();
#else
                var existing = UnityEngine.Object.FindObjectOfType<AudioListener>();
#endif
                if (existing != null && existing.gameObject != go)
                    return Response.Error("LISTENER_EXISTS", new { existingOn = existing.gameObject.name, hint = "pass allow_multiple=true to add anyway" });
            }

            if (go.GetComponent<AudioListener>() != null)
                return Response.Success($"'{go.name}' already has an AudioListener.");

            Undo.AddComponent<AudioListener>(go);
            return Response.Success($"AudioListener added to '{go.name}'.");
        }

        [Description("Get the AudioSource settings on a GameObject.")]
        [ReadOnlyTool]
        public static object GetAudioSourceInfo(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);
            var src = go.GetComponent<AudioSource>();
            if (src == null) return Response.Error("NO_AUDIO_SOURCE", new { target });
            return Response.Success($"AudioSource on '{go.name}'.", Describe(src));
        }

        [Description("Preview-play an AudioClip in the editor without entering Play Mode (uses the internal AudioUtil API). Pass an AudioClip asset path.")]
        public static object PlayClipPreview(
            [ToolParam("AudioClip asset path (e.g. 'Assets/Sfx/hit.wav')")] string clip,
            [ToolParam("Loop the preview", Required = false)] bool loop = false)
        {
            var loaded = AssetDatabase.LoadAssetAtPath<AudioClip>(clip);
            if (loaded == null) return Response.Error("CLIP_NOT_FOUND", new { clip });

            if (!TryInvokeAudioUtil("PlayPreviewClip", new object[] { loaded, 0, loop },
                    new[] { typeof(AudioClip), typeof(int), typeof(bool) }, out var error))
                return Response.Error("AUDIO_PREVIEW_UNAVAILABLE", new { message = error });

            return Response.Success($"Previewing '{clip}' ({loaded.length:F2}s).");
        }

        [Description("Stop all editor audio clip previews started by play_clip_preview.")]
        public static object StopClipPreview()
        {
            if (!TryInvokeAudioUtil("StopAllPreviewClips", Array.Empty<object>(), Type.EmptyTypes, out var error)
                && !TryInvokeAudioUtil("StopAllClips", Array.Empty<object>(), Type.EmptyTypes, out error))
                return Response.Error("AUDIO_PREVIEW_UNAVAILABLE", new { message = error });
            return Response.Success("Stopped editor audio previews.");
        }

        private static object Describe(AudioSource src) => new
        {
            clip = src.clip != null ? AssetDatabase.GetAssetPath(src.clip) : null,
            clipName = src.clip != null ? src.clip.name : null,
            volume = src.volume,
            pitch = src.pitch,
            loop = src.loop,
            playOnAwake = src.playOnAwake,
            spatialBlend = src.spatialBlend,
            minDistance = src.minDistance,
            maxDistance = src.maxDistance,
            mixerGroup = src.outputAudioMixerGroup != null ? src.outputAudioMixerGroup.name : null
        };

        private static AudioMixerGroup LoadMixerGroup(string spec)
        {
            string assetPath = spec;
            string groupName = null;
            int slash = spec.IndexOf('/');
            if (slash >= 0) { assetPath = spec.Substring(0, slash); groupName = spec.Substring(slash + 1); }

            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(assetPath);
            if (mixer == null) return null;

            var groups = mixer.FindMatchingGroups(groupName ?? string.Empty);
            return groups != null && groups.Length > 0 ? groups[0] : null;
        }

        private static bool TryInvokeAudioUtil(string method, object[] args, Type[] signature, out string error)
        {
            error = null;
            var audioUtil = Type.GetType("UnityEditor.AudioUtil, UnityEditor");
            if (audioUtil == null) { error = "UnityEditor.AudioUtil not found"; return false; }

            var mi = audioUtil.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, signature, null);
            if (mi == null) { error = $"AudioUtil.{method} signature not found (Unity version mismatch)"; return false; }

            mi.Invoke(null, args);
            return true;
        }
    }
}
#endif
