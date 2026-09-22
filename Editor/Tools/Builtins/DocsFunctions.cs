// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using KitWright.Editor.Tools.Helpers;
using UnityEngine;
using UnityEngine.Networking;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Docs")]
    internal static class DocsFunctions
    {
        private const int MaxPagesPerCall = 10;
        private const int MinChars = 500;
        private const int MaxChars = 20000;

        // Pages are immutable per Unity version, so one fetch per domain reload is enough.
        private static readonly Dictionary<string, DocPage> PageCache =
            new Dictionary<string, DocPage>(StringComparer.OrdinalIgnoreCase);

        private sealed class DocPage
        {
            public string Text;
            public string[] Examples;
        }

        private static readonly Regex CodeExampleRegex =
            new Regex(@"<pre[^>]*\bclass=""[^""]*codeExample[^""]*""[^>]*>(.*?)</pre>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex ScriptOrStyleRegex =
            new Regex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex BlockBreakRegex =
            new Regex(@"</(p|div|li|tr|h[1-6]|pre|table|ul|ol)\s*>|<br\s*/?>", RegexOptions.IgnoreCase);
        private static readonly Regex TagRegex = new Regex("<[^>]+>", RegexOptions.Singleline);
        private static readonly Regex HorizontalSpaceRegex = new Regex(@"[ \t]+");
        private static readonly Regex BlankLinesRegex = new Regex(@"\n{3,}");

        [Description("Get the Unity documentation URL for a scripting API type or member. Returns the ScriptReference link for the current Unity version (e.g. 'Rigidbody', 'GameObject.SetActive', 'AI.NavMeshAgent'). " +
                     "Builds the link offline and fetches nothing — use fetch_docs instead when you want to read the page.")]
        [ReadOnlyTool]
        public static string GetScriptReferenceUrl(
            [ToolParam("Type or member, e.g. 'Rigidbody' or 'Transform.Rotate'. Namespace dots are stripped except the member separator.")] string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return ToolResultFormatter.Error("EMPTY_SYMBOL");

            return $"ScriptReference URL for '{symbol}' (Unity {Application.unityVersion}):\n<{ScriptReferenceUrl(symbol)}>";
        }

        [Description("Get a Unity Manual search URL for a topic (e.g. 'lightmapping', 'addressables'). Returns a docs.unity3d.com Manual search link for the current Unity version. " +
                     "Use this when the Manual slug is unknown; fetch_docs reads a page once you have the slug.")]
        [ReadOnlyTool]
        public static string SearchManual(
            [ToolParam("Topic or keyword to search the Unity Manual for")] string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return ToolResultFormatter.Error("EMPTY_QUERY");

            return $"Manual search URL for '{query}' (Unity {Application.unityVersion}):\n<{ManualSearchUrl(query)}>";
        }

        [Description("Fetch Unity documentation pages and return them as plain text, so usage notes and code examples arrive without a separate web fetch. " +
                     "Accepts a comma-separated list (up to 10) for batch lookup: a bare name hits the ScriptReference " +
                     "('Physics.Raycast', 'AI.NavMeshAgent'), a 'manual:' prefix hits the Unity Manual by slug ('manual:execution-order'). " +
                     "Docs are for the editor's own Unity version. Each page comes back as a markdown section: its URL, the page prose, " +
                     "then every code example repeated in its own fenced block, so the runnable code is still there when max_chars truncates the prose. " +
                     "Pair with reflect_api: reflect_api confirms a member exists on this version, fetch_docs explains how to use it. " +
                     "A page that does not exist is reported as NOT FOUND with a search URL to try instead.")]
        // Returns plain text, not the usual JSON envelope: the payload is mostly prose and code, so
        // JSON would escape every newline and quote in it, and a client that linkifies the raw result
        // swallows the closing quote and the next field into the URL. On its own line, a URL ends
        // where the line ends.
        [ReadOnlyTool]
        public static async Task<string> FetchDocs(
            [ToolParam("Comma-separated pages, e.g. 'Physics.Raycast,Transform.Rotate' or 'manual:execution-order'")] string pages,
            [ToolParam("Maximum characters of text kept per page (default 4000, clamped to 500-20000)", Required = false)] int max_chars = 4000)
        {
            if (string.IsNullOrWhiteSpace(pages)) return ToolResultFormatter.Error("EMPTY_PAGES");

            var requested = pages.Split(',')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (requested.Count == 0) return ToolResultFormatter.Error("EMPTY_PAGES");

            var dropped = Math.Max(0, requested.Count - MaxPagesPerCall);
            if (dropped > 0) requested = requested.Take(MaxPagesPerCall).ToList();

            var limit = Mathf.Clamp(max_chars, MinChars, MaxChars);
            var report = new StringBuilder();
            var found = 0;

            foreach (var entry in requested)
            {
                bool isManual = entry.StartsWith("manual:", StringComparison.OrdinalIgnoreCase);
                var name = isManual ? entry.Substring("manual:".Length).Trim() : entry;

                if (name.Length == 0)
                {
                    report.Append($"\n## {entry} — NOT FOUND\nEmpty page name.\n");
                    continue;
                }

                var url = isManual ? ManualUrl(name) : ScriptReferenceUrl(name);
                var page = await GetPageAsync(url);

                if (page == null)
                {
                    report.Append($"\n## {entry} — NOT FOUND\n<{url}>\n\nSearch instead:\n")
                        .Append($"<{(isManual ? ManualSearchUrl(name) : ScriptReferenceSearchUrl(name))}>")
                        .Append(isManual
                            ? "\n\nNo Manual page at that slug. Try the search URL, or a different slug.\n"
                            : "\n\nNo ScriptReference page at that symbol. Check the name with reflect_api, or try the search URL.\n");
                    continue;
                }

                found++;
                var truncated = page.Text.Length > limit;
                // Angle-bracketed: the result reaches the client inside a JSON envelope, and a client
                // that linkifies the raw text runs a bare URL straight into the escaped characters
                // that follow it. '>' is not a URL character, so the link ends where it should.
                report.Append($"\n## {entry}\n<{url}>\n\n")
                    .Append(truncated ? page.Text.Substring(0, limit) : page.Text)
                    .Append(truncated ? $"\n[truncated to {limit} chars; raise max_chars for the rest]\n" : "\n");

                // Repeated after the prose so a caller after a working snippet does not have to find
                // the code inside it -- and so the examples survive truncation, which cuts prose first.
                for (var i = 0; i < page.Examples.Length; i++)
                    report.Append($"\n### Example {i + 1}\n```csharp\n{page.Examples[i]}\n```\n");
            }

            var header = $"Fetched {found}/{requested.Count} page(s) for Unity {Application.unityVersion}.";
            if (dropped > 0)
                header += $" {dropped} page(s) past the {MaxPagesPerCall}-per-call limit were dropped.";

            return header + "\n" + report;
        }

        private static async Task<DocPage> GetPageAsync(string url)
        {
            lock (PageCache)
            {
                if (PageCache.TryGetValue(url, out var cached))
                    return cached;
            }

            string html = null;
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 20;
                request.SetRequestHeader("User-Agent", "KitWright-Unity-MCP");

                var operation = request.SendWebRequest();
                while (!operation.isDone)
                    await Task.Delay(50);

                if (request.result == UnityWebRequest.Result.Success)
                    html = request.downloadHandler.text;
            }

            if (html == null)
                return null;

            var page = new DocPage { Text = HtmlToText(html), Examples = ExtractExamples(html) };
            lock (PageCache)
            {
                PageCache[url] = page;
            }
            return page;
        }

        // ScriptReference wraps every runnable snippet in <pre class="codeExampleCS">.
        internal static string[] ExtractExamples(string html)
        {
            if (string.IsNullOrEmpty(html)) return new string[0];

            return CodeExampleRegex.Matches(html)
                .Cast<Match>()
                .Select(m => WebUtility.HtmlDecode(TagRegex.Replace(m.Groups[1].Value, string.Empty))
                    .Replace("\r\n", "\n").Trim())
                .Where(snippet => snippet.Length > 0)
                .ToArray();
        }

        private static string ScriptReferenceUrl(string symbol)
        {
            var page = symbol.Trim().Replace("UnityEngine.", "").Replace("UnityEditor.", "");
            return $"https://docs.unity3d.com/{DocVersion()}/Documentation/ScriptReference/{page}.html";
        }

        private static string ManualUrl(string slug)
        {
            var page = slug.Trim();
            if (page.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                page = page.Substring(0, page.Length - 5);
            return $"https://docs.unity3d.com/{DocVersion()}/Documentation/Manual/{page}.html";
        }

        private static string ManualSearchUrl(string query)
        {
            var encoded = UnityWebRequest.EscapeURL(query.Trim());
            return $"https://docs.unity3d.com/{DocVersion()}/Documentation/Manual/30_search.html?q={encoded}";
        }

        private static string ScriptReferenceSearchUrl(string query)
        {
            var encoded = UnityWebRequest.EscapeURL(query.Trim());
            return $"https://docs.unity3d.com/{DocVersion()}/Documentation/ScriptReference/30_search.html?q={encoded}";
        }

        // Docs pages put the nav ahead of the article and the legal boilerplate after it, so the
        // first <h1> and the copyright line bound the part worth sending to a model.
        internal static string HtmlToText(string html)
        {
            if (string.IsNullOrEmpty(html)) return string.Empty;

            var body = ScriptOrStyleRegex.Replace(html, " ");

            var headingIndex = body.IndexOf("<h1", StringComparison.OrdinalIgnoreCase);
            if (headingIndex > 0)
                body = body.Substring(headingIndex);

            // ScriptReference pages wedge the "Leave feedback" suggestion form between the heading and
            // the first subsection. Both anchors sit at the same offset inside a <div ...> tag, so
            // splicing them together leaves valid markup. Manual pages carry no such form.
            var feedbackIndex = body.IndexOf("class=\"scrollToFeedback", StringComparison.OrdinalIgnoreCase);
            if (feedbackIndex > 0)
            {
                var resumeIndex = body.IndexOf("class=\"subsection", feedbackIndex, StringComparison.OrdinalIgnoreCase);
                if (resumeIndex > feedbackIndex)
                    body = body.Remove(feedbackIndex, resumeIndex - feedbackIndex);
            }

            // Cut at the '<' that opens the footer element, not at the class attribute inside it:
            // stopping mid-tag leaves a "<div " fragment that the tag stripper cannot match.
            var footerIndex = body.IndexOf("class=\"footer", StringComparison.OrdinalIgnoreCase);
            if (footerIndex > 0)
            {
                var tagStart = body.LastIndexOf('<', footerIndex);
                body = body.Substring(0, tagStart > 0 ? tagStart : footerIndex);
            }

            body = BlockBreakRegex.Replace(body, "\n");
            body = TagRegex.Replace(body, string.Empty);
            body = WebUtility.HtmlDecode(body);

            var builder = new StringBuilder(body.Length);
            foreach (var line in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                builder.Append(HorizontalSpaceRegex.Replace(line, " ").Trim()).Append('\n');

            return BlankLinesRegex.Replace(builder.ToString(), "\n\n").Trim();
        }

        internal static string DocVersion(string unityVersion)
        {
            var v = unityVersion;
            int first = v.IndexOf('.');
            if (first < 0) return v;
            int second = v.IndexOf('.', first + 1);
            return second < 0 ? v : v.Substring(0, second);
        }

        private static string DocVersion() => DocVersion(Application.unityVersion);
    }
}
