// Copyright (C) KitWright. Licensed under MIT.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
#if KITWRIGHT_URP
using System;
using System.Collections.Generic;
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

        [Description("Create a post-processing Volume GameObject (URP). global=true affects the whole scene; false makes a local box-collider volume. " +
                     "The profile is saved as Assets/<name>.asset, so name must be a valid file name. Returns instanceId. Requires URP.")]
        public static object CreateVolume(
            [ToolParam("Name of the volume object, also the file name of its profile asset under Assets/", Required = false)] string name = "PP Volume",
            [ToolParam("Global volume (affects whole scene) vs local box", Required = false)] bool global = true,
            [ToolParam("Priority (higher wins when volumes overlap)", Required = false)] float priority = 0f)
        {
#if KITWRIGHT_URP
            if (!AssetFunctions.IsValidAssetName(name) ||
                !AssetFunctions.TryNormalizeAssetPath($"Assets/{name}.asset", out var assetPath))
                return Response.Error("INVALID_NAME", new { name },
                    "name is the file name of the profile asset: no folders, no characters a file name cannot hold, and no leading or trailing dots or spaces.");

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            string path;
            try
            {
                path = AssetDatabase.GenerateUniqueAssetPath(assetPath);
                AssetDatabase.CreateAsset(profile, path);
            }
            catch (Exception ex)
            {
                UnityEngine.Object.DestroyImmediate(profile);
                return Response.Error("ASSET_CREATE_FAILED", new { name, message = ex.Message });
            }
            AssetDatabase.SaveAssetIfDirty(profile);
            Undo.RegisterCreatedObjectUndo(profile, $"Create {name}");

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
            var volume = Undo.AddComponent<Volume>(go);
            volume.isGlobal = global;
            volume.priority = priority;
            volume.sharedProfile = profile;

            string warning = null;
#if KITWRIGHT_PHYSICS
            if (!global)
                Undo.AddComponent<BoxCollider>(go).isTrigger = true;
#else
            if (!global)
                warning = "The physics module is not installed, so this local volume has no trigger collider and will not apply.";
#endif

            Selection.activeGameObject = go;
            return Response.Success($"Created {(global ? "global" : "local")} volume '{name}'.", new
            {
                instanceId = ObjectIdCodec.GetSerializableId(go),
                name = go.name,
                profilePath = path,
                global,
                priority,
                warning
            });
#else
            return UrpRequired();
#endif
        }

        [Description("List post-processing overrides on a Volume's profile (URP), with each override's active state and how many of its parameters are ticked (overrideState). Requires URP.")]
        [ReadOnlyTool]
        public static object GetVolumeInfo(
            [ToolParam("Volume GameObject name, path, or instance ID")] string target)
        {
#if KITWRIGHT_URP
            if (!TryResolveProfile(target, out var profile, out var err)) return err;
            var overrides = Present(profile).Select(c => new
            {
                type = c.GetType().Name,
                active = c.active,
                parameters = c.parameters.Count,
                overriddenParameters = c.parameters.Count(p => p.overrideState)
            }).ToArray();
            return Response.Success($"Volume '{target}' has {overrides.Length} override(s).", overrides);
#else
            return UrpRequired();
#endif
        }

        [Description("Add a post-processing override to a Volume profile (URP). override_type is the effect name: Bloom, Tonemapping, ColorAdjustments, DepthOfField, Vignette, ChromaticAberration, MotionBlur, FilmGrain, LensDistortion, etc. Requires URP.")]
        public static object AddVolumeOverride(
            [ToolParam("Volume GameObject name, path, or instance ID")] string target,
            [ToolParam("Effect type name (e.g. 'Bloom', 'Tonemapping')")] string override_type,
            [ToolParam("true (default) ticks every parameter of the new override at its default value, which forces those defaults over lower-priority volumes. " +
                       "false adds it un-ticked like the Inspector does: it changes nothing until set_volume_override_property ticks a parameter.", Required = false)] bool override_all_parameters = true)
        {
#if KITWRIGHT_URP
            if (!TryResolveProfile(target, out var profile, out var err)) return err;

            var type = ResolveOverrideType(override_type);
            if (type == null) return Response.Error("OVERRIDE_TYPE_NOT_FOUND", new { override_type });
            if (Present(profile).Any(c => c.GetType() == type))
                return Response.Error("OVERRIDE_ALREADY_ADDED", new { override_type = type.Name });

            Undo.RegisterCompleteObjectUndo(profile, "Add Volume Override");
            profile.components.RemoveAll(c => c == null);
            var comp = (VolumeComponent)profile.Add(type, overrides: override_all_parameters);
            comp.active = true;
            Undo.RegisterCreatedObjectUndo(comp, "Add Volume Override");

            // The override is a ScriptableObject of its own. Unless it is stored inside the profile
            // asset, no file owns it: the profile serializes a reference to an object that is
            // nowhere on disk, that reads back as null, and the override is gone after a reload -
            // having reported success. Unity's own profile editor stores it the same way.
            if (AssetDatabase.Contains(profile))
            {
                AssetDatabase.AddObjectToAsset(comp, profile);
                EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssetIfDirty(profile);
            }

            EditorUtility.SetDirty(profile);

            return Response.Success($"Added '{type.Name}' to '{target}'.",
                new { override_type = type.Name, allParametersOverridden = override_all_parameters });
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
            var comp = Present(profile).FirstOrDefault(c => c.GetType() == type);
            if (comp == null) return Response.Error("OVERRIDE_NOT_PRESENT", new { override_type = type.Name });

            // The list edit is recorded first and the destroy after it, so Undo restores the object
            // before it restores the list that points at it (the order Unity's own profile editor
            // uses). Destroying a sub-asset also takes it out of the .asset, so every add/remove
            // round no longer leaves another orphan inside the file.
            Undo.RegisterCompleteObjectUndo(profile, "Remove Volume Override");
            profile.components.RemoveAll(c => c == null);
            profile.Remove(type);
            Undo.DestroyObjectImmediate(comp);

            EditorUtility.SetDirty(profile);
            if (AssetDatabase.Contains(profile))
                AssetDatabase.SaveAssetIfDirty(profile);

            return Response.Success($"Removed '{type.Name}' from '{target}'.", new { override_type = type.Name });
#else
            return UrpRequired();
#endif
        }

        [Description("Set a single parameter on a Volume override and enable its overrideState (URP). Value is coerced to the parameter type: number, bool, enum name, or 'r,g,b,a' color. Reports the value the parameter stored, which differs from the request when it clamps. Requires URP.")]
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
            var comp = Present(profile).FirstOrDefault(c => c.GetType() == type);
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

            if (!IsCoercibleType(valueProp.PropertyType))
                return Response.Error("PARAM_TYPE_UNSUPPORTED", new { property, type = valueProp.PropertyType.Name },
                    "This tool sets number, bool, enum and color parameters only.");
            if (!TryCoerce(value, valueProp.PropertyType, out var coerced))
                return Response.Error("VALUE_COERCION_FAILED", new { value, expected = valueProp.PropertyType.Name });

            // comp, not profile: the profile owns only the list, and every field written below lives
            // on the override, which is a separate ScriptableObject. Recording the profile left Undo
            // with nothing of the value to restore - changing Bloom intensity and undoing kept it.
            Undo.RecordObject(comp, "Set Volume Override Property");
            valueProp.SetValue(param, coerced);
            param.overrideState = true;
            comp.active = true;
            EditorUtility.SetDirty(comp);
            EditorUtility.SetDirty(profile);
            if (AssetDatabase.Contains(profile))
                AssetDatabase.SaveAssetIfDirty(profile);

            // Clamped and min/max parameters apply their range in the setter, so what was asked for
            // is not necessarily what is stored.
            var stored = valueProp.GetValue(param);
            var storedText = Convert.ToString(stored, CultureInfo.InvariantCulture);
            var clamped = !Equals(coerced, stored);
            return Response.Success(
                clamped
                    ? $"Set {type.Name}.{property} = {storedText} (requested {value}; the parameter clamps it to its range)."
                    : $"Set {type.Name}.{property} = {storedText}.",
                new { override_type = type.Name, property, value = storedText, requested = value, clamped });
#else
            return UrpRequired();
#endif
        }

#if KITWRIGHT_URP
        // An override whose sub-asset was destroyed under the list (Undo, a deleted file) stays in
        // profile.components as null, and every c.GetType() over it threw. VolumeProfile.Add and
        // Remove throw on it too, so the two writers drop null entries before calling them.
        private static IEnumerable<VolumeComponent> Present(VolumeProfile profile) =>
            profile.components.Where(c => c != null);

        // A name shared by a URP type and an HDRP or project type resolves to the URP one, since this
        // tool is for URP; any other tie is refused rather than settled by assembly load order.
        internal static Type ResolveOverrideType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var wanted = name.Trim();

            var concrete = TypeCache.GetTypesDerivedFrom<VolumeComponent>().Where(t => !t.IsAbstract).ToList();
            var matches = concrete.Where(t => string.Equals(t.FullName, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                matches = concrete.Where(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matches.Count > 1)
            {
                var urp = matches.Where(t => t.Namespace == "UnityEngine.Rendering.Universal").ToList();
                if (urp.Count != 1)
                    throw new AmbiguousTypeException(name, matches.Select(t => t.FullName).OrderBy(n => n).ToArray());
                return urp[0];
            }
            return matches.Count == 1 ? matches[0] : null;
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

        private static bool IsCoercibleType(Type t) =>
            t == typeof(float) || t == typeof(int) || t == typeof(bool) || t == typeof(Color) || t.IsEnum;

        // Enum.Parse takes "99" and "A,B" and stores an undefined value; only a declared name is valid.
        internal static bool TryCoerce(string raw, Type t, out object result)
        {
            result = null;
            var ci = CultureInfo.InvariantCulture;
            try
            {
                if (t == typeof(float)) { result = float.Parse(raw, NumberStyles.Float, ci); return !float.IsNaN((float)result) && !float.IsInfinity((float)result); }
                if (t == typeof(int)) { result = int.Parse(raw, ci); return true; }
                if (t == typeof(bool)) { result = bool.Parse(raw); return true; }
                if (t == typeof(Color))
                {
                    if (string.IsNullOrWhiteSpace(raw) || !ValueConverter.TryParseColor(raw, Color.white, out var color, out _))
                        return false;
                    result = color;
                    return true;
                }
                if (t.IsEnum)
                {
                    var name = Array.Find(Enum.GetNames(t), n => string.Equals(n, raw?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (name == null) return false;
                    result = Enum.Parse(t, name);
                    return true;
                }
            }
            catch { return false; }
            return false;
        }
#endif
    }
}
