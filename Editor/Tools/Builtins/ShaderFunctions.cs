// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Shader")]
    internal static class ShaderFunctions
    {
        private static readonly Regex NameRegex = new Regex(@"^[a-zA-Z_][a-zA-Z0-9_]*$");

        [Description("Create a new .shader source file in the project. If contents is omitted a default unlit CG template is written. Fails if a shader with the same name or file already exists.")]
        public static object CreateShader(
            [ToolParam("Shader file name without extension (letters/digits/underscore, cannot start with a digit)")] string name,
            [ToolParam("Folder under Assets/ to place the file (default 'Shaders')", Required = false)] string path = "Shaders",
            [ToolParam("Shader source. If omitted, a default unlit template is generated.", Required = false)] string contents = null)
        {
            if (!NameRegex.IsMatch(name))
                return Response.Error("INVALID_NAME", new { name, hint = "letters, digits, underscore; cannot start with a digit" });

            var (fullPath, relativePath) = ResolvePaths(name, path);

            if (File.Exists(fullPath))
                return Response.Error("SHADER_EXISTS", new { path = relativePath, hint = "use update_shader to modify" });
            if (Shader.Find(name) != null)
                return Response.Error("SHADER_NAME_TAKEN", new { name });

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(fullPath, contents ?? DefaultShader(name), new System.Text.UTF8Encoding(false));
                AssetDatabase.ImportAsset(relativePath);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return Response.Success($"Shader '{name}.shader' created.", new { path = relativePath });
            }
            catch (Exception e)
            {
                return Response.Error("CREATE_FAILED", new { path = relativePath, message = e.Message });
            }
        }

        [Description("Read the source of a .shader file in the project.")]
        [ReadOnlyTool]
        public static object ReadShader(
            [ToolParam("Shader file name without extension")] string name,
            [ToolParam("Folder under Assets/ the file lives in (default 'Shaders')", Required = false)] string path = "Shaders")
        {
            var (fullPath, relativePath) = ResolvePaths(name, path);
            if (!File.Exists(fullPath))
                return Response.Error("SHADER_NOT_FOUND", new { path = relativePath });

            try
            {
                return Response.Success($"Shader '{name}.shader' read.", new { path = relativePath, contents = File.ReadAllText(fullPath) });
            }
            catch (Exception e)
            {
                return Response.Error("READ_FAILED", new { path = relativePath, message = e.Message });
            }
        }

        [Description("Overwrite the source of an existing .shader file. Fails if the file does not exist.")]
        public static object UpdateShader(
            [ToolParam("Shader file name without extension")] string name,
            [ToolParam("New full shader source")] string contents,
            [ToolParam("Folder under Assets/ the file lives in (default 'Shaders')", Required = false)] string path = "Shaders")
        {
            var (fullPath, relativePath) = ResolvePaths(name, path);
            if (!File.Exists(fullPath))
                return Response.Error("SHADER_NOT_FOUND", new { path = relativePath, hint = "use create_shader to add a new shader" });
            if (string.IsNullOrEmpty(contents))
                return Response.Error("CONTENTS_REQUIRED", new { message = "contents is required for update" });

            try
            {
                File.WriteAllText(fullPath, contents, new System.Text.UTF8Encoding(false));
                AssetDatabase.ImportAsset(relativePath);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return Response.Success($"Shader '{name}.shader' updated.", new { path = relativePath });
            }
            catch (Exception e)
            {
                return Response.Error("UPDATE_FAILED", new { path = relativePath, message = e.Message });
            }
        }

        [Description("Delete a .shader file by moving it to the OS trash (Recycle Bin). Not an Editor undo step, but the file can be restored by hand from the trash.")]
        public static object DeleteShader(
            [ToolParam("Shader file name without extension. The file is moved to the OS trash, so it can be recovered by hand.")] string name,
            [ToolParam("Folder under Assets/ the file lives in (default 'Shaders')", Required = false)] string path = "Shaders")
        {
            var (fullPath, relativePath) = ResolvePaths(name, path);
            if (!File.Exists(fullPath))
                return Response.Error("SHADER_NOT_FOUND", new { path = relativePath });

            try
            {
                if (!AssetDatabase.MoveAssetToTrash(relativePath))
                {
                    // A .shader written outside the Editor has no AssetDatabase entry to move until it is imported.
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    if (!AssetDatabase.MoveAssetToTrash(relativePath))
                        return Response.Error("DELETE_FAILED", new
                        {
                            path = relativePath,
                            message = "the file exists on disk but is not in the AssetDatabase, so it cannot be moved to the trash",
                            hint = "import it first (AssetDatabase.ImportAsset / Assets > Refresh), then retry delete_shader"
                        });
                }
                return Response.Success($"Shader '{name}.shader' moved to OS trash.", new { path = relativePath });
            }
            catch (Exception e)
            {
                return Response.Error("DELETE_FAILED", new { path = relativePath, message = e.Message });
            }
        }

        [Description("List shader files (.shader) in the project. Optionally filter by a name substring.")]
        [ReadOnlyTool]
        public static object ListShaders(
            [ToolParam("Case-insensitive substring to filter shader paths by", Required = false)] string filter = null,
            [ToolParam("Maximum number of results", Required = false)] int count = 100)
        {
            count = Mathf.Clamp(count, 1, 500);
            var guids = AssetDatabase.FindAssets("t:Shader");
            var matches = guids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                .Where(p => string.IsNullOrEmpty(filter) || p.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(p => p)
                .ToList();
            var paths = matches.Take(count).ToList();

            var message = paths.Count < matches.Count
                ? $"Found {matches.Count} shader file(s) (showing {paths.Count})."
                : $"Found {matches.Count} shader file(s).";
            return Response.Success(message, new { count = paths.Count, total = matches.Count, shaders = paths });
        }

        [Description("Get metadata for a shader loaded in the project (by shader name as declared in the Shader \"...\" line): supported flag, render queue, LOD, and its exposed properties with types.")]
        [ReadOnlyTool]
        public static object GetShaderInfo(
            [ToolParam("Shader name as declared inside the shader (e.g. 'Custom/MyShader', 'Standard')")] string shader_name)
        {
            var shader = Shader.Find(shader_name);
            if (shader == null)
                return Response.Error("SHADER_NOT_FOUND", new { shader_name, hint = "use the name from the Shader \"...\" declaration, not the file name" });

            int propCount = shader.GetPropertyCount();
            var props = Enumerable.Range(0, propCount).Select(i => new
            {
                name = shader.GetPropertyName(i),
                type = shader.GetPropertyType(i).ToString(),
                description = shader.GetPropertyDescription(i)
            }).ToList();

            return Response.Success($"Shader '{shader_name}' info.", new
            {
                name = shader.name,
                isSupported = shader.isSupported,
                renderQueue = shader.renderQueue,
                maximumLOD = shader.maximumLOD,
                assetPath = AssetDatabase.GetAssetPath(shader),
                propertyCount = propCount,
                properties = props
            });
        }

        internal static (string fullPath, string relativePath) ResolvePaths(string name, string path)
        {
            var relativeDir = (path ?? "Shaders").Replace('\\', '/').Trim('/');
            if (string.Equals(relativeDir, "Assets", StringComparison.OrdinalIgnoreCase))
                relativeDir = "";
            else if (relativeDir.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                relativeDir = relativeDir.Substring("Assets/".Length).TrimStart('/');
            if (string.IsNullOrEmpty(relativeDir))
                relativeDir = "Shaders";

            var fileName = $"{name}.shader";
            var fullPath = Path.Combine(Application.dataPath, relativeDir, fileName);
            var relativePath = $"Assets/{relativeDir}/{fileName}".Replace("//", "/");
            return (fullPath, relativePath);
        }

        private static string DefaultShader(string name)
        {
            return @"Shader """ + name + @"""
{
    Properties
    {
        _MainTex (""Texture"", 2D) = ""white"" {}
    }
    SubShader
    {
        Tags { ""RenderType""=""Opaque"" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target { return tex2D(_MainTex, i.uv); }
            ENDCG
        }
    }
}";
        }
    }
}
