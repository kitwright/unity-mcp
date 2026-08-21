// Copyright (C) KitWright. Licensed under MIT.

using KitWright.Editor.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace KitWright.Editor.MCP.Server
{
    internal static class SafetyPanel
    {
        public static void AddTo(VisualElement parent, SettingsController settings)
        {
            if (parent == null || settings == null)
                return;

            AddSafetyBox(parent, new VisualElement().Card(),
                "Default execute_code safety checks",
                "Default for execute_code calls when safety_checks is omitted. Explicit safety_checks=false can still bypass this for trusted local calls.",
                settings.ExecuteCodeSafetyChecksEnabled,
                value => settings.ExecuteCodeSafetyChecksEnabled = value);

            AddSafetyBox(parent, new VisualElement().Card(),
                "Strict filesystem guard",
                "Adds checks for broad System.IO file writes, raw file streams, and absolute/user/system/traversal paths. This is a defensive guard, not a complete sandbox.",
                settings.ExecuteCodeStrictFilesystemSafetyEnabled,
                value => settings.ExecuteCodeStrictFilesystemSafetyEnabled = value);

            AddSafetyBox(parent, new VisualElement().Card(),
                "Require client approval",
                "Off by default. When enabled, the first connection from an unknown client executable shows an Allow/Deny dialog and the answer is remembered per user across projects. Identity is the executable path, so approving a shared runtime (curl, node, python) covers every script that uses it. Batch mode and the editor itself are always allowed.",
                settings.RequireClientApprovalEnabled,
                value => settings.RequireClientApprovalEnabled = value);

            AddSafetyBox(parent, new VisualElement().Card(),
                "Auto-inject project namespaces",
                "Off by default. When enabled, only namespaces from loaded Library/ScriptAssemblies assemblies are injected; explicit using directives remain the least ambiguous option.",
                settings.ExecuteCodeProjectNamespaceInjectionEnabled,
                value => settings.ExecuteCodeProjectNamespaceInjectionEnabled = value);
        }

        private static void AddSafetyBox(
            VisualElement parent,
            VisualElement box,
            string title,
            string hint,
            bool value,
            System.Action<bool> onChanged)
        {
            var toggle = new MCPSwitchToggle(title);
            toggle.tooltip = hint;
            toggle.SetValueWithoutNotify(value);
            toggle.RegisterValueChangedCallback(onChanged);
            box.Add(toggle);

            parent.Add(box);
        }
    }
}
