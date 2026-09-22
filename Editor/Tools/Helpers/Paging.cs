// Copyright (C) KitWright. All rights reserved.
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KitWright.Editor.Tools.Helpers
{
    // One cursor shape for every tool that caps a list.
    internal static class Paging
    {
        internal static List<T> Page<T>(IList<T> items, int cursor, int pageSize) =>
            items.Skip(Mathf.Clamp(cursor, 0, items.Count)).Take(Mathf.Max(pageSize, 1)).ToList();

        // 0 once the page reached the end, so callers can treat it as "no more". The only copy of
        // this sum: tools whose page did not come from Page derive theirs here too, and Suffix
        // works it out rather than taking it, so what is reported cannot disagree with it.
        internal static int Next(int cursor, int shown, int total)
        {
            cursor = Mathf.Max(cursor, 0);
            return cursor + shown < total ? cursor + shown : 0;
        }

        // Empty while everything fits, so an unpaged response reads exactly as it did before.
        internal static string Suffix(int cursor, int shown, int total)
        {
            cursor = Mathf.Max(cursor, 0);
            var next = Next(cursor, shown, total);
            if (next > 0)
                return $" Showing {cursor + 1}-{cursor + shown} of {total}; pass cursor={next} for the next page.";
            if (cursor <= 0)
                return string.Empty;
            return shown > 0
                ? $" Showing {cursor + 1}-{cursor + shown} of {total}; end of the list."
                : $" cursor={cursor} is past the end of {total} item(s).";
        }

        internal const string CursorParam =
            "Resume at this index, as reported by a previous call's next_cursor. 0 starts at the beginning.";
    }
}
