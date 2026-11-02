// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace KitWright.Editor.Tools.Helpers
{
    /// <summary>
    /// Bounds on the files and directories the plugin writes under Library and %TEMP%, which nothing
    /// else ever removes. Every entry point swallows its own failures: housekeeping must never turn
    /// a tool call into an error.
    /// </summary>
    internal static class Housekeeping
    {
        internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

        /// <summary>Beyond the newest <paramref name="keep"/> AND older than <paramref name="maxAge"/>: both must hold.</summary>
        internal static List<string> SelectFilesToPrune(
            IEnumerable<(string path, DateTime utc)> files, int keep, TimeSpan maxAge, DateTime nowUtc)
        {
            return files
                .OrderByDescending(file => file.utc)
                .Skip(Math.Max(keep, 0))
                .Where(file => nowUtc - file.utc > maxAge)
                .Select(file => file.path)
                .ToList();
        }

        /// <summary>True once every <paramref name="every"/> calls; a counter that starts at <c>every</c> makes the first call due.</summary>
        internal static bool Due(ref int writesSincePrune, int every)
        {
            if (Interlocked.Increment(ref writesSincePrune) < every)
                return false;

            Interlocked.Exchange(ref writesSincePrune, 0);
            return true;
        }

        internal static void PruneFiles(string directory, string pattern, int keep, string justWritten)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return;

                var protectedPath = Path.GetFullPath(justWritten);
                var files = new DirectoryInfo(directory).EnumerateFiles(pattern)
                    .Select(file => (file.FullName, file.LastWriteTimeUtc));
                foreach (var path in SelectFilesToPrune(files, keep, StaleAfter, DateTime.UtcNow))
                {
                    if (string.Equals(path, protectedPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try { File.Delete(path); }
                    catch { }
                }
            }
            catch
            {
            }
        }

        internal static void DeleteStaleDirectories(string root, TimeSpan maxAge)
        {
            try
            {
                if (!Directory.Exists(root))
                    return;

                var directories = new DirectoryInfo(root).EnumerateDirectories()
                    .Select(directory => (directory.FullName, directory.LastWriteTimeUtc));
                foreach (var path in SelectFilesToPrune(directories, 0, maxAge, DateTime.UtcNow))
                {
                    try { Directory.Delete(path, true); }
                    catch { }
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Caps a per-session map that otherwise grows with every session id ever seen, evicting the
    /// least recently touched session first so a live one (touched on each request) survives.
    /// </summary>
    internal sealed class SessionRecency
    {
        internal const int MaxSessions = 256;

        private readonly ConcurrentDictionary<string, long> _stamps = new ConcurrentDictionary<string, long>();
        private long _clock;

        public void Touch(string key) => _stamps[key] = Interlocked.Increment(ref _clock);

        public int Evict<TValue>(ConcurrentDictionary<string, TValue> map, int cap)
        {
            if (_stamps.Count <= cap)
                return 0;

            var victims = SelectKeysToEvict(_stamps, cap);
            foreach (var key in victims)
            {
                _stamps.TryRemove(key, out _);
                map.TryRemove(key, out _);
            }

            return victims.Count;
        }

        internal static List<string> SelectKeysToEvict(IEnumerable<KeyValuePair<string, long>> stamps, int cap)
        {
            var all = stamps.ToList();
            return all
                .OrderBy(entry => entry.Value)
                .Take(Math.Max(all.Count - cap, 0))
                .Select(entry => entry.Key)
                .ToList();
        }
    }
}
