// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Reflection;
using KitWright.Editor.Services;
using UnityEditor;

namespace KitWright.Editor.Tools.Helpers
{
    /// <summary>
    /// "No Throttling" lease so long MCP ops keep progressing while the editor is unfocused.
    /// Uses EditorPrefs the native editor loop reads, so unlike managed workarounds it stays
    /// effective during domain reloads. Auto-extends while compiling/importing, restores the
    /// user's settings on expiry, and recovers a lease orphaned by a crashed session.
    /// </summary>
    [InitializeOnLoad]
    internal static class NoThrottleLease
    {
        internal const string InteractionModeKey = "InteractionMode";
        internal const string ApplicationIdleTimeKey = "ApplicationIdleTime";

        internal const string ActiveKey = "KitWright.NoThrottle.Active";
        internal const string PrevInteractionModeKey = "KitWright.NoThrottle.PrevInteractionMode";
        internal const string PrevIdleTimeKey = "KitWright.NoThrottle.PrevIdleTime";
        internal const string DeadlineKey = "KitWright.NoThrottle.DeadlineTicks";

        private static bool _hooked;

        static NoThrottleLease()
        {
            if (!IsActive)
                return;
            if (RecoverIfStale())
                return;
            Hook();
        }

        internal static bool IsActive => EditorPrefs.GetBool(ActiveKey, false);

        internal static void Acquire(TimeSpan duration)
        {
            long deadline = DateTime.UtcNow.Ticks + duration.Ticks;
            if (deadline > GetDeadlineTicks())
                SessionState.SetString(DeadlineKey, deadline.ToString());

            if (!IsActive)
            {
                EditorPrefs.SetInt(PrevInteractionModeKey, EditorPrefs.GetInt(InteractionModeKey, 0));
                EditorPrefs.SetInt(PrevIdleTimeKey, EditorPrefs.GetInt(ApplicationIdleTimeKey, 4));
                EditorPrefs.SetBool(ActiveKey, true);
                EditorPrefs.SetInt(InteractionModeKey, 1);
                EditorPrefs.SetInt(ApplicationIdleTimeKey, 0);
                ApplyInteractionModeSettings();
            }

            Hook();
        }

        internal static void Release()
        {
            if (!IsActive)
                return;
            SessionState.SetString(DeadlineKey, "0");
            TryExpire();
        }

        internal static void TryExpire()
        {
            if (!IsActive)
            {
                Unhook();
                return;
            }
            if (DateTime.UtcNow.Ticks < GetDeadlineTicks())
                return;
            if (ShouldHoldLease())
                return;
            Restore();
            Unhook();
        }

        /// <summary>Expiry gate of <see cref="TryExpire"/>, extracted so it is testable: a lease that
        /// expires mid-compile/import hands throttling back while the work is still running.</summary>
        internal static bool ShouldHoldLease()
        {
            return CompilationService.IsActuallyCompiling || EditorApplication.isUpdating;
        }

        /// <summary>Restores a lease that outlived its editor session (crash or quit mid-operation).
        /// SessionState is empty on a fresh session, so an active lease without a deadline is stale.</summary>
        internal static bool RecoverIfStale()
        {
            if (!IsActive)
                return false;
            if (SessionState.GetString(DeadlineKey, "").Length != 0)
                return false;
            Restore();
            return true;
        }

        private static void Restore()
        {
            EditorPrefs.SetInt(InteractionModeKey, EditorPrefs.GetInt(PrevInteractionModeKey, 0));
            EditorPrefs.SetInt(ApplicationIdleTimeKey, EditorPrefs.GetInt(PrevIdleTimeKey, 4));
            EditorPrefs.DeleteKey(ActiveKey);
            EditorPrefs.DeleteKey(PrevInteractionModeKey);
            EditorPrefs.DeleteKey(PrevIdleTimeKey);
            SessionState.EraseString(DeadlineKey);
            ApplyInteractionModeSettings();
        }

        private static long GetDeadlineTicks()
        {
            return long.TryParse(SessionState.GetString(DeadlineKey, "0"), out var ticks) ? ticks : 0;
        }

        private static void Hook()
        {
            if (_hooked)
                return;
            _hooked = true;
            EditorApplication.update += TryExpire;
        }

        private static void Unhook()
        {
            if (!_hooked)
                return;
            _hooked = false;
            EditorApplication.update -= TryExpire;
        }

        private static void ApplyInteractionModeSettings()
        {
            try
            {
                typeof(EditorApplication)
                    .GetMethod("UpdateInteractionModeSettings", BindingFlags.Static | BindingFlags.NonPublic)
                    ?.Invoke(null, null);
            }
            catch
            {
                // Internal API absent on this Unity version: prefs still apply on the next focus change.
            }
        }
    }
}
