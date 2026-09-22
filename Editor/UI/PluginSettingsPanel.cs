// Copyright (C) KitWright. All rights reserved.

using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using KitWright.Editor.Settings;

namespace KitWright.Editor.MCP.Server
{
    internal sealed class PluginSettingsPanel : IMCPWindowPanel
    {
        private readonly SettingsController _settingsController;
        private VisualElement _container;
        private MCPSwitchToggle _debugLoggingToggle;

        public PluginSettingsPanel(SettingsController settingsController)
        {
            _settingsController = settingsController;
        }

        public void Build(VisualElement container)
        {
            _container = container;
            _settingsController.OnSettingsChanged += RefreshStatus;
            BuildUI();
        }

        public void Dispose()
        {
            if (_settingsController != null)
                _settingsController.OnSettingsChanged -= RefreshStatus;
        }

        private void BuildUI()
        {
            _container.Clear();

            _container.Add(MCPSection.PanelTitle("MCP Settings"));
            _container.Add(MCPSection.PanelHint("Project-level settings for the KitWright MCP for Unity plugin. Safety checks and debug logging are stored per project."));

            var (settingsSection, settingsFoldout) = MCPSection.Create(
                "Settings", "Settings", labelColor: MCPPalette.HeadingBlue);
            _container.Add(settingsSection);

            var autostartSection = new VisualElement().Card();
            var autostartToggle = new MCPSwitchToggle("Autostart on Unity open");
            autostartToggle.tooltip = "When enabled, the MCP server starts automatically the next time you open this Unity project (if it was connected).";
            autostartToggle.SetValueWithoutNotify(_settingsController.MCPAutostartEnabled);
            autostartToggle.RegisterValueChangedCallback(value => _settingsController.MCPAutostartEnabled = value);
            autostartSection.Add(autostartToggle);
            settingsFoldout.Add(autostartSection);

            AddToggleCard(settingsFoldout,
                "Default execute_code safety checks",
                "The default for execute_code calls that omit safety_checks. On its own it stops accidents, not a caller that wants through: any client can pass safety_checks=false. Lock it below to make that argument inert.",
                _settingsController.ExecuteCodeSafetyChecksEnabled,
                value => _settingsController.ExecuteCodeSafetyChecksEnabled = value);

            AddToggleCard(settingsFoldout,
                "Lock execute_code safety checks",
                "Off by default. When on, the setting above becomes the only input and a client's safety_checks argument is ignored, so a caller cannot clear the guard. Turn it on for a shared or untrusted client; legitimate reflection, P/Invoke, or serialization snippets then need it turned off again.",
                _settingsController.ExecuteCodeSafetyChecksLocked,
                value => _settingsController.ExecuteCodeSafetyChecksLocked = value);

            AddToggleCard(settingsFoldout,
                "Strict filesystem guard",
                "Adds checks for broad System.IO file writes, raw file streams, and absolute/user/system/traversal paths. This is a defensive guard, not a complete sandbox.",
                _settingsController.ExecuteCodeStrictFilesystemSafetyEnabled,
                value => _settingsController.ExecuteCodeStrictFilesystemSafetyEnabled = value);

            AddToggleCard(settingsFoldout,
                "Auto-inject project namespaces",
                "Off by default. When enabled, only namespaces from loaded Library/ScriptAssemblies assemblies are injected; explicit using directives remain the least ambiguous option.",
                _settingsController.ExecuteCodeProjectNamespaceInjectionEnabled,
                value => _settingsController.ExecuteCodeProjectNamespaceInjectionEnabled = value);

            var debugSection = new VisualElement().Card();

            _debugLoggingToggle = new MCPSwitchToggle("Enable debug logging");
            _debugLoggingToggle.tooltip = "When enabled, plugin lifecycle, MCP request, transport, and tool execution traces are written to the Unity Console. Warnings and errors are always written.";
            _debugLoggingToggle.SetValueWithoutNotify(_settingsController.PluginDebugLoggingEnabled);
            _debugLoggingToggle.RegisterValueChangedCallback(value =>
            {
                _settingsController.PluginDebugLoggingEnabled = value;
                RefreshStatus();
            });
            debugSection.Add(_debugLoggingToggle);

            settingsFoldout.Add(debugSection);

            AddToggleCard(settingsFoldout,
                "Compact tool schema",
                "Strip parameter descriptions and trim each tool description to its first sentence when exporting the schema. Saves ~8-13k tokens per session at the cost of terser tool docs.",
                _settingsController.MCPCompactSchemaEnabled,
                value => _settingsController.MCPCompactSchemaEnabled = value);

            var logCapacitySection = new VisualElement().Card();
            logCapacitySection.Add(BuildSizeSlider("Recent activity log limit",
                "Maximum number of tool call entries kept in the Recent Activity log buffer (circular buffer).",
                () => _settingsController.ActivityLogCapacity,
                v => _settingsController.ActivityLogCapacity = v,
                50, 1000));
            settingsFoldout.Add(logCapacitySection);

            var screenshotSection = new VisualElement().Card();
            screenshotSection.Add(BuildSizeSlider("Game/Scene screenshot size",
                "Longest side of capture_game_view/scene_view when no width/height is passed. Smaller = fewer tokens.",
                () => _settingsController.ScreenshotDefaultSize,
                v => _settingsController.ScreenshotDefaultSize = v));
            var wndSlider = BuildSizeSlider("Editor window screenshot size",
                "Longest side of capture_editor_window when no width/height is passed. Smaller = fewer tokens.",
                () => _settingsController.EditorWindowScreenshotSize,
                v => _settingsController.EditorWindowScreenshotSize = v);
            wndSlider.style.marginTop = 8;
            screenshotSection.Add(wndSlider);
            settingsFoldout.Add(screenshotSection);

            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (_settingsController == null)
                return;

            if (_debugLoggingToggle != null)
                _debugLoggingToggle.SetValueWithoutNotify(_settingsController.PluginDebugLoggingEnabled);
        }

        private const int ScreenshotSizeMin = 64;
        private const int ScreenshotSizeMax = 4096;

        private static void AddToggleCard(VisualElement parent, string title, string hint, bool value, Action<bool> onChanged)
        {
            var toggle = new MCPSwitchToggle(title);
            toggle.tooltip = hint;
            toggle.SetValueWithoutNotify(value);
            toggle.RegisterValueChangedCallback(onChanged);

            var card = new VisualElement().Card();
            card.Add(toggle);
            parent.Add(card);
        }

        private VisualElement BuildSizeSlider(string labelText, string tooltip, Func<int> getter, Action<int> setter, int min = ScreenshotSizeMin, int max = ScreenshotSizeMax)
        {
            var accent = MCPPalette.AccentGreen;
            var root = new VisualElement();
            root.style.flexDirection = FlexDirection.Row;
            root.style.alignItems = Align.Center;
            root.style.flexShrink = 0;

            var title = new Label(labelText);
            title.style.fontSize = 13;
            title.style.color = new Color(0.88f, 0.88f, 0.9f);
            title.style.flexShrink = 0;
            title.style.width = 190;
            title.style.marginRight = 10;
            title.tooltip = tooltip;
            root.Add(title);

            var track = new VisualElement();
            track.style.flexGrow = 1;
            track.style.height = 8;
            track.style.backgroundColor = new Color(0.22f, 0.22f, 0.24f);
            track.Rounded(4);
            track.style.justifyContent = Justify.Center;

            var fill = new VisualElement();
            fill.style.position = Position.Absolute;
            fill.style.left = 0;
            fill.style.height = 8;
            fill.style.backgroundColor = accent;
            fill.Rounded(4);
            track.Add(fill);

            var handle = new VisualElement();
            handle.style.position = Position.Absolute;
            handle.style.width = 18;
            handle.style.height = 18;
            handle.style.backgroundColor = Color.white;
            handle.Rounded(9).Border(2, accent);
            track.Add(handle);
            root.Add(track);

            var valueBox = new IntegerField();
            valueBox.style.width = 60;
            valueBox.style.height = 18;
            valueBox.style.flexGrow = 0;
            valueBox.style.flexShrink = 0;
            valueBox.style.marginLeft = 10;
            var vbInput = valueBox.Q("unity-text-input");
            if (vbInput != null)
            {
                vbInput.style.backgroundColor = new Color(0.12f, 0.14f, 0.12f);
                vbInput.style.unityTextAlign = TextAnchor.MiddleCenter;
                vbInput.style.flexShrink = 0;
                vbInput.style.height = 18;
            }
            var vbText = valueBox.Q<Label>();
            if (vbText != null) vbText.style.color = accent;
            root.Add(valueBox);

            var current = Mathf.Clamp(getter(), min, max);

            void Paint(int v)
            {
                var t = (v - min) / (float)(max - min);
                var w = track.resolvedStyle.width;
                fill.style.width = t * w;
                handle.style.left = t * w - 9;
                handle.style.top = -5;
                valueBox.SetValueWithoutNotify(v);
            }

            void Commit(int v)
            {
                current = Mathf.Clamp(v, min, max);
                setter(current);
                Paint(current);
            }

            void SetFromMouse(float localX, float trackWidth)
            {
                var t = Mathf.Clamp01(localX / Mathf.Max(1f, trackWidth));
                Commit(Mathf.RoundToInt(min + t * (max - min)));
            }

            var dragging = false;
            track.RegisterCallback<MouseDownEvent>(e => { dragging = true; track.CaptureMouse(); SetFromMouse((float)e.localMousePosition.x, track.resolvedStyle.width); });
            track.RegisterCallback<MouseMoveEvent>(e => { if (dragging) SetFromMouse((float)e.localMousePosition.x, track.resolvedStyle.width); });
            track.RegisterCallback<MouseUpEvent>(e => { dragging = false; track.ReleaseMouse(); });
            track.RegisterCallback<MouseCaptureOutEvent>(_ => dragging = false);
            track.RegisterCallback<GeometryChangedEvent>(_ => Paint(current));
            valueBox.RegisterCallback<FocusOutEvent>(_ => Commit(valueBox.value));
            valueBox.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) Commit(valueBox.value); });

            Paint(current);
            return root;
        }

    }
}
