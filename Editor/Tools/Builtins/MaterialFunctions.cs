// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Materials & Shaders")]
    internal static class MaterialFunctions
    {
        [Description("Inspect a Material: shader name, enabled keywords, render queue, and every shader property " +
                     "with its type and current value. 'path' is preferred as an asset path (e.g. 'Assets/Foo.mat'); " +
                     "if it does not resolve to a material asset it is treated as a scene GameObject identifier and the " +
                     "first Renderer.sharedMaterial is used.")]
        [ReadOnlyTool]
        public static object GetMaterialProperties(
            [ToolParam("Material asset path (e.g. 'Assets/Materials/Foo.mat'), or a scene GameObject id/name/path whose Renderer.sharedMaterial is inspected.")] string path)
        {
            var mat = ResolveMaterial(path, out var source);
            if (mat == null)
                return Response.Error("MATERIAL_NOT_FOUND",
                    new { path, hint = "Provide a valid .mat asset path, or a scene GameObject with a Renderer that has a sharedMaterial." });

            var shader = mat.shader;
            var properties = new List<object>();
            if (shader != null)
            {
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    var pName = shader.GetPropertyName(i);
                    var pType = shader.GetPropertyType(i);
                    object value;
                    switch (pType)
                    {
                        case ShaderPropertyType.Color:
                        {
                            var c = mat.GetColor(pName);
                            value = new { r = c.r, g = c.g, b = c.b, a = c.a };
                            break;
                        }
                        case ShaderPropertyType.Vector:
                        {
                            var v = mat.GetVector(pName);
                            value = new { x = v.x, y = v.y, z = v.z, w = v.w };
                            break;
                        }
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                            value = mat.GetFloat(pName);
                            break;
                        case ShaderPropertyType.Texture:
                        {
                            var tex = mat.GetTexture(pName);
                            value = tex != null ? AssetDatabase.GetAssetPath(tex) : null;
                            break;
                        }
                        default:
                            // ShaderPropertyType.Int (and any future kinds) — read as integer.
                            value = mat.GetInteger(pName);
                            break;
                    }
                    properties.Add(new { name = pName, type = pType.ToString(), value });
                }
            }

            return Response.Success($"Material '{mat.name}' ({properties.Count} shader properties).", new
            {
                material = mat.name,
                source,
                shader = shader != null ? shader.name : null,
                keywords = mat.shaderKeywords,
                renderQueue = mat.renderQueue,
                properties
            });
        }

        // -------- Helpers --------

        // Prefer an asset-path resolution; only fall back to a scene GameObject's Renderer.sharedMaterial.
        internal static Material ResolveMaterial(string target, out string source)
        {
            source = null;
            if (string.IsNullOrEmpty(target))
                return null;

            var asset = AssetDatabase.LoadAssetAtPath<Material>(target);
            if (asset != null)
            {
                source = "asset:" + target;
                return asset;
            }

            var go = ObjectsHelper.FindTarget(target);
            if (go != null)
            {
                var renderer = go.GetComponent<Renderer>();
                if (renderer != null && renderer.sharedMaterial != null)
                {
                    source = "renderer:" + ObjectsHelper.GetGameObjectPath(go);
                    return renderer.sharedMaterial;
                }
            }

            return null;
        }

        internal static bool IsEditableMaterial(string assetPath, bool persistent, bool notEditable = false, bool writablePackage = false)
        {
            if (!persistent)
                return true;
            if (notEditable || assetPath == null)
                return false;

            return assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                   (writablePackage && assetPath.StartsWith("Packages/", StringComparison.Ordinal));
        }

        internal static bool IsWritablePackageAsset(string assetPath)
        {
            if (assetPath == null || !assetPath.StartsWith("Packages/", StringComparison.Ordinal))
                return false;

            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            return info != null &&
                   (info.source == UnityEditor.PackageManager.PackageSource.Embedded ||
                    info.source == UnityEditor.PackageManager.PackageSource.Local);
        }
    }
}
