// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.IO;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Asset")]
    internal static class AssetCreateFunctions
    {
        [Description("Create an asset of any UnityEngine.Object type that can be built without arguments: a ScriptableObject, a " +
                     "Material (shader optional), an AnimationClip, a PhysicsMaterial... Components and GameObjects are not assets and " +
                     "are refused. Refuses to replace an existing asset unless overwrite=true. create_scriptable_object also sets properties.")]
        public static object CreateAsset(
            [ToolParam("Asset path under Assets/, with the extension that suits the type (e.g. Assets/Data/Config.asset, Assets/Art/Wall.mat)")] string path,
            [ToolParam("Type name, short or fully qualified (e.g. 'Material', 'MyGame.GameConfig')")] string type,
            [ToolParam("Material only: shader name. Default: the active render pipeline's default shader, else Standard.", Required = false)] string shader = null,
            [ToolParam("Replace an asset that already exists at the path", Required = false)] bool overwrite = false)
        {
            if (!AssetFunctions.TryResolveUnderAssetsOrPackages(path, out var assetPath, out var absolutePath))
                return Response.Error("INVALID_PATH", new { path }, "path must be an asset file under Assets/ with no '..' segments.");

            var resolved = TypeResolver.Resolve(type);
            if (resolved == null)
                return TypeResolver.UnresolvedError(type, "TYPE_NOT_FOUND", nameof(type));

            if (File.Exists(absolutePath) && !overwrite)
                return Response.Error("ASSET_EXISTS", new { path = assetPath }, "An asset is already at this path. Pass overwrite=true to replace it.");

            UnityEngine.Object instance;
            if (typeof(ScriptableObject).IsAssignableFrom(resolved) && !resolved.IsAbstract)
            {
                instance = ScriptableObject.CreateInstance(resolved);
            }
            else if (resolved == typeof(Material))
            {
                var shaderAsset = string.IsNullOrWhiteSpace(shader) ? DefaultShader() : Shader.Find(shader.Trim());
                if (shaderAsset == null)
                    return Response.Error("SHADER_NOT_FOUND", new { shader }, "list_shaders shows the shader names.");

                instance = new Material(shaderAsset);
            }
            else if (typeof(Component).IsAssignableFrom(resolved) || typeof(GameObject).IsAssignableFrom(resolved) ||
                     resolved.IsAbstract || resolved.GetConstructor(Type.EmptyTypes) == null)
            {
                return Response.Error("NOT_CREATABLE", new { type = resolved.FullName },
                    "The type is a component, abstract, or has no parameterless constructor, so it cannot be created as a standalone asset.");
            }
            else
            {
                instance = (UnityEngine.Object)Activator.CreateInstance(resolved);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            AssetDatabase.CreateAsset(instance, assetPath);
            AssetDatabase.SaveAssets();

            return Response.Success($"Created {resolved.Name} at '{assetPath}'.", new
            {
                path = assetPath,
                type = resolved.FullName,
                guid = AssetDatabase.AssetPathToGUID(assetPath)
            });
        }

        private static Shader DefaultShader()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            return pipeline != null && pipeline.defaultShader != null ? pipeline.defaultShader : Shader.Find("Standard");
        }
    }
}
