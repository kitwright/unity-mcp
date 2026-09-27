// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Security.Cryptography;
using KitWright.Editor.Services;
using UnityEditor;

namespace KitWright.Editor.MCP.Server
{
    /// <summary>
    /// The shared secret a client must present to reach this project's editor. Without one, any
    /// process that can open a socket to the loopback port can drive the editor - and execute_code
    /// is in every tool exposure profile, so that is arbitrary code in the editor and arbitrary
    /// writes to the project's source.
    ///
    /// It rides in the URL path rather than a header: the config auto-rewrite already repairs URLs
    /// in client config files, so an existing install picks the token up on the next editor start
    /// with no user action, and no client has to support custom headers. The URL never leaves the
    /// machine - the server binds loopback only and no browser is allowed to speak to it at all.
    ///
    /// Stored in EditorPrefs, which is per-user and per-machine and survives a Library wipe.
    /// </summary>
    internal static class ServerToken
    {
        public const string Marker = "t";
        public const int Length = 32;

        private const string KeyPrefix = "KitWright.MCP.ServerToken.";

        /// <summary>This project's token, minted on first use.</summary>
        public static string Get()
        {
            var key = KeyPrefix + ProjectIdentity.PinFromProjectPath(ApplicationPaths.ProjectRoot);
            var existing = EditorPrefs.GetString(key, string.Empty);
            if (existing.Length == Length)
                return existing;

            var minted = Mint();
            EditorPrefs.SetString(key, minted);
            return minted;
        }

        internal static string Mint()
        {
            var bytes = new byte[Length / 2];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        /// <summary>The token segment of a request path, or empty when the path carries none.</summary>
        public static string ExtractToken(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            var query = path.IndexOf('?');
            if (query >= 0)
                path = path.Substring(0, query);

            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1; i++)
                if (string.Equals(segments[i], Marker, StringComparison.OrdinalIgnoreCase))
                    return segments[i + 1];

            return string.Empty;
        }

        public enum Verdict
        {
            /// <summary>The path carries this project's token.</summary>
            Ok,

            /// <summary>No token at all. A config written before tokens existed looks like this.</summary>
            Missing,

            /// <summary>A token that is not this project's: a stale config, or someone guessing.</summary>
            Mismatch
        }

        public static Verdict Check(string path, string expected)
        {
            if (string.IsNullOrEmpty(expected))
                return Verdict.Ok;

            var presented = ExtractToken(path);
            if (presented.Length == 0)
                return Verdict.Missing;

            return FixedTimeEquals(presented, expected) ? Verdict.Ok : Verdict.Mismatch;
        }

        public static string RefusalMessage(Verdict verdict) => verdict == Verdict.Missing
            ? "This editor only answers a URL that carries its access token. Press Configure in the KitWright " +
              "window, or restart the editor, to write the current URL into the client's config, then restart the client."
            : "That access token is not this project's. Press Configure in the KitWright window " +
              "to rewrite the client's config with the current URL.";

        /// <summary>The URL with its token segment blanked, for anything that gets logged.</summary>
        public static string Redact(string url)
        {
            var token = ExtractToken(url);
            return token.Length == 0 ? url : url.Replace("/" + token, "/<token>");
        }

        // string.Equals returns at the first differing char, so its timing tells a caller how much
        // of the token it has already guessed. The broker carries the same loop.
        private static bool FixedTimeEquals(string candidate, string expected)
        {
            if (candidate.Length != expected.Length)
                return false;

            var difference = 0;
            for (var i = 0; i < candidate.Length; i++)
                difference |= candidate[i] ^ expected[i];
            return difference == 0;
        }
    }
}
