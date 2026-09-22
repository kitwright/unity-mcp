// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KitWright.Editor.Threading
{
    /// <summary>
    /// Reads and dismisses the modal dialog holding the editor. Windows only. Works off the
    /// editor thread on purpose: a modal is exactly when that thread cannot answer.
    /// </summary>
    internal static class Win32Dialogs
    {
        internal const string CloseCaption = "Close";

#if UNITY_EDITOR_WIN
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc callback, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendMessageTimeoutPtr(
            IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeoutW(
            IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam,
            uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint BM_CLICK = 0x00F5;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_GETTEXT = 0x000D;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        // Long enough that a pumping editor always answers, short enough that one that is not
        // pumping costs a probe a quarter second per window instead of the rest of the session.
        private const uint TextTimeoutMs = 250;

        // A dialog that has not acknowledged a click in five seconds is not going to.
        private const uint ClickTimeoutMs = 5000;
#endif

        /// <summary>The blocking dialog's title and buttons, or null when nothing conclusive is visible.</summary>
        public static string BlockingDialog()
        {
            if (!TryDescribeBlockingDialog(out var title, out _, out var buttons))
                return null;

            return buttons.Length == 0 ? title : $"{title} [buttons: {string.Join(" | ", buttons)}]";
        }

        internal static bool TryDescribeBlockingDialog(out string title, out string message, out string[] buttons)
        {
            message = null;
            buttons = Array.Empty<string>();

            var dialog = FindBlockingDialog(out title);
            if (dialog == IntPtr.Zero)
                return false;

            message = BodyText(dialog);
            buttons = ButtonCaptions(dialog);
            return true;
        }

        /// <summary>Clicks a button by caption. Returns null on success, or why it refused.</summary>
        internal static string ClickBlockingDialogButton(string expectedTitle, string caption)
        {
#if UNITY_EDITOR_WIN
            var dialog = FindBlockingDialog(out var title);
            if (dialog == IntPtr.Zero)
                return "No modal dialog is open, or more than one editor window is enabled so it cannot be identified.";

            if (!string.Equals(title, expectedTitle, StringComparison.Ordinal))
                return $"The open dialog is '{title}', not '{expectedTitle}'. Refusing to click a dialog the caller did not expect.";

            var buttons = ButtonCaptions(dialog);

            // A Unity IMGUI modal draws its buttons rather than owning Win32 ones, so there is
            // nothing to click; closing the window is the only lever, and it means Cancel.
            if (buttons.Length == 0)
            {
                if (!string.Equals(caption, CloseCaption, StringComparison.OrdinalIgnoreCase))
                    return $"'{title}' is a Unity window with no clickable buttons. " +
                           $"Pass button='{CloseCaption}' to close it, which is what its title-bar X does.";

                PostMessageW(dialog, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return null;
            }

            var target = FindButton(dialog, caption);
            if (target == IntPtr.Zero)
                return $"'{title}' has no button captioned '{caption}'. Buttons: {string.Join(" | ", buttons)}.";

            // Timeout for the same reason TextOf has one: this runs off the editor thread, and a
            // plain SendMessage waits for the target's thread with no way out. The click is still
            // delivered; only the wait for its acknowledgement is bounded.
            SendMessageTimeoutPtr(
                target, BM_CLICK, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, ClickTimeoutMs, out _);
            return null;
#else
            return "Dismissing editor dialogs is implemented for Windows only.";
#endif
        }

        private static IntPtr FindBlockingDialog(out string title)
        {
            title = null;
#if UNITY_EDITOR_WIN
            try
            {
                var ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                var dialog = IntPtr.Zero;
                string found = null;
                var enabledCount = 0;
                var anyDisabled = false;

                EnumWindows((hWnd, _) =>
                {
                    GetWindowThreadProcessId(hWnd, out var pid);
                    if (pid != ownPid || !IsWindowVisible(hWnd))
                        return true;

                    var text = TextOf(hWnd);
                    if (text.Length == 0)
                        return true;

                    if (!IsWindowEnabled(hWnd))
                        anyDisabled = true;
                    else if (enabledCount++ == 0)
                    {
                        dialog = hWnd;
                        found = text;
                    }

                    return true;
                }, IntPtr.Zero);

                // A modal disables the window it owns; two enabled windows means floating editor
                // windows are in play and naming the wrong one is worse than staying vague.
                if (!anyDisabled || enabledCount != 1)
                    return IntPtr.Zero;

                title = found;
                return dialog;
            }
            catch
            {
                return IntPtr.Zero;
            }
#else
            return IntPtr.Zero;
#endif
        }

#if UNITY_EDITOR_WIN
        // Returning false from the visitor stops the walk, the way EnumChildWindows itself works.
        private static void ForEachChild(IntPtr dialog, Func<IntPtr, bool> visit)
        {
            EnumChildWindows(dialog, (child, _) => visit(child), IntPtr.Zero);
        }

        // The message body sits in a Static or, for Unity's own prompts, an Edit control.
        private static string BodyText(IntPtr dialog)
        {
            var parts = new List<string>();
            ForEachChild(dialog, child =>
            {
                var className = ClassOf(child);
                if (className.Equals("Static", StringComparison.OrdinalIgnoreCase) ||
                    className.Equals("Edit", StringComparison.OrdinalIgnoreCase))
                {
                    var text = TextOf(child);
                    if (text.Length > 0)
                        parts.Add(text);
                }
                return true;
            });

            return parts.Count == 0 ? null : string.Join("\n", parts);
        }

        private static IntPtr FindButton(IntPtr dialog, string caption)
        {
            var match = IntPtr.Zero;
            ForEachChild(dialog, child =>
            {
                if (!IsButton(child) || !IsWindowEnabled(child) ||
                    !CaptionOf(child).Equals(caption, StringComparison.OrdinalIgnoreCase))
                    return true;

                match = child;
                return false;
            });
            return match;
        }

        private static bool IsButton(IntPtr hWnd)
        {
            return ClassOf(hWnd).Equals("Button", StringComparison.OrdinalIgnoreCase) && IsWindowVisible(hWnd);
        }

        private static string CaptionOf(IntPtr hWnd) => TextOf(hWnd).Replace("&", string.Empty);

        private static string ClassOf(IntPtr hWnd)
        {
            var name = new StringBuilder(64);
            GetClassNameW(hWnd, name, name.Capacity);
            return name.ToString();
        }

        /// <summary>
        /// The window title, or empty when the owning thread does not answer in time.
        /// </summary>
        /// <remarks>
        /// Never GetWindowText here. For a window of the calling process it sends WM_GETTEXT and
        /// waits with no timeout for the owning thread to pump. This runs off the editor thread on
        /// purpose, and during a domain reload that thread stops pumping -- so the call parks in
        /// user32, where Mono cannot abort it, and the domain unload waits on that thread pool job
        /// forever. Editor stuck in "Reloading Domain", nothing in any log. Bound the send instead.
        /// </remarks>
        private static string TextOf(IntPtr hWnd)
        {
            var text = new StringBuilder(512);
            if (SendMessageTimeoutW(
                    hWnd, WM_GETTEXT, (IntPtr)text.Capacity, text,
                    SMTO_ABORTIFHUNG, TextTimeoutMs, out _) == IntPtr.Zero)
                return string.Empty;

            return text.ToString();
        }

        private static string[] ButtonCaptions(IntPtr dialog)
        {
            var captions = new List<string>();
            ForEachChild(dialog, child =>
            {
                if (IsButton(child))
                {
                    var caption = CaptionOf(child);
                    if (caption.Length > 0)
                        captions.Add(caption);
                }
                return true;
            });

            return captions.Count == 0 ? Array.Empty<string>() : captions.ToArray();
        }
#else
        private static string BodyText(IntPtr dialog) => null;
        private static string[] ButtonCaptions(IntPtr dialog) => Array.Empty<string>();
#endif
    }
}
