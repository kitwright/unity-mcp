// Copyright (C) KitWright. Licensed under MIT.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Prefs")]
    internal static class PrefsFunctions
    {
        [Description("Read an EditorPrefs value (editor-only, persists across sessions on this machine). Auto-detects type as string, int, float, or bool. " +
                     "EditorPrefs cannot tell a bool from an int, so a stored 0 or 1 is reported as bool when type is auto; pass type to force the reading.")]
        [ReadOnlyTool]
        public static object GetEditorPref(
            [ToolParam("Preference key")] string key,
            [ToolParam("Type hint: string, int, float, bool, or auto", Required = false)] string type = "auto")
        {
            if (string.IsNullOrEmpty(key))
                return Response.Error("INVALID_ARGUMENT", new { key });

            if (!IsKnownPrefType(type))
                return UnknownPrefType(type, "string, int, float, bool or auto");

            if (!EditorPrefs.HasKey(key))
                return Response.Error("KEY_NOT_FOUND", new { key });

            var resolved = ResolvePrefType(type);
            if (resolved == "auto")
                resolved = DetectEditorPrefType(key);

            object value = resolved switch
            {
                "int" => EditorPrefs.GetInt(key),
                "float" => EditorPrefs.GetFloat(key),
                "bool" => EditorPrefs.GetBool(key),
                _ => EditorPrefs.GetString(key)
            };

            return Response.Success($"EditorPrefs['{key}'] = {value}", new { key, type = resolved, value });
        }

        [Description("Write an EditorPrefs value (editor-only, persists across sessions on this machine).")]
        public static object SetEditorPref(
            [ToolParam("Preference key")] string key,
            [ToolParam("Value to store")] string value,
            [ToolParam("Type: string, int, float, or bool", Required = false)] string type = "string")
        {
            if (string.IsNullOrEmpty(key))
                return Response.Error("INVALID_ARGUMENT", new { key });

            if (!IsKnownPrefType(type))
                return UnknownPrefType(type, "string, int, float or bool");

            var resolved = ResolvePrefType(type);
            if (!TryWritePref(key, value, resolved, isEditor: true, out var error))
                return Response.Error("INVALID_VALUE", new { key, value, type = resolved, error });

            return Response.Success($"Set EditorPrefs['{key}'] = {value} ({resolved})", new { key, type = resolved, value });
        }

        [Description("Delete an EditorPrefs key. Returns whether the key existed.")]
        public static object DeleteEditorPref(
            [ToolParam("Preference key")] string key)
        {
            if (string.IsNullOrEmpty(key))
                return Response.Error("INVALID_ARGUMENT", new { key });

            bool existed = EditorPrefs.HasKey(key);
            EditorPrefs.DeleteKey(key);
            return Response.Success($"Deleted EditorPrefs['{key}'] (existed: {existed})", new { key, existed });
        }

        [Description("Read a PlayerPrefs value (shipped with the game, persists across runs). Auto-detects type as string, int, or float.")]
        [ReadOnlyTool]
        public static object GetPlayerPref(
            [ToolParam("Preference key")] string key,
            [ToolParam("Type hint: string, int, float, or auto", Required = false)] string type = "auto")
        {
            if (string.IsNullOrEmpty(key))
                return Response.Error("INVALID_ARGUMENT", new { key });

            if (!IsKnownPrefType(type))
                return UnknownPrefType(type, "string, int, float or auto");

            if (!PlayerPrefs.HasKey(key))
                return Response.Error("KEY_NOT_FOUND", new { key });

            var resolved = ResolvePrefType(type);
            // PlayerPrefs has no bool; treat bool hint as int.
            if (resolved == "bool") resolved = "int";
            if (resolved == "auto")
                resolved = DetectPlayerPrefType(key);

            object value = resolved switch
            {
                "int" => PlayerPrefs.GetInt(key),
                "float" => PlayerPrefs.GetFloat(key),
                _ => PlayerPrefs.GetString(key)
            };

            return Response.Success($"PlayerPrefs['{key}'] = {value}", new { key, type = resolved, value });
        }

        [Description("Write a PlayerPrefs value (shipped with the game, persists across runs). Calls PlayerPrefs.Save.")]
        public static object SetPlayerPref(
            [ToolParam("Preference key")] string key,
            [ToolParam("Value to store")] string value,
            [ToolParam("Type: string, int, float, or bool (stored as an int, 0 or 1)", Required = false)] string type = "string")
        {
            if (string.IsNullOrEmpty(key))
                return Response.Error("INVALID_ARGUMENT", new { key });

            if (!IsKnownPrefType(type))
                return UnknownPrefType(type, "string, int, float or bool");

            var resolved = ResolvePrefType(type);
            if (!TryWritePref(key, value, resolved, isEditor: false, out var error))
                return Response.Error("INVALID_VALUE", new { key, value, type = resolved, error });

            PlayerPrefs.Save();
            var stored = resolved == "bool" ? "int" : resolved;
            return Response.Success($"Set PlayerPrefs['{key}'] = {value} ({stored})", new { key, type = stored, value });
        }

        [Description("Delete a PlayerPrefs key. Returns whether the key existed. Calls PlayerPrefs.Save.")]
        public static object DeletePlayerPref(
            [ToolParam("Preference key")] string key)
        {
            if (string.IsNullOrEmpty(key))
                return Response.Error("INVALID_ARGUMENT", new { key });

            bool existed = PlayerPrefs.HasKey(key);
            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
            return Response.Success($"Deleted PlayerPrefs['{key}'] (existed: {existed})", new { key, existed });
        }

        [Description("Delete ALL PlayerPrefs keys for this project. Destructive and irreversible. Calls PlayerPrefs.Save.")]
        public static object DeleteAllPlayerPrefs()
        {
            PlayerPrefs.DeleteAll();
            PlayerPrefs.Save();
            return Response.Success("Deleted all PlayerPrefs keys.");
        }

        /// <summary>
        /// Which type a stored key actually holds. Neither prefs API will say, but both return the
        /// default you pass when the key is not of that type - so asking twice with two different
        /// defaults answers it: two identical results mean the value came from the store.
        ///
        /// Order matters. EditorPrefs stores a bool as an int, so bool is asked for first and only
        /// accepted when the int is 0 or 1; anything else is a plain int. Without this, "auto" read
        /// every key as a string, which is what the tool's own description promised it would not do.
        /// </summary>
        internal static string DetectEditorPrefType(string key)
        {
            if (EditorPrefs.GetInt(key, int.MinValue) == EditorPrefs.GetInt(key, int.MaxValue))
            {
                var stored = EditorPrefs.GetInt(key);
                return stored == 0 || stored == 1 ? "bool" : "int";
            }

            if (EditorPrefs.GetFloat(key, float.MinValue) == EditorPrefs.GetFloat(key, float.MaxValue))
                return "float";

            return "string";
        }

        internal static string DetectPlayerPrefType(string key)
        {
            // No bool in PlayerPrefs, so an int stays an int.
            if (PlayerPrefs.GetInt(key, int.MinValue) == PlayerPrefs.GetInt(key, int.MaxValue))
                return "int";

            if (PlayerPrefs.GetFloat(key, float.MinValue) == PlayerPrefs.GetFloat(key, float.MaxValue))
                return "float";

            return "string";
        }

        // ResolvePrefType reads an unknown name as "auto", which is what let a misspelt type write a string
        // or read the wrong kind with no sign of it. The tools check the name first.
        internal static bool IsKnownPrefType(string type)
        {
            return string.IsNullOrWhiteSpace(type) ||
                   ResolvePrefType(type) != "auto" ||
                   type.Trim().Equals("auto", System.StringComparison.OrdinalIgnoreCase);
        }

        private static object UnknownPrefType(string type, string expected)
        {
            return Response.Error("INVALID_PARAM", new { param = "type", provided = type, expected = $"one of: {expected}" });
        }

        internal static string ResolvePrefType(string type)
        {
            switch (type?.Trim().ToLowerInvariant())
            {
                case "int": return "int";
                case "float": return "float";
                case "bool": return "bool";
                case "string": return "string";
                default: return "auto";
            }
        }

        private static bool TryParseBool(string value, out bool result)
        {
            result = false;
            switch (value?.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": result = true; return true;
                case "false": case "0": case "no": return true;
                default: return false;
            }
        }

        internal static bool TryWritePref(string key, string value, string type, bool isEditor, out string error)
        {
            error = null;
            var effective = type == "auto" ? "string" : type;

            switch (effective)
            {
                case "int":
                    if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i)) { error = "not an int"; return false; }
                    if (isEditor) EditorPrefs.SetInt(key, i); else PlayerPrefs.SetInt(key, i);
                    return true;
                case "float":
                    if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                    { error = "not a float"; return false; }
                    if (float.IsNaN(f) || float.IsInfinity(f))
                    { error = "not a finite float (NaN and Infinity cannot be stored)"; return false; }
                    if (isEditor) EditorPrefs.SetFloat(key, f); else PlayerPrefs.SetFloat(key, f);
                    return true;
                case "bool":
                    if (!TryParseBool(value, out var b)) { error = "not a bool (true, false, 1, 0, yes or no)"; return false; }
                    if (isEditor) EditorPrefs.SetBool(key, b); else PlayerPrefs.SetInt(key, b ? 1 : 0);
                    return true;
                default:
                    if (isEditor) EditorPrefs.SetString(key, value); else PlayerPrefs.SetString(key, value);
                    return true;
            }
        }
    }
}
