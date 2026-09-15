// Copyright (C) KitWright. Licensed under MIT.

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

        public static Color ParseColor(string value, Color fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            value = value.Trim();
            if (value.StartsWith("#") && ColorUtility.TryParseHtmlString(value, out var c))
                return c;

            var p = value.Trim('(', ')', ' ').Split(',');
            if (p.Length >= 3)
            {
                float r = float.Parse(p[0].Trim(), CultureInfo.InvariantCulture);
                float g = float.Parse(p[1].Trim(), CultureInfo.InvariantCulture);
                float b = float.Parse(p[2].Trim(), CultureInfo.InvariantCulture);
                float a = p.Length >= 4 ? float.Parse(p[3].Trim(), CultureInfo.InvariantCulture) : 1f;
                return new Color(r, g, b, a);
            }
            return fallback;
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
            if (float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                result = new Vector3(x, y, z);
                return true;
            }
            error = $"'{value}' has a non-numeric component";
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
            if (float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                result = new Vector2(x, y);
                return true;
            }
            error = $"'{value}' has a non-numeric component";
            return false;
        }

        public static Vector2 ParseVector2(string value, Vector2 fallback)
        {
            return TryParseVector2(value, out var result, out _) ? result : fallback;
        }
    }
}
