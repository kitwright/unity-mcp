// Copyright (C) KitWright. Licensed under MIT.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
#if KITWRIGHT_URP
using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine.Rendering;
#endif

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Volume")]
    [RequiresPackage("com.unity.render-pipelines.universal")]
    internal static class VolumeFunctions
    {
        private const string UrpRequiredHint =
            "This project has no Universal Render Pipeline package. Post-processing Volumes require URP. " +
            "Install 'com.unity.render-pipelines.universal' and assign a URP asset in Project Settings > Graphics, then retry.";

#if !KITWRIGHT_URP
        private static object UrpRequired() => Response.Error("URP_REQUIRED", new { hint = UrpRequiredHint });
#endif

        [Description("Create a post-processing Volume GameObject (URP). global=true affects the whole scene; false makes a local box-collider volume. Returns instanceId. Requires URP.")]
        public static object CreateVolume(
            [ToolParam("Name of the volume object", Required = false)] string name = "PP Volume",
            [ToolParam("Global volume (affects whole scene) vs local box", Required = false)] bool global = true,
            [ToolParam("Priority (higher wins when volumes overlap)", Required = false)] float priority = 0f)
        {
#if KITWRIGHT_URP
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
            var volume = Undo.AddComponent<Volume>(go);
            volume.isGlobal = global;
            volume.priority = priority;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var path = AssetDatabase.GenerateUniqueAssetPath($"Assets/{name}.asset");
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            volume.sharedProfile = profile;

#if KITWRIGHT_PHYSICS
            if (!global)
                Undo.AddComponent<BoxCollider>(go).isTrigger = true;
#endif

            Selection.activeGameObject = go;
            return Response.Success($"Created {(global ? "global" : "local")} volume '{name}'.", new
            {
                instanceId = ObjectIdCodec.GetSerializableId(go),
                name = go.name,
                profilePath = path,
                global,
                priority
            });
#else
            return UrpRequired();
#endif
        }

        [Description("List post-processing overrides on a Volume's profile (URP), with each override's active state. Requires URP.")]
        [ReadOnlyTool]
        public static object GetVolumeInfo(
            [ToolParam("Volume GameObject name, path, or instance ID")] string target)
        {
#if KITWRIGHT_URP
            if (!TryResolveProfile(target, out var profile, out var err)) return err;
            var overrides = profile.components.Select(c => new
            {
                type = c.GetType().Name,
                active = c.active
            }).ToArray();
            return Response.Success($"Volume '{target}' has {overrides.Length} override(s).", overrides);
#else
            return UrpRequired();
#endif
        }

        [Description("Add a post-processing override to a Volume profile (URP). override_type is the effect name: Bloom, Tonemapping, ColorAdjustments, DepthOfField, Vignette, ChromaticAberration, MotionBlur, FilmGrain, LensDistortion, etc. Requires URP.")]
        public static object AddVolumeOverride(
            [ToolParam("Volume GameObject name, path, or instance ID")] string target,
            [ToolParam("Effect type name (e.g. 'Bloom', 'Tonemapping')")] string override_type)
        {
#if KITWRIGHT_URP
            if (!TryResolveProfile(target, out var profile, out var err)) return err;

            var type = ResolveOverrideType(override_type);
            if (type == null) return Response.Error("OVERRIDE_TYPE_NOT_FOUND", new { override_type });
            if (profile.components.Any(c => c.GetType() == type))
                return Response.Error("OVERRIDE_ALREADY_ADDED", new { override_type = type.Name });

            Undo.RegisterCompleteObjectUndo(profile, "Add Volume Override");
            var comp = (VolumeComponent)profile.Add(type, overrides: true);
            comp.active = true;
            EditorUtility.SetDirty(profile);

            return Response.Success($"Added '{type.Name}' to '{target}'.", new { override_type = type.Name });
#else
            return UrpRequired();
#endif
        }

        [Description("Remove a post-processing override from a Volume profile (URP). Requires URP.")]
        public static object RemoveVolumeOverride(
            [ToolParam("Volume GameObject name, path, or instance ID")] string target,
            [ToolParam("Effect type name (e.g. 'Bloom')")] string override_type)
        {
#if KITWRIGHT_URP
            if (!TryResolveProfile(target, out var profile, out var err)) return err;

            var type = ResolveOverrideType(override_type);
            if (type == null) return Response.Error("OVERRIDE_TYPE_NOT_FOUND", new { override_type });
            var comp = profile.components.FirstOrDefault(c => c.GetType() == type);
            if (comp == null) return Response.Error("OVERRIDE_NOT_PRESENT", new { override_type = type.Name });

            Undo.RegisterCompleteObjectUndo(profile, "Remove Volume Override");
            profile.Remove(type);
            EditorUtility.SetDirty(profile);

            return Response.Success($"Removed '{type.Name}' from '{target}'.", new { override_type = type.Name });
#else
            return UrpRequired();
#endif
        }

        [Description("Set a single parameter on a Volume override and enable its overrideState (URP). Value is coerced to the parameter type: number, bool, enum name, or 'r,g,b,a' color. Requires URP.")]
        public static object SetVolumeOverrideProperty(
            [ToolParam("Volume GameObject name, path, or instance ID")] string target,
            [ToolParam("Effect type name (e.g. 'Bloom')")] string override_type,
            [ToolParam("Parameter field name (e.g. 'intensity', 'threshold')")] string property,
            [ToolParam("Value: number, true/false, enum name, or 'r,g,b,a'")] string value)
        {
#if KITWRIGHT_URP
            if (!TryResolveProfile(target, out var profile, out var err)) return err;

            var type = ResolveOverrideType(override_type);
            if (type == null) return Response.Error("OVERRIDE_TYPE_NOT_FOUND", new { override_type });
            var comp = profile.components.FirstOrDefault(c => c.GetType() == type);
            if (comp == null) return Response.Error("OVERRIDE_NOT_PRESENT", new { override_type = type.Name, hint = "Call add_volume_override first." });

            var field = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(f => string.Equals(f.Name, property, StringComparison.OrdinalIgnoreCase)
                                     && typeof(VolumeParameter).IsAssignableFrom(f.FieldType));
            if (field == null) return Response.Error("PROPERTY_NOT_FOUND", new
            {
                property,
                available = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(f => typeof(VolumeParameter).IsAssignableFrom(f.FieldType))
                    .Select(f => f.Name).ToArray()
            });

            var param = (VolumeParameter)field.GetValue(comp);
            var valueProp = field.FieldType.GetProperty("value");
            if (valueProp == null) return Response.Error("PARAM_NOT_SETTABLE", new { property });

            if (!TryCoerce(value, valueProp.PropertyType, out var coerced))
                return Response.Error("VALUE_COERCION_FAILED", new { value, expected = valueProp.PropertyType.Name });

            Undo.RegisterCompleteObjectUndo(profile, "Set Volume Override Property");
            valueProp.SetValue(param, coerced);
            param.overrideState = true;
            comp.active = true;
            EditorUtility.SetDirty(profile);

            return Response.Success($"Set {type.Name}.{property} = {value}.", new { override_type = type.Name, property, value });
#else
            return UrpRequired();
#endif
        }

#if KITWRIGHT_URP
        internal static Type ResolveOverrideType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .Where(t => t != null && typeof(VolumeComponent).IsAssignableFrom(t) && !t.IsAbstract)
                .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryResolveProfile(string target, out VolumeProfile profile, out object error)
        {
            profile = null;
            error = null;
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) { error = ObjectsHelper.NotFound("target", target); return false; }
            var volume = go.GetComponent<Volume>();
            if (volume == null) { error = Response.Error("NO_VOLUME", new { target }); return false; }
            profile = volume.sharedProfile;
            if (profile == null) { error = Response.Error("NO_VOLUME_PROFILE", new { target }); return false; }
            return true;
        }

        private static bool TryCoerce(string raw, Type t, out object result)
        {
            result = null;
            var ci = CultureInfo.InvariantCulture;
            try
            {
                if (t == typeof(float)) { result = float.Parse(raw, ci); return true; }
                if (t == typeof(int)) { result = int.Parse(raw, ci); return true; }
                if (t == typeof(bool)) { result = bool.Parse(raw); return true; }
                if (t == typeof(Color))
                {
                    var p = raw.Split(',');
                    if (p.Length < 3) return false;
                    float a = p.Length >= 4 ? float.Parse(p[3], ci) : 1f;
                    result = new Color(float.Parse(p[0], ci), float.Parse(p[1], ci), float.Parse(p[2], ci), a);
                    return true;
                }
                if (t.IsEnum) { result = Enum.Parse(t, raw, true); return true; }
            }
            catch { return false; }
            return false;
        }
#endif
    }
}
