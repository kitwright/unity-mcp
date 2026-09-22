// Copyright (C) KitWright. All rights reserved.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Threading;
using KitWright.Editor.Tools.Helpers;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// Read and dismiss the modal dialog holding the editor. Both run off the editor thread,
    /// since a modal is exactly when the editor thread cannot answer.
    /// </summary>
    [ToolProvider("EditorDialog")]
    internal static class EditorDialogFunctions
    {
        [Description("Report the modal dialog currently blocking the Unity editor: its title, message and button " +
                     "captions. Returns none when nothing is blocking. Answers while a modal owns the editor loop, " +
                     "unlike every other tool, because it reads window state instead of asking the editor. It cannot " +
                     "answer during a recompile or domain reload: the MCP backend is torn down there, so this call " +
                     "gets the same 'Unity is recompiling scripts' notice as any other tool.")]
        [ReadOnlyTool]
        [OffEditorThread]
        public static object GetEditorDialog()
        {
            if (!Win32Dialogs.TryDescribeBlockingDialog(out var title, out var message, out var buttons))
                return Response.Success("No modal dialog is blocking the editor.", new { open = false });

            return Response.Success($"Modal dialog '{title}' is blocking the editor.",
                new { open = true, title, message, buttons },
                buttons.Length > 0
                    ? "Pass one of these captions to dismiss_editor_dialog."
                    : "A Unity window draws its own buttons, so none are listed. " +
                      $"dismiss_editor_dialog accepts button='{Win32Dialogs.CloseCaption}' here, which closes it - the same as Cancel.");
        }

        [Description("Click a button on the modal dialog blocking the editor, to unstick it without leaving the chair. " +
                     "Both expected_title and button must match what is actually on screen, so a dialog the caller did " +
                     "not anticipate is refused rather than answered blindly. Call get_editor_dialog first. " +
                     "This answers a question on the user's behalf - 'Don't Save' discards their work - so pick the " +
                     "button deliberately.")]
        [OffEditorThread]
        public static object DismissEditorDialog(
            [ToolParam("Exact title of the dialog you expect, as reported by get_editor_dialog")] string expected_title,
            [ToolParam("Exact button caption to click, e.g. 'Cancel'")] string button)
        {
            if (string.IsNullOrWhiteSpace(expected_title) || string.IsNullOrWhiteSpace(button))
                return Response.Error("DIALOG_ARGS_REQUIRED",
                    new { hint = "Pass both expected_title and button; call get_editor_dialog to read them." });

            var refusal = Win32Dialogs.ClickBlockingDialogButton(expected_title, button);
            if (refusal != null)
                return Response.Error("DIALOG_NOT_DISMISSED", new { expected_title, button }, refusal);

            return Response.Success($"Clicked '{button}' on '{expected_title}'.", new { expected_title, button });
        }
    }
}
