// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KitWright.Editor.Tools.Scripting
{
    /// <summary>
    /// Method-level edits to a C# file. patch_script matches text, so it breaks on the things that
    /// vary between a model's memory of a file and the file itself -- indentation, an attribute that
    /// moved, a comment. These edits address a member by name instead, and every span they touch is
    /// found by matching braces, so a replacement cannot half-overwrite the next member.
    ///
    /// Not a parser. It masks literals and comments, then works on the masked text, which is enough
    /// to find a type body and the members directly inside it. What it does not model: nested types
    /// with a member of the same name as the outer type's, and preprocessor branches -- an <c>#if</c>
    /// that hides a brace is invisible to it, so a file that uses one around a member boundary
    /// should be edited with patch_script.
    /// </summary>
    internal static class CSharpMemberEditor
    {
        internal const string OpReplace = "replace_method";
        internal const string OpInsert = "insert_method";
        internal const string OpDelete = "delete_method";

        internal sealed class MemberEdit
        {
            public string Op;
            public string ClassName;
            public string MethodName;
            public string Replacement;
            public string Position;
            public string AnchorMethod;
        }

        internal sealed class EditOutcome
        {
            public bool Success;
            public string Source;
            public string ErrorCode;
            public string Message;
            public string[] Candidates;
        }

        public static EditOutcome Apply(string source, IReadOnlyList<MemberEdit> edits)
        {
            var current = source;
            for (var i = 0; i < edits.Count; i++)
            {
                // Re-masked per edit rather than carrying offsets across one: an edit shifts every
                // position after it, and a stale offset silently cuts the wrong span.
                var outcome = ApplyOne(current, edits[i]);
                if (!outcome.Success)
                {
                    outcome.Message = $"Edit {i + 1} of {edits.Count} ({edits[i].Op}): {outcome.Message}";
                    return outcome;
                }

                current = outcome.Source;
            }

            return new EditOutcome { Success = true, Source = MatchLineEndings(source, current) };
        }

        // Edits are assembled with '\n', which would leave them as the only LF lines in a CRLF file.
        // A file that already mixes the two is left alone: rewriting it would touch lines no edit
        // asked for.
        private static string MatchLineEndings(string original, string edited)
        {
            if (original.IndexOf("\r\n", StringComparison.Ordinal) < 0) return edited;
            if (Regex.IsMatch(original, @"(?<!\r)\n")) return edited;

            return Regex.Replace(edited, @"(?<!\r)\n", "\r\n");
        }

        private static EditOutcome ApplyOne(string source, MemberEdit edit)
        {
            if (edit == null)
                return Fail("INVALID_EDIT", "The edit is null.");
            if (string.IsNullOrWhiteSpace(edit.ClassName))
                return Fail("INVALID_EDIT", "class_name is required.");

            var mask = Mask(source);
            if (!TryFindTypeBody(mask, edit.ClassName, out var bodyOpen, out var bodyClose))
                return Fail("TYPE_NOT_FOUND",
                    $"No class, struct, interface or record named '{edit.ClassName}' in this file.",
                    DeclaredTypeNames(mask));

            switch (edit.Op)
            {
                case OpReplace: return Replace(source, mask, bodyOpen, bodyClose, edit);
                case OpDelete: return Delete(source, mask, bodyOpen, bodyClose, edit);
                case OpInsert: return Insert(source, mask, bodyOpen, bodyClose, edit);
                default:
                    return Fail("UNKNOWN_OP",
                        $"Unknown op '{edit.Op}'. Expected {OpReplace}, {OpInsert} or {OpDelete}.");
            }
        }

        private static EditOutcome Replace(string source, string mask, int bodyOpen, int bodyClose, MemberEdit edit)
        {
            if (edit.Replacement == null)
                return Fail("INVALID_EDIT", "replacement is required for " + OpReplace + ".");

            var found = ResolveSingleMethod(source, mask, bodyOpen, bodyClose, edit.MethodName, out var failure);
            if (found == null) return failure;

            return Replaced(source, found.Start, found.End,
                Reindent(edit.Replacement, IndentOfLineAt(source, found.Start)));
        }

        private static EditOutcome Delete(string source, string mask, int bodyOpen, int bodyClose, MemberEdit edit)
        {
            var found = ResolveSingleMethod(source, mask, bodyOpen, bodyClose, edit.MethodName, out var failure);
            if (found == null) return failure;

            // Takes the member's own line break and the blank line after it, so the gap left behind
            // looks like every other gap between members.
            var trailing = Regex.Match(source.Substring(found.End), @"^\r?\n([ \t]*\r?\n)?").Length;
            return Replaced(source, LineStart(source, found.Start), found.End + trailing, string.Empty);
        }

        private static EditOutcome Insert(string source, string mask, int bodyOpen, int bodyClose, MemberEdit edit)
        {
            if (string.IsNullOrWhiteSpace(edit.Replacement))
                return Fail("INVALID_EDIT", "replacement is required for " + OpInsert + ".");

            var position = string.IsNullOrWhiteSpace(edit.Position) ? "end" : edit.Position.Trim().ToLowerInvariant();

            MethodSpan anchor = null;
            if (position == "after" || position == "before")
            {
                if (string.IsNullOrWhiteSpace(edit.AnchorMethod))
                    return Fail("INVALID_EDIT", $"anchor_method is required when position is '{position}'.");

                anchor = ResolveSingleMethod(source, mask, bodyOpen, bodyClose, edit.AnchorMethod, out var failure);
                if (anchor == null) return failure;
            }

            var body = Reindent(edit.Replacement, anchor != null
                ? IndentOfLineAt(source, anchor.Start)
                : MemberIndent(source, bodyOpen, bodyClose));

            // Each position lands on a different side of an existing line break, so the padding that
            // leaves exactly one blank line between members differs per case.
            switch (position)
            {
                case "after": return Inserted(source, anchor.End, "\n\n" + body);
                case "before": return Inserted(source, LineStart(source, anchor.Start), body + "\n\n");
                case "start": return Inserted(source, bodyOpen + 1, "\n" + body + "\n");
                case "end": return Inserted(source, LineStart(source, bodyClose), "\n" + body + "\n");
                default:
                    return Fail("INVALID_EDIT",
                        $"Unknown position '{edit.Position}'. Expected start, end, after or before.");
            }
        }

        private sealed class MethodSpan
        {
            public int Start;
            public int End;
            public string Signature;
        }

        private static MethodSpan ResolveSingleMethod(
            string source, string mask, int bodyOpen, int bodyClose, string methodName, out EditOutcome failure)
        {
            failure = null;

            if (string.IsNullOrWhiteSpace(methodName))
            {
                failure = Fail("INVALID_EDIT", "method_name is required.");
                return null;
            }

            var matches = FindMethods(source, mask, bodyOpen, bodyClose, methodName);
            if (matches.Count == 0)
            {
                failure = Fail("METHOD_NOT_FOUND",
                    $"No method named '{methodName}' directly inside that type.",
                    MethodNames(mask, bodyOpen, bodyClose));
                return null;
            }

            if (matches.Count > 1)
            {
                failure = Fail("AMBIGUOUS_METHOD",
                    $"'{methodName}' is overloaded {matches.Count} times here; this tool addresses a member by name, " +
                    "so it cannot tell them apart. Use patch_script for one overload.",
                    matches.Select(m => m.Signature).ToArray());
                return null;
            }

            return matches[0];
        }

        /// <summary>
        /// A copy of the source with every string, char literal and comment blanked to spaces, and
        /// line breaks kept. Index math on it maps back to the original one-to-one, so a brace found
        /// here is a real brace and never one inside a string.
        /// </summary>
        internal static string Mask(string source) => Mask(source, out _);

        /// <param name="unterminated">
        /// The construct still open at end of file, or null. Everything after it was blanked, so a
        /// caller that only counts braces would otherwise report the wrong problem.
        /// </param>
        internal static string Mask(string source, out string unterminated)
        {
            unterminated = null;
            if (string.IsNullOrEmpty(source)) return source ?? string.Empty;

            var masked = new char[source.Length];
            bool inString = false, inVerbatim = false, inChar = false;
            bool inLineComment = false, inBlockComment = false;
            var rawQuotes = 0;

            for (var i = 0; i < source.Length; i++)
            {
                char c = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';

                if (c == '\n')
                {
                    inLineComment = false;
                    // A non-verbatim string or a char literal cannot span lines; closing them here
                    // stops one stray quote or apostrophe from blanking the rest of the file.
                    if (inString && !inVerbatim) inString = false;
                    inChar = false;
                    masked[i] = c;
                    continue;
                }

                if (inLineComment) { masked[i] = ' '; continue; }

                if (inBlockComment)
                {
                    masked[i] = ' ';
                    if (c == '*' && next == '/' && i + 1 < source.Length) { inBlockComment = false; masked[++i] = ' '; }
                    continue;
                }

                if (rawQuotes > 0)
                {
                    masked[i] = ' ';
                    if (c != '"') continue;

                    var closing = QuoteRun(source, i);
                    for (var q = 1; q < closing; q++) masked[++i] = ' ';
                    if (closing >= rawQuotes) rawQuotes = 0;
                    continue;
                }

                if (inString)
                {
                    masked[i] = ' ';
                    if (inVerbatim)
                    {
                        if (c == '"' && next == '"') { masked[++i] = ' '; continue; }
                        if (c == '"') { inString = false; inVerbatim = false; }
                    }
                    else
                    {
                        if (c == '\\' && i + 1 < source.Length) { masked[++i] = ' '; continue; }
                        if (c == '"') inString = false;
                    }
                    continue;
                }

                if (inChar)
                {
                    masked[i] = ' ';
                    if (c == '\\' && i + 1 < source.Length) { masked[++i] = ' '; continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }

                if (c == '/' && next == '/') { inLineComment = true; masked[i] = ' '; continue; }
                if (c == '/' && next == '*') { inBlockComment = true; masked[i] = ' '; continue; }

                // A raw string opens on three or more quotes, after any run of '$' (a raw string takes
                // no '@' — there, four quotes are a verbatim string holding one), and closes on a quote
                // run at least as long. Rule borrowed from CoplayDev/unity-mcp
                // MCPForUnity/Editor/Tools/ManageScript.cs (CSharpLexer raw-string branch).
                if (c == '"' || c == '$')
                {
                    var afterPrefix = i;
                    while (afterPrefix < source.Length && source[afterPrefix] == '$')
                        afterPrefix++;

                    var opening = QuoteRun(source, afterPrefix);
                    if (opening >= 3)
                    {
                        rawQuotes = opening;
                        for (var j = i; j < afterPrefix + opening; j++) masked[j] = ' ';
                        i = afterPrefix + opening - 1;
                        continue;
                    }
                }

                if (c == '@' && next == '"') { inString = true; inVerbatim = true; masked[i] = ' '; masked[++i] = ' '; continue; }
                if (c == '$' && next == '"') { inString = true; masked[i] = ' '; masked[++i] = ' '; continue; }
                if (((c == '$' && next == '@') || (c == '@' && next == '$')) && i + 2 < source.Length && source[i + 2] == '"')
                {
                    inString = true; inVerbatim = true;
                    masked[i] = ' '; masked[++i] = ' '; masked[++i] = ' ';
                    continue;
                }
                if (c == '"') { inString = true; masked[i] = ' '; continue; }
                if (c == '\'') { inChar = true; masked[i] = ' '; continue; }

                masked[i] = c;
            }

            if (inBlockComment) unterminated = "block comment";
            else if (inString && inVerbatim) unterminated = "verbatim string";
            else if (rawQuotes > 0) unterminated = "raw string";
            else if (inChar) unterminated = "char literal";

            return new string(masked);
        }

        private static int QuoteRun(string source, int index)
        {
            var run = 0;
            while (index + run < source.Length && source[index + run] == '"') run++;
            return run;
        }

        private static bool TryFindTypeBody(string mask, string typeName, out int bodyOpen, out int bodyClose)
        {
            bodyOpen = bodyClose = -1;

            var declaration = new Regex($@"\b(class|struct|interface|record)\s+{Regex.Escape(typeName)}\b");
            foreach (Match match in declaration.Matches(mask))
            {
                var open = mask.IndexOf('{', match.Index + match.Length);
                if (open < 0) continue;

                // A ';' before the '{' means this was a forward reference, not the definition.
                if (mask.IndexOf(';', match.Index + match.Length, open - match.Index - match.Length) >= 0)
                    continue;

                if (!TryMatchPair(mask, open, '{', '}', out var close)) continue;

                bodyOpen = open;
                bodyClose = close;
                return true;
            }

            return false;
        }

        private static List<MethodSpan> FindMethods(string source, string mask, int bodyOpen, int bodyClose, string methodName)
        {
            var results = new List<MethodSpan>();
            var identifier = new Regex($@"\b{Regex.Escape(methodName)}\b");

            foreach (Match match in identifier.Matches(mask))
            {
                if (match.Index <= bodyOpen || match.Index >= bodyClose) continue;
                if (DepthBetween(mask, bodyOpen, match.Index) != 1) continue;

                var afterName = SkipWhitespace(mask, match.Index + match.Length);
                if (afterName < mask.Length && mask[afterName] == '<')
                {
                    if (!TryMatchPair(mask, afterName, '<', '>', out var closeAngle)) continue;
                    afterName = SkipWhitespace(mask, closeAngle + 1);
                }

                if (afterName >= mask.Length || mask[afterName] != '(') continue;
                if (!TryMatchPair(mask, afterName, '(', ')', out var closeParen)) continue;
                if (!LooksLikeDeclaration(mask, bodyOpen, match.Index)) continue;

                var end = FindMemberEnd(mask, closeParen + 1);
                if (end < 0) continue;

                results.Add(new MethodSpan
                {
                    Start = DeclarationStart(source, mask, bodyOpen, match.Index),
                    End = end,
                    Signature = Collapse(source.Substring(match.Index, closeParen + 1 - match.Index))
                });
            }

            return results;
        }

        /// <summary>
        /// Separates a declaration from a call. Everything between the end of the previous member and
        /// the name must be modifiers and a return type -- no '=' (a field holding a lambda that calls
        /// the same name) and no '(' (a call inside an initializer).
        /// </summary>
        private static bool LooksLikeDeclaration(string mask, int bodyOpen, int nameIndex)
        {
            for (var i = nameIndex - 1; i > bodyOpen; i--)
            {
                var c = mask[i];
                if (c == ';' || c == '{' || c == '}' || c == ']') return true;
                if (c == '=' || c == '(' || c == ',') return false;
            }

            return true;
        }

        // A member ends at its closing brace, at the ';' of an expression body, or at the ';' of a
        // declaration with no body at all (abstract, extern, interface).
        private static int FindMemberEnd(string mask, int afterArguments)
        {
            for (var i = afterArguments; i < mask.Length; i++)
            {
                var c = mask[i];
                if (char.IsWhiteSpace(c)) continue;

                if (c == '{')
                    return TryMatchPair(mask, i, '{', '}', out var close) ? close + 1 : -1;

                if (c == ';')
                    return i + 1;

                if (c == '=' && i + 1 < mask.Length && mask[i + 1] == '>')
                {
                    var terminator = mask.IndexOf(';', i);
                    return terminator < 0 ? -1 : terminator + 1;
                }
            }

            return -1;
        }

        // Attributes and the comment above a member are part of it: leaving them behind would
        // duplicate whatever the replacement carries.
        private static int DeclarationStart(string source, string mask, int bodyOpen, int nameIndex)
        {
            var start = LineStart(source, nameIndex);

            while (start > bodyOpen)
            {
                // start - 2, not start - 1: at start - 1 sits the line break that ends the previous
                // line, and LineStart of a line break is the line after it -- which is this one.
                var previousStart = LineStart(source, Math.Max(0, start - 2));
                if (previousStart >= start) break;

                var raw = source.Substring(previousStart, start - previousStart);
                if (raw.Trim().Length == 0) break;

                // The attribute test reads the masked line so a bracket inside a string does not
                // count; the comment test reads the raw line, because a comment masks to blank.
                var masked = mask.Substring(previousStart, start - previousStart).Trim();
                var isAttribute = masked.StartsWith("[", StringComparison.Ordinal) && masked.EndsWith("]", StringComparison.Ordinal);
                var isComment = raw.TrimStart().StartsWith("//", StringComparison.Ordinal);
                if (!isAttribute && !isComment) break;

                start = previousStart;
            }

            return start;
        }

        private static int DepthBetween(string mask, int from, int to)
        {
            var depth = 0;
            for (var i = from; i < to; i++)
            {
                if (mask[i] == '{') depth++;
                else if (mask[i] == '}') depth--;
            }

            return depth;
        }

        private static bool TryMatchPair(string mask, int open, char openChar, char closeChar, out int close)
        {
            close = -1;
            var depth = 0;

            for (var i = open; i < mask.Length; i++)
            {
                // A type parameter list never spans a statement; bailing stops a stray '<' from
                // running away through the rest of the file.
                if (openChar == '<' && (mask[i] == '{' || mask[i] == ';')) return false;

                if (mask[i] == openChar) depth++;
                else if (mask[i] == closeChar)
                {
                    depth--;
                    if (depth == 0) { close = i; return true; }
                }
            }

            return false;
        }

        private static string[] MethodNames(string mask, int bodyOpen, int bodyClose)
        {
            var names = new List<string>();

            foreach (Match match in new Regex(@"\b([A-Za-z_]\w*)\s*(<[^;{}]*>)?\s*\(").Matches(mask))
            {
                if (match.Index <= bodyOpen || match.Index >= bodyClose) continue;
                if (DepthBetween(mask, bodyOpen, match.Index) != 1) continue;
                if (!LooksLikeDeclaration(mask, bodyOpen, match.Index)) continue;

                names.Add(match.Groups[1].Value);
            }

            return names.Distinct().ToArray();
        }

        private static string[] DeclaredTypeNames(string mask) =>
            new Regex(@"\b(?:class|struct|interface|record)\s+([A-Za-z_]\w*)")
                .Matches(mask).Cast<Match>().Select(m => m.Groups[1].Value).ToArray();

        private static int LineStart(string source, int index)
        {
            var start = source.LastIndexOf('\n', Math.Max(0, Math.Min(index, source.Length - 1)));
            return start < 0 ? 0 : start + 1;
        }

        private static string IndentOfLineAt(string source, int index)
        {
            var start = LineStart(source, index);
            var end = start;
            while (end < source.Length && (source[end] == ' ' || source[end] == '\t')) end++;
            return source.Substring(start, end - start);
        }

        private static string MemberIndent(string source, int bodyOpen, int bodyClose)
        {
            for (var i = bodyOpen + 1; i < bodyClose; i++)
            {
                if (!char.IsWhiteSpace(source[i]))
                    return IndentOfLineAt(source, i);
            }

            return IndentOfLineAt(source, bodyOpen) + "    ";
        }

        /// <summary>
        /// Re-indents a block to sit at <paramref name="indent"/>, keeping its internal shape, so the
        /// caller writes a method the way it reads rather than the way it lines up at this nesting.
        /// </summary>
        internal static string Reindent(string text, string indent)
        {
            var lines = text.Replace("\r\n", "\n").Trim('\n').Split('\n');
            var common = lines.Where(l => l.Trim().Length > 0)
                .Select(l => l.Length - l.TrimStart(' ', '\t').Length)
                .DefaultIfEmpty(0)
                .Min();

            var builder = new StringBuilder();
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) builder.Append('\n');
                if (lines[i].Trim().Length == 0) continue;
                builder.Append(indent).Append(lines[i].Substring(common).TrimEnd());
            }

            return builder.ToString();
        }

        private static int SkipWhitespace(string mask, int index)
        {
            while (index < mask.Length && char.IsWhiteSpace(mask[index])) index++;
            return index;
        }

        private static string Collapse(string text) =>
            Regex.Replace(text.Replace('\n', ' ').Replace('\r', ' '), @"\s+", " ").Trim();

        private static EditOutcome Inserted(string source, int at, string text) => Replaced(source, at, at, text);

        private static EditOutcome Replaced(string source, int start, int end, string text) =>
            new EditOutcome { Success = true, Source = source.Substring(0, start) + text + source.Substring(end) };

        private static EditOutcome Fail(string code, string message, string[] candidates = null) =>
            new EditOutcome { Success = false, ErrorCode = code, Message = message, Candidates = candidates };
    }

    /// <summary>
    /// Structural check on C# source, for use before it reaches disk. Deliberately not a compiler:
    /// the authoritative answer is Unity's own, via request_recompile + get_compilation_errors, which
    /// builds the whole assembly with its real define symbols and its sibling files. Compiling one
    /// file on its own — which is what a per-file validator has to do — misreports every partial
    /// class and every <c>#if</c> branch whose symbol it does not know.
    /// </summary>
    internal static class CSharpSyntaxCheck
    {
        /// <returns>Null when the source is structurally sound, otherwise the first problem found.</returns>
        internal static string FindProblem(string source)
        {
            if (string.IsNullOrEmpty(source)) return "The file is empty.";

            var mask = CSharpMemberEditor.Mask(source, out var unterminated);
            if (unterminated != null)
                return $"Unterminated {unterminated}: it is still open at end of file.";

            var openers = new Stack<KeyValuePair<char, int>>();
            var line = 1;

            for (var i = 0; i < mask.Length; i++)
            {
                var c = mask[i];
                if (c == '\n') { line++; continue; }

                if (c == '{' || c == '(' || c == '[')
                {
                    openers.Push(new KeyValuePair<char, int>(c, line));
                    continue;
                }

                if (c != '}' && c != ')' && c != ']') continue;

                if (openers.Count == 0)
                    return $"Line {line}: '{c}' closes nothing.";

                var opener = openers.Pop().Key;
                var expected = opener == '{' ? '}' : opener == '(' ? ')' : ']';
                if (expected != c)
                    return $"Line {line}: '{c}' does not close the '{opener}' it was matched with; expected '{expected}'.";
            }

            if (openers.Count == 0) return null;

            var unclosed = openers.Pop();
            return $"Line {unclosed.Value}: '{unclosed.Key}' is never closed.";
        }
    }
}
