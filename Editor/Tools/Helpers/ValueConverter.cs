// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Globalization;
using UnityEngine;

namespace KitWright.Editor.Tools.Helpers
{
    internal static class ValueConverter
    {
        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F2} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F2} MB";
            if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F2} KB";
            return $"{bytes} B";
        }

        // The editor runs under the OS culture, not InvariantCulture, and float.Parse(string) allows
        // group separators: on a de-DE machine "0.5" reads as 5 with no exception to catch. Tool
        // arguments arrive as JSON text and are always invariant, so they must be read that way.
        public static bool TryParseFloat(string value, out float result) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        public static bool TryParseInt(string value, out int result) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

        // Lenient form, kept because unity-mcp-pro calls it; the built-in tools use TryParseColor.
        public static Color ParseColor(string value, Color fallback) =>
            TryParseColor(value, fallback, out var color, out _) ? color : fallback;

        // Reports what it could not read: this used to answer the fallback for "red" and throw a bare
        // FormatException for "a,b,c", so a material came out white under a reply that said it had the color.
        public static bool TryParseColor(string value, Color fallback, out Color color, out string error)
        {
            color = fallback;
            error = null;
            if (string.IsNullOrWhiteSpace(value))
                return true;

            var text = value.Trim();
            if (text.StartsWith("#") || text.IndexOf(',') < 0)
            {
                // '#RRGGBB[AA]', or a name Unity knows such as 'red'.
                if (ColorUtility.TryParseHtmlString(text, out color))
                    return true;

                color = fallback;
                error = $"'{value}' is not a #hex color, a color name, or 'r,g,b[,a]' numbers";
                return false;
            }

            var parts = text.Trim('(', ')', ' ').Split(',');
            if (parts.Length < 3 || parts.Length > 4 ||
                !TryFiniteFloat(parts[0], out var r) || !TryFiniteFloat(parts[1], out var g) || !TryFiniteFloat(parts[2], out var b))
            {
                error = $"'{value}' is not 'r,g,b' or 'r,g,b,a' numbers";
                return false;
            }

            var a = 1f;
            if (parts.Length == 4 && !TryFiniteFloat(parts[3], out a))
            {
                error = $"'{value}' has a non-numeric alpha";
                return false;
            }

            color = new Color(r, g, b, a);
            return true;
        }

        public static bool TryFiniteFloat(string text, out float value) =>
            TryParseFloat(text.Trim(), out value) && !float.IsNaN(value) && !float.IsInfinity(value);

        // Enum.TryParse also takes "A,B" flag combinations and bare numbers ("99") and calls them valid.
        public static bool TryParseEnum<T>(string value, out T result) where T : struct, Enum
        {
            result = default;
            var text = value?.Trim();
            var name = Array.Find(Enum.GetNames(typeof(T)), n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
            return name != null && Enum.TryParse(name, out result);
        }

        public static bool TryParseVector3(string value, out Vector3 result, out string error)
        {
            result = Vector3.zero;
            error = null;
            var parts = (value ?? string.Empty).Trim('(', ')', ' ').Split(',');
            if (parts.Length != 3)
            {
                error = $"expected 3 comma-separated numbers 'x,y,z', got {parts.Length}";
                return false;
            }
            if (TryFiniteFloat(parts[0], out var x) && TryFiniteFloat(parts[1], out var y) && TryFiniteFloat(parts[2], out var z))
            {
                result = new Vector3(x, y, z);
                return true;
            }
            error = $"'{value}' has a non-numeric or non-finite component";
            return false;
        }

        public static bool TryParseVector2(string value, out Vector2 result, out string error)
        {
            result = Vector2.zero;
            error = null;
            var parts = (value ?? string.Empty).Trim('(', ')', ' ').Split(',');
            if (parts.Length != 2)
            {
                error = $"expected 2 comma-separated numbers 'x,y', got {parts.Length}";
                return false;
            }
            if (TryFiniteFloat(parts[0], out var x) && TryFiniteFloat(parts[1], out var y))
            {
                result = new Vector2(x, y);
                return true;
            }
            error = $"'{value}' has a non-numeric or non-finite component";
            return false;
        }

        public static Vector2 ParseVector2(string value, Vector2 fallback)
        {
            return TryParseVector2(value, out var result, out _) ? result : fallback;
        }
    }
}
