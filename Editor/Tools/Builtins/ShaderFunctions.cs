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
    [ToolProvider("Materials & Shaders")]
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
                return Response.Error("SHADER_EXISTS", new { path = relativePath, hint = "use write_file (with expected_sha256 from read_file) to modify" });
            if (Shader.Find(name) != null)
                return Response.Error("SHADER_NAME_TAKEN", new { name });

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                AtomicFile.WriteAllText(fullPath, contents ?? DefaultShader(name));
                AssetDatabase.ImportAsset(relativePath);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return Response.Success($"Shader '{name}.shader' created.", new { path = relativePath });
            }
            catch (Exception e)
            {
                return Response.Error("CREATE_FAILED", new { path = relativePath, message = e.Message });
            }
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

            // Resolved before the disk is touched: the folder can carry "..". A rooted folder (a drive
            // letter survives the '/' trim) stays unprefixed so it is refused instead of glued under Assets/.
            var fileName = $"{name}.shader";
            var relativePath = Path.IsPathRooted(relativeDir)
                ? $"{relativeDir}/{fileName}"
                : $"Assets/{relativeDir}/{fileName}".Replace("//", "/");
            return (PathSafety.ResolveAssetPath(relativePath), relativePath);
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
