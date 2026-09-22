// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using System.Text;

namespace KitWright.Editor.Tools.Scripting
{
    /// <summary>
    /// Wraps every loop condition in a snippet with <see cref="LoopBudgetGuard"/>.Check before the
    /// snippet reaches the compiler, so a loop that never exits throws instead of hanging the editor.
    /// </summary>
    /// <remarks>
    /// Only the condition expression is rewritten -- never a loop body -- so a braced body and a
    /// single-statement body need no different treatment and the snippet's structure is untouched.
    /// foreach has no condition to wrap and is left alone; an endless iterator still hangs.
    /// The scanner tracks strings, chars and comments so a `while` inside a literal is not rewritten.
    /// </remarks>
    internal static class LoopGuardInjector
    {
        private const string CheckOpen = "global::KitWright.Editor.Tools.Scripting.LoopBudgetGuard.Check(";

        public static string Inject(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;

            var insertions = new List<(int Offset, string Text)>();

            for (var i = 0; i < source.Length;)
            {
                if (TrySkipTrivia(source, ref i)) continue;

                if (!IsIdentifierStart(source[i]) || (i > 0 && IsIdentifierPart(source[i - 1])))
                {
                    i++;
                    continue;
                }

                var wordStart = i;
                while (i < source.Length && IsIdentifierPart(source[i])) i++;
                var word = source.Substring(wordStart, i - wordStart);
                if (word != "while" && word != "for") continue;

                var cursor = i;
                if (!TrySkipToChar(source, ref cursor, '(')) continue;
                var open = cursor;
                if (!TryFindMatchingBracket(source, open, out var close)) continue;

                if (word == "while") WrapSpan(insertions, open + 1, close);
                else WrapForCondition(source, insertions, open, close);

                i = open + 1;
            }

            if (insertions.Count == 0) return source;

            insertions.Sort((a, b) => b.Offset.CompareTo(a.Offset));
            var builder = new StringBuilder(source);
            foreach (var (offset, text) in insertions) builder.Insert(offset, text);
            return builder.ToString();
        }

        private static void WrapForCondition(string source, List<(int, string)> insertions, int open, int close)
        {
            var semicolons = new List<int>();
            for (var i = open + 1; i < close && semicolons.Count < 2;)
            {
                if (TrySkipTrivia(source, ref i)) continue;
                var c = source[i];
                if (c == '(' || c == '[' || c == '{')
                {
                    if (!TryFindMatchingBracket(source, i, out var end)) return;
                    i = end + 1;
                    continue;
                }
                if (c == ';') semicolons.Add(i);
                i++;
            }

            if (semicolons.Count < 2) return;

            var start = semicolons[0] + 1;
            var end2 = semicolons[1];
            if (source.Substring(start, end2 - start).Trim().Length == 0)
                insertions.Add((start, CheckOpen + "true)"));
            else
                WrapSpan(insertions, start, end2);
        }

        private static void WrapSpan(List<(int, string)> insertions, int start, int end)
        {
            insertions.Add((start, CheckOpen));
            insertions.Add((end, ")"));
        }

        private static bool TrySkipToChar(string source, ref int i, char target)
        {
            while (i < source.Length)
            {
                if (TrySkipTrivia(source, ref i)) continue;
                if (char.IsWhiteSpace(source[i])) { i++; continue; }
                return source[i] == target;
            }
            return false;
        }

        private static bool TryFindMatchingBracket(string source, int open, out int close)
        {
            var closer = source[open] == '(' ? ')' : source[open] == '[' ? ']' : '}';
            var opener = source[open];
            var depth = 0;
            for (var i = open; i < source.Length;)
            {
                if (i > open && TrySkipTrivia(source, ref i)) continue;
                var c = source[i];
                if (c == opener) depth++;
                else if (c == closer)
                {
                    depth--;
                    if (depth == 0) { close = i; return true; }
                }
                i++;
            }
            close = -1;
            return false;
        }

        /// <summary>Consumes a comment or literal at <paramref name="i"/>; false when none starts there.</summary>
        private static bool TrySkipTrivia(string source, ref int i)
        {
            var c = source[i];

            if (c == '/' && i + 1 < source.Length)
            {
                if (source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n') i++;
                    return true;
                }
                if (source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                    i = Math.Min(i + 2, source.Length);
                    return true;
                }
                return false;
            }

            if (c == '\'')
            {
                i++;
                while (i < source.Length && source[i] != '\'')
                    i += source[i] == '\\' ? 2 : 1;
                i = Math.Min(i + 1, source.Length);
                return true;
            }

            // Eat any run of the $ and @ prefixes so every spelling -- "", $"", @"", $@"" and @$"" --
            // arrives at the same place. A verbatim string escapes its quote by doubling it; every
            // other form uses a backslash.
            var start = i;
            var verbatim = false;
            while (start < source.Length && (source[start] == '@' || source[start] == '$'))
            {
                verbatim |= source[start] == '@';
                start++;
            }

            if (start >= source.Length || source[start] != '"') return false;

            i = start + 1;
            while (i < source.Length)
            {
                if (verbatim)
                {
                    if (source[i] != '"') { i++; continue; }
                    if (i + 1 < source.Length && source[i + 1] == '"') { i += 2; continue; }
                    break;
                }
                if (source[i] == '\\') { i += 2; continue; }
                if (source[i] == '"') break;
                i++;
            }
            i = Math.Min(i + 1, source.Length);
            return true;
        }

        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

        private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
