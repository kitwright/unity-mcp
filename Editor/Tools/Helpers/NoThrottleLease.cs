// Copyright (C) KitWright. All rights reserved.
using UnityEditor;

namespace KitWright.Editor.Tools.Helpers
{
    /// <summary>
    /// What is left of the "No Throttling" lease: putting back one that an older version left
    /// active. The lease switched the user-wide InteractionMode prefs to No Throttling around long
    /// operations so they would progress while the editor was unfocused. Measured on 6000.3 it did
    /// not: an unfocused editor ticked 10 times a second with it and without it, and a compile plus
    /// domain reload took as long either way. What it did do was rewrite a setting every other editor
    /// of the same user reads, and one editor could take another's lease. An editor that crashed or
    /// quit mid-lease left No Throttling on for good, which this restores once.
    /// </summary>
    [InitializeOnLoad]
    internal static class NoThrottleLease
    {
        internal const string InteractionModeKey = "InteractionMode";
        internal const string ApplicationIdleTimeKey = "ApplicationIdleTime";
        internal const string ActiveKey = "KitWright.NoThrottle.Active";
        internal const string PrevInteractionModeKey = "KitWright.NoThrottle.PrevInteractionMode";
        internal const string PrevIdleTimeKey = "KitWright.NoThrottle.PrevIdleTime";

        static NoThrottleLease()
        {
            RestoreLeftoverLease();
        }

        internal static void RestoreLeftoverLease()
        {
            if (!EditorPrefs.GetBool(ActiveKey, false))
                return;

            EditorPrefs.SetInt(InteractionModeKey, EditorPrefs.GetInt(PrevInteractionModeKey, 0));
            EditorPrefs.SetInt(ApplicationIdleTimeKey, EditorPrefs.GetInt(PrevIdleTimeKey, 4));
            EditorPrefs.DeleteKey(ActiveKey);
            EditorPrefs.DeleteKey(PrevInteractionModeKey);
            EditorPrefs.DeleteKey(PrevIdleTimeKey);
        }
    }
}
