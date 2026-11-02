// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace KitWright.Editor.MCP.Server
{
    /// <summary>
    /// Simple JSON serializer/deserializer for MCP server.
    /// Handles dictionaries, lists, and basic types.
    /// </summary>
    internal static class JsonCodec
    {
        public static string Serialize(object obj)
        {
            return Serialize(obj, 0);
        }

        private static string Serialize(object obj, int depth)
        {
            // Serialize recurses through nested containers; without this a deep or cyclic graph
            // overflows the stack, which is uncatchable and takes the editor down. Deserialize
            // already caps at the same depth.
            if (depth > MaxDepth)
                throw new Exception("JSON nesting too deep");

            if (obj == null)
                return "null";

            if (obj is string str)
                return "\"" + EscapeString(str) + "\"";

            if (obj is bool b)
                return b ? "true" : "false";

            if (obj is float || obj is double)
            {
                // JSON has no Infinity/NaN literal; Convert.ToString emits one, which is invalid JSON.
                // Match JSON.stringify and drop non-finite floats to null.
                var number = Convert.ToDouble(obj, CultureInfo.InvariantCulture);
                return double.IsNaN(number) || double.IsInfinity(number)
                    ? "null"
                    : Convert.ToString(obj, CultureInfo.InvariantCulture);
            }
            if (obj is int || obj is long || obj is uint || obj is ulong ||
                obj is byte || obj is sbyte || obj is short || obj is ushort || obj is decimal)
                return Convert.ToString(obj, CultureInfo.InvariantCulture);

            if (obj is IDictionary dict)
                return SerializeDictionary(dict, depth);

            if (obj is IList list)
                return SerializeList(list, depth);

            if (IsAnonymousType(obj.GetType()))
                return SerializeAnonymous(obj, depth);

            return "\"" + EscapeString(obj.ToString()) + "\"";
        }

        // Without this an anonymous type falls through to ToString() and reaches the wire as the
        // quoted blob "{ a = 1 }". Scalars Unity formats itself (Vector3, enums) keep that fallback.
        private static bool IsAnonymousType(Type type)
        {
            return type.IsDefined(typeof(CompilerGeneratedAttribute), false) &&
                   type.Name.IndexOf("AnonymousType", StringComparison.Ordinal) >= 0;
        }

        private static string SerializeAnonymous(object obj, int depth)
        {
            var sb = new StringBuilder();
            sb.Append("{");

            bool first = true;
            foreach (var property in obj.GetType().GetProperties())
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("\"");
                sb.Append(EscapeString(property.Name));
                sb.Append("\":");
                sb.Append(Serialize(property.GetValue(obj), depth + 1));
            }

            sb.Append("}");
            return sb.ToString();
        }

        private static string SerializeDictionary(IDictionary dict, int depth)
        {
            var sb = new StringBuilder();
            sb.Append("{");

            bool first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("\"");
                sb.Append(EscapeString(entry.Key.ToString()));
                sb.Append("\":");
                sb.Append(Serialize(entry.Value, depth + 1));
            }

            sb.Append("}");
            return sb.ToString();
        }

        private static string SerializeList(IList list, int depth)
        {
            var sb = new StringBuilder();
            sb.Append("[");

            bool first = true;
            foreach (var item in list)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append(Serialize(item, depth + 1));
            }

            sb.Append("]");
            return sb.ToString();
        }

        private const int MaxDepth = 64;

        public static object Deserialize(string json)
        {
            return Deserialize(json, 0);
        }

        // Config files are JSONC for several clients; the parser above stops at the first token that is
        // not a key, so a comment ahead of or inside a section hides everything after it.
        public static string StripComments(string json)
        {
            if (string.IsNullOrEmpty(json))
                return json;

            var sb = new StringBuilder(json.Length);
            var i = 0;
            while (i < json.Length)
            {
                var c = json[i];
                var next = i + 1 < json.Length ? json[i + 1] : '\0';

                if (c == '"')
                {
                    var end = FindStringEnd(json, i);
                    sb.Append(json, i, end - i + 1);
                    i = end + 1;
                }
                else if (c == '/' && next == '/')
                {
                    while (i < json.Length && json[i] != '\n')
                        i++;
                }
                else if (c == '/' && next == '*')
                {
                    var close = json.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? json.Length : close + 2;
                    sb.Append(' ');
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }

            return sb.ToString();
        }

        // The string values held directly by the object at `path` (keys from the root), located in the
        // text itself so a caller can edit one in place and leave comments and layout alone.
        internal static List<(string Key, int Start, int Length)> FindStringValues(string json, params string[] path)
        {
            var found = new List<(string Key, int Start, int Length)>();
            if (string.IsNullOrEmpty(json))
                return found;

            // One slot per open container: the key last read in an object, null in an array.
            var keys = new List<string>();
            var i = 0;
            while (true)
            {
                i = SkipTrivia(json, i);
                if (i >= json.Length)
                    return found;

                var c = json[i];
                if (c == '{' || c == '[')
                {
                    keys.Add(null);
                    i++;
                }
                else if (c == '}' || c == ']')
                {
                    if (keys.Count > 0)
                        keys.RemoveAt(keys.Count - 1);
                    i++;
                }
                else if (c == '"')
                {
                    var end = FindStringEnd(json, i);
                    var after = SkipTrivia(json, end + 1);
                    if (after < json.Length && json[after] == ':')
                    {
                        if (keys.Count > 0)
                            keys[keys.Count - 1] = json.Substring(i + 1, end - i - 1);
                    }
                    else if (keys.Count == path.Length + 1 && keys[keys.Count - 1] != null && IsUnder(keys, path))
                    {
                        found.Add((keys[keys.Count - 1], i, end - i + 1));
                    }

                    i = end + 1;
                }
                else
                {
                    i++;
                }
            }
        }

        private static bool IsUnder(List<string> keys, string[] path)
        {
            for (var i = 0; i < path.Length; i++)
            {
                if (keys[i] != path[i])
                    return false;
            }

            return true;
        }

        private static int SkipTrivia(string json, int i)
        {
            while (i < json.Length)
            {
                if (char.IsWhiteSpace(json[i]))
                {
                    i++;
                }
                else if (json[i] == '/' && i + 1 < json.Length && json[i + 1] == '/')
                {
                    while (i < json.Length && json[i] != '\n')
                        i++;
                }
                else if (json[i] == '/' && i + 1 < json.Length && json[i + 1] == '*')
                {
                    var close = json.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? json.Length : close + 2;
                }
                else
                {
                    break;
                }
            }

            return i;
        }

        private static object Deserialize(string json, int depth)
        {
            if (string.IsNullOrEmpty(json)) return null;
            json = json.Trim();

            if (json.StartsWith("{")) return DeserializeDictionary(json, depth);
            if (json.StartsWith("[")) return DeserializeList(json, depth);
            if (json.StartsWith("\""))
                return json.Length >= 2 && json.EndsWith("\"")
                    ? UnescapeString(json.Substring(1, json.Length - 2))
                    : UnescapeString(json.Substring(1));
            if (json == "null") return null;
            if (json == "true") return true;
            if (json == "false") return false;
            if (TryParseInteger(json, out var integer))
                return integer;
            if (double.TryParse(json, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                return number;
            return json;
        }

        // A double holds integers exactly only up to 2^53, so a 64-bit id read as one came back changed.
        // A literal with a fraction or exponent, or too large for 64 bits, stays a double.
        private static bool TryParseInteger(string text, out object value)
        {
            value = null;
            var start = text.Length > 0 && text[0] == '-' ? 1 : 0;
            if (start == text.Length)
                return false;

            for (var i = start; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                    return false;
            }

            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var signed))
                value = signed;
            else if (start == 0 && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var unsigned))
                value = unsigned;

            return value != null;
        }

        private static Dictionary<string, object> DeserializeDictionary(string json, int depth)
        {
            if (depth >= MaxDepth) throw new Exception("JSON nesting too deep");
            var result = new Dictionary<string, object>();
            json = json.Trim().Substring(1, json.Length - 2).Trim();
            if (string.IsNullOrEmpty(json)) return result;

            int pos = 0;
            while (pos < json.Length)
            {
                while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
                if (pos >= json.Length) break;
                if (json[pos] != '"') break;

                int keyStart = pos + 1;
                int keyEnd = FindStringEnd(json, pos);
                string key = UnescapeString(json.Substring(keyStart, keyEnd - keyStart));
                pos = keyEnd + 1;

                while (pos < json.Length && (char.IsWhiteSpace(json[pos]) || json[pos] == ':')) pos++;

                int valueEnd = FindValueEnd(json, pos);
                string valueStr = json.Substring(pos, valueEnd - pos).Trim();
                result[key] = Deserialize(valueStr, depth + 1);
                pos = valueEnd;

                while (pos < json.Length && (char.IsWhiteSpace(json[pos]) || json[pos] == ',')) pos++;
            }

            return result;
        }

        private static List<object> DeserializeList(string json, int depth)
        {
            if (depth >= MaxDepth) throw new Exception("JSON nesting too deep");
            var result = new List<object>();
            json = json.Trim().Substring(1, json.Length - 2).Trim();
            if (string.IsNullOrEmpty(json)) return result;

            int pos = 0;
            while (pos < json.Length)
            {
                while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
                if (pos >= json.Length) break;

                int valueEnd = FindValueEnd(json, pos);
                string valueStr = json.Substring(pos, valueEnd - pos).Trim();
                result.Add(Deserialize(valueStr, depth + 1));
                pos = valueEnd;

                while (pos < json.Length && (char.IsWhiteSpace(json[pos]) || json[pos] == ',')) pos++;
            }

            return result;
        }

        private static int FindStringEnd(string json, int start)
        {
            int pos = start + 1;
            while (pos < json.Length)
            {
                if (json[pos] == '"')
                {
                    int backslashes = 0;
                    int b = pos - 1;
                    while (b > start && json[b] == '\\') { backslashes++; b--; }
                    if (backslashes % 2 == 0)
                        return pos;
                }
                pos++;
            }
            return json.Length - 1;
        }

        private static int FindValueEnd(string json, int start)
        {
            if (start >= json.Length) return json.Length;
            char c = json[start];

            if (c == '"') return FindStringEnd(json, start) + 1;

            if (c == '{')
            {
                int depth = 1; int pos = start + 1;
                while (pos < json.Length && depth > 0)
                {
                    // Skip string bodies so a brace inside a value ("a}b") does not miscount depth.
                    if (json[pos] == '"') { pos = FindStringEnd(json, pos) + 1; continue; }
                    if (json[pos] == '{') depth++;
                    else if (json[pos] == '}') depth--;
                    pos++;
                }
                return pos;
            }

            if (c == '[')
            {
                int depth = 1; int pos = start + 1;
                while (pos < json.Length && depth > 0)
                {
                    if (json[pos] == '"') { pos = FindStringEnd(json, pos) + 1; continue; }
                    if (json[pos] == '[') depth++;
                    else if (json[pos] == ']') depth--;
                    pos++;
                }
                return pos;
            }

            int end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']') end++;
            return end;
        }

        private static string EscapeString(string str)
        {
            if (str == null) return "";
            var sb = new StringBuilder();
            foreach (char c in str)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string UnescapeString(string str)
        {
            if (str == null) return "";
            var sb = new StringBuilder();
            int i = 0;
            while (i < str.Length)
            {
                if (str[i] == '\\' && i + 1 < str.Length)
                {
                    switch (str[i + 1])
                    {
                        case '"': sb.Append('"'); i += 2; break;
                        case '\\': sb.Append('\\'); i += 2; break;
                        case '/': sb.Append('/'); i += 2; break;
                        case 'b': sb.Append('\b'); i += 2; break;
                        case 'f': sb.Append('\f'); i += 2; break;
                        case 'n': sb.Append('\n'); i += 2; break;
                        case 'r': sb.Append('\r'); i += 2; break;
                        case 't': sb.Append('\t'); i += 2; break;
                        case 'u':
                            if (i + 5 < str.Length)
                            {
                                var hex = str.Substring(i + 2, 4);
                                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                                { sb.Append((char)cp); i += 6; }
                                else { sb.Append(str[i]); i++; }
                            }
                            else { sb.Append(str[i]); i++; }
                            break;
                        default: sb.Append(str[i]); i++; break;
                    }
                }
                else { sb.Append(str[i]); i++; }
            }
            return sb.ToString();
        }
    }
}
