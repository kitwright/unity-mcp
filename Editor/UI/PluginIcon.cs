// Copyright (C) KitWright. All rights reserved.

using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.MCP.Server
{
    internal static class PluginIcon
    {
        private static readonly System.Collections.Generic.Dictionary<string, Texture2D> _cache =
            new System.Collections.Generic.Dictionary<string, Texture2D>();

        public static Texture2D TabTexture => Load("kitwright_icon_tab");

        public static Texture2D LogoTextTexture => Load("kitwright_logo_text");

        private static Texture2D Load(string assetName)
        {
            if (_cache.TryGetValue(assetName, out var cached))
                return cached;

            // Direct path first: FindAssets scans the whole project (~60ms in big projects),
            // which is paid during CreateGUI after every domain reload.
            // Pro ahead of free: its presence is what the badged art announces, so when both
            // packages are installed the add-on's copy has to win.
            Texture2D found =
                AssetDatabase.LoadAssetAtPath<Texture2D>($"Packages/com.kitwright.unity.mcp.pro/Editor/Icons/{assetName}.png")
                ?? AssetDatabase.LoadAssetAtPath<Texture2D>($"Packages/com.kitwright.unity.mcp/Editor/Icons/{assetName}.png")
                ?? AssetDatabase.LoadAssetAtPath<Texture2D>($"Assets/KitWright/Editor/Icons/{assetName}.png");
            if (found != null)
            {
                _cache[assetName] = found;
                return found;
            }

            foreach (var guid in AssetDatabase.FindAssets($"{assetName} t:Texture2D"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) != assetName)
                    continue;
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex != null)
                {
                    found = tex;
                    break;
                }
            }

            _cache[assetName] = found;
            return found;
        }
    }
}
