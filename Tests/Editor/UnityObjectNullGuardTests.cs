// Copyright (C) KitWright. All rights reserved.

using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using KitWright.Editor.Tools.Scripting;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// Source scan for the one Unity trap that compiles, reads correctly and does the opposite of what
    /// it says. In the editor, GetComponent for a component that is not there does not hand back a C#
    /// null: it hands back a stub, so that dereferencing it reports "there is no X attached to the Y
    /// game object" instead of a bare NullReferenceException. Unity's own == operator calls that stub
    /// null. `??` and `?.` compare references and do not, so
    ///     var c = go.GetComponent&lt;AudioSource&gt;() ?? Undo.AddComponent&lt;AudioSource&gt;(go);
    /// never adds the component and throws on the first write to it - which is how add_audio_source,
    /// add_nav_mesh_agent, add_nav_mesh_obstacle and create_lod_group all shipped unable to add the
    /// component they exist to add. Nothing about the line looks wrong, and it would have worked in a
    /// player build, so a test that greps for it is the only thing that catches the next one.
    /// </summary>
    public sealed class UnityObjectNullGuardTests
    {
        // Deliberately only the GetComponent family: those are the lookups that answer "nothing found"
        // with a stub. An asset load or a scene search returns a real null, and ?? on one of those is
        // fine - a guard that flagged them too would be ignored within a week.
        // The plural overloads are absent because they return an array, where ?? behaves normally.
        private static readonly Regex Coalesced = new Regex(
            @"\bGetComponent(InChildren|InParent)?\s*(<[^<>()]*>)?\s*\([^()]*\)\s*(\?\?|\?\.)",
            RegexOptions.Compiled);

        private static string ThisFile([CallerFilePath] string path = null) => path;

        // Tests/Editor/<this file> -> Tests -> the package root.
        private static string PackageRoot() =>
            Directory.GetParent(Path.GetDirectoryName(ThisFile()))?.Parent?.FullName;

        [Test]
        public void NoUnityLookupIsNullCoalescedOrNullConditioned()
        {
            var root = PackageRoot();
            if (root == null || !Directory.Exists(Path.Combine(root, "Editor")))
                Assert.Ignore($"The package source is not on disk at '{root}', so it cannot be scanned.");

            var violations = new List<string>();

            foreach (var file in Directory.GetFiles(Path.Combine(root, "Editor"), "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(file);

                // Strings, chars and comments blanked with the offsets kept, so a line that only
                // mentions the pattern in prose is not reported. Single-line matching only: every
                // instance found so far was one line, and a call split across lines is rare enough
                // to be worth missing rather than to be worth a parser.
                var masked = CSharpMemberEditor.Mask(source).Split('\n');

                for (var i = 0; i < masked.Length; i++)
                {
                    if (!Coalesced.IsMatch(masked[i]))
                        continue;

                    violations.Add($"{Path.GetFileName(file)}:{i + 1} -- {masked[i].Trim()}");
                }
            }

            Assert.IsEmpty(violations,
                "A Unity lookup was combined with ?? or ?. Neither honours UnityEngine.Object's own " +
                "== operator, so the fallback never runs and the stub is used as if it were the real " +
                "thing. Assign to a local and compare it with == null instead:\n" +
                string.Join("\n", violations));
        }
    }
}
