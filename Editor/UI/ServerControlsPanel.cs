// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KitWright.Editor.Settings;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace KitWright.Editor.MCP.Server
{
    internal sealed class ServerControlsPanel
    {
        private const float FieldLabelWidth = 100f;
        private const string DirectTransportChoice = "Direct HTTP";
        private const string BrokerTransportChoice = "Broker Mode (default)";
        private static readonly List<string> TransportChoices = new List<string> { BrokerTransportChoice, DirectTransportChoice };

        private readonly SettingsController _settings;
        private readonly MCPServerService _server;
        private readonly Action _refreshStatus;
        private readonly Action _rebuildWindow;
        private Label _brokerStatus;
        private TextField _brokerMonoPathField;
        private Label _brokerMonoHint;
        private Button _connectButton;
        private Label _connectIcon;
        private Label _connectText;
        private bool _connecting;

        public ServerControlsPanel(
            SettingsController settings,
            MCPServerService server,
            Action refreshStatus,
            Action rebuildWindow)
        {
            _settings = settings;
            _server = server;
            _refreshStatus = refreshStatus;
            _rebuildWindow = rebuildWindow;
        }

        public void AddTo(VisualElement parent)
        {
            var portRow = new VisualElement();
            portRow.style.flexDirection = FlexDirection.Row;
            portRow.style.alignItems = Align.Center;
            portRow.style.marginBottom = 8;

            var portField = new IntegerField("Server Port");
            portField.SetValueWithoutNotify(_settings.MCPServerPort);
            // Commit on Enter/blur rather than per keystroke, since committing triggers a
            // full transport restart below -- typing a multi-digit port would otherwise
            // restart the server once per digit.
            portField.isDelayed = true;
            portField.RegisterValueChangedCallback(evt =>
            {
                _settings.MCPServerPort = evt.newValue;
                if (_server != null && _server.IsRunning)
                {
                    // The port is part of the transport settings, so the write above already
                    // scheduled a restart -- IsTransitioning is true from here on. Show the same
                    // Connecting state as pressing Connect; the poll below rebuilds once the new
                    // port is actually bound.
                    UpdateConnectButton();
                    InvokeRefreshStatus();
                    return;
                }

                // The config sweep otherwise only runs on server start, so a port edit made
                // while stopped never reaches the client files.
                MCPClientConfigAutoRewrite.Schedule(_settings.MCPServerPort);

                // Rebuild rather than refresh: the Client Configuration snippet renders the
                // port into a read-only field that is only ever built once.
                EditorApplication.delayCall += () => _rebuildWindow();
            });
            portField.style.flexGrow = 1;
            portField.Shrinkable();
            portField.style.marginBottom = 0;
            LockLabelWidth(portField);
            portRow.Add(portField);

            _connectButton = new Button(ToggleServer);
            _connectButton.style.flexDirection = FlexDirection.Row;
            _connectButton.style.alignItems = Align.Center;
            _connectButton.style.justifyContent = Justify.Center;
            _connectButton.style.flexShrink = 0;
            _connectButton.style.width = 130;
            _connectButton.style.height = 24;
            _connectButton.style.marginLeft = 6;
            _connectButton.style.marginRight = 0;
            _connectButton.style.marginTop = 0;
            _connectButton.style.marginBottom = 0;
            _connectButton.Rounded(6);
            _connectButton.style.color = Color.white;

            _connectIcon = new Label();
            _connectIcon.style.fontSize = 12;
            _connectIcon.style.marginRight = 6;
            _connectIcon.style.unityTextAlign = TextAnchor.MiddleCenter;
            _connectButton.Add(_connectIcon);

            _connectText = new Label();
            _connectText.style.fontSize = 13;
            _connectText.style.unityFontStyleAndWeight = FontStyle.Bold;
            _connectText.style.color = Color.white;
            _connectButton.Add(_connectText);

            portRow.Add(_connectButton);
            parent.Add(portRow);
            UpdateConnectButton();

            var transportModeDropdown = new DropdownField("Transport Mode");
            transportModeDropdown.choices = TransportChoices;
            transportModeDropdown.tooltip =
                "Broker Mode (default): runs a tiny local broker process that owns the MCP HTTP port and keeps " +
                "client requests alive while Unity reloads the scripting domain. Falls back to Direct HTTP if the " +
                "broker cannot start. " +
                "Direct HTTP: the server owns the port itself.";
            transportModeDropdown.SetValueWithoutNotify(_settings.MCPBrokerModeEnabled ? BrokerTransportChoice : DirectTransportChoice);
            transportModeDropdown.RegisterValueChangedCallback(evt =>
            {
                var enabled = evt.newValue == BrokerTransportChoice;
                _settings.MCPBrokerModeEnabled = enabled;
                UpdateBrokerControls(enabled);

                // Switching to Direct: the broker still owns the configured port, so the in-process
                // listener would fall forward to the next free one and rewrite every client config.
                if (!enabled)
                    MCPBrokerProcessManager.Stop();

                if (_settings.MCPServerEnabled)
                {
                    // The restart probes broker health, so the status refresh below would report
                    // the old stopped state and never correct itself.
                    _connecting = true;
                    UpdateConnectButton();
                    _ = _server.StopAsync();
                    StartAndRebuild();
                    return;
                }

                EditorApplication.delayCall += () =>
                    EditorApplication.delayCall += () => { UpdateBrokerStatus(); InvokeRefreshStatus(); };
            });
            transportModeDropdown.style.marginBottom = 4;
            LockLabelWidth(transportModeDropdown);
            MCPDropdownStyle.Apply(transportModeDropdown);
            parent.Add(transportModeDropdown);

            _brokerMonoPathField = new TextField("Broker Mono Path");
            _brokerMonoPathField.SetValueWithoutNotify(_settings.MCPBrokerMonoPath);
            _brokerMonoPathField.RegisterValueChangedCallback(evt =>
            {
                _settings.MCPBrokerMonoPath = evt.newValue;
                EditorApplication.delayCall += UpdateBrokerStatus;
            });
            _brokerMonoPathField.style.marginBottom = 4;
            _brokerMonoPathField.tooltip = "Mono runtime used to run the broker process.";
            LockLabelWidth(_brokerMonoPathField);
            parent.Add(_brokerMonoPathField);

            _brokerMonoHint = new Label();
            _brokerMonoHint.style.whiteSpace = WhiteSpace.Normal;
            _brokerMonoHint.style.color = new Color(0.9f, 0.35f, 0.35f);
            _brokerMonoHint.style.marginBottom = 4;
            parent.Add(_brokerMonoHint);

            RefreshMonoPathAutoDetection();

            _brokerStatus = new Label();
            _brokerStatus.style.whiteSpace = WhiteSpace.Normal;
            _brokerStatus.style.opacity = 0.78f;
            _brokerStatus.style.marginBottom = 10;
            parent.Add(_brokerStatus);

            UpdateBrokerControls(_settings.MCPBrokerModeEnabled);
            UpdateBrokerStatus();

            // Nothing notifies this window when the server restarts itself -- a port edit while
            // running goes through HandleSettingsChanged, and a start that had to fall forward
            // lands on a port the fields never saw. Poll the state we render instead.
            var observed = (_server?.IsTransitioning, _server?.IsRunning, _server?.Port);
            parent.schedule.Execute(() =>
            {
                var current = (_server?.IsTransitioning, _server?.IsRunning, _server?.Port);
                if (current == observed)
                    return;

                observed = current;

                // A restart moves through three states, and rebuilding on each would flash the
                // whole window; the two labels carry it until the server settles.
                if (_server != null && _server.IsTransitioning)
                {
                    UpdateConnectButton();
                    InvokeRefreshStatus();
                    return;
                }

                _rebuildWindow();
            }).Every(500);
        }

        private static void LockLabelWidth(VisualElement field)
        {
            field.style.marginLeft = 0;
            field.style.marginRight = 0;

            var label = field.Q<Label>(className: "unity-base-field__label");
            if (label == null)
                return;

            label.style.width = FieldLabelWidth;
            label.style.minWidth = FieldLabelWidth;
            label.style.maxWidth = FieldLabelWidth;
        }

        // StartAsync blocks until its first await (port probing, broker spawn), so yield a frame to
        // let the button repaint, then rebuild once Port finally lands -- the Client Configuration
        // snippet renders that port, not the setting.
        private void StartAndRebuild()
        {
            EditorApplication.delayCall += () => _server.StartAsync().ContinueWith(
                _ => EditorApplication.delayCall += () => _rebuildWindow(),
                TaskScheduler.Default);
        }

        private void ToggleServer()
        {
            var enable = !_settings.MCPServerEnabled;
            _settings.MCPServerEnabled = enable;
            if (enable)
            {
                _connecting = true;
                UpdateConnectButton();
                StartAndRebuild();
                return;
            }

            _ = _server.StopAsync();
            MCPBrokerProcessManager.Stop();

            EditorApplication.delayCall += () =>
                EditorApplication.delayCall += () =>
                {
                    UpdateConnectButton();
                    UpdateBrokerStatus();
                    InvokeRefreshStatus();
                };
        }

        private void UpdateConnectButton()
        {
            if (_connectButton == null)
                return;

            // Starting takes a visible moment, so report the transition instead of looking dead.
            // ◌ shares the ■ / ▶ Unicode block, so it renders in the editor's default font.
            if (_connecting || _server?.IsTransitioning == true)
            {
                _connectText.text = "Connecting...";
                _connectIcon.text = "◌";
                _connectIcon.style.color = new Color(0.8f, 0.8f, 0.8f);
                _connectButton.style.backgroundColor = new Color(0.28f, 0.28f, 0.30f);
                _connectButton.SetEnabled(false);
                return;
            }

            var running = _settings.MCPServerEnabled;
            _connectText.text = running ? "Disconnect" : "Connect";
            _connectIcon.text = running ? "■" : "▶";
            _connectIcon.style.color = running ? new Color(0.95f, 0.55f, 0.5f) : new Color(0.35f, 0.9f, 0.4f);

            var baseColor = running ? new Color(0.42f, 0.24f, 0.24f) : new Color(0.22f, 0.42f, 0.26f);
            _connectButton.style.backgroundColor = baseColor;
        }

        private void InvokeRefreshStatus()
        {
            _refreshStatus?.Invoke();
        }

        private void UpdateBrokerControls(bool enabled)
        {
            if (_brokerMonoPathField != null)
                _brokerMonoPathField.style.display = enabled ? DisplayStyle.Flex : DisplayStyle.None;
            if (_brokerMonoHint != null)
                _brokerMonoHint.style.display = enabled && !string.IsNullOrEmpty(_brokerMonoHint.text)
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
        }

        /// <summary>
        /// Auto-detection is display-only: it never writes to <see cref="SettingsController.MCPBrokerMonoPath"/>,
        /// so clearing the field (or never touching it) keeps the setting at its real "auto-detect" default.
        /// </summary>
        private void RefreshMonoPathAutoDetection()
        {
            if (_brokerMonoPathField == null)
                return;

            if (!string.IsNullOrEmpty(_settings.MCPBrokerMonoPath))
            {
                _brokerMonoPathField.tooltip =
                    "Optional override for Unity's bundled Mono executable. Leave empty to auto-detect it from the Unity editor install.";
                SetMonoHint(null);
                return;
            }

            var detected = MCPBrokerProcessManager.ResolveMono(null);
            if (!string.IsNullOrEmpty(detected))
            {
                _brokerMonoPathField.SetValueWithoutNotify(detected);
                _brokerMonoPathField.tooltip =
                    "Auto-detected from the Unity editor install. Fill this in only if you need to override it.";
                SetMonoHint(null);
            }
            else
            {
                _brokerMonoPathField.tooltip =
                    "Optional override for Unity's bundled Mono executable. Leave empty to auto-detect it from the Unity editor install.";
                SetMonoHint("Could not auto-detect Unity's bundled Mono executable. Broker mode needs this path set manually.");
            }
        }

        private void SetMonoHint(string text)
        {
            if (_brokerMonoHint == null)
                return;

            _brokerMonoHint.text = text ?? string.Empty;
            _brokerMonoHint.style.display = !string.IsNullOrEmpty(text) && _settings.MCPBrokerModeEnabled
                ? DisplayStyle.Flex
                : DisplayStyle.None;
        }

        private void UpdateBrokerStatus()
        {
            if (_brokerStatus == null)
                return;

            if (!_settings.MCPBrokerModeEnabled)
            {
                // The port field shows the configured port; a start that had to fall forward
                // binds a different one, and the client must be pointed at the bound port. Why it
                // moved is the Connection row's line -- naming it here too said it twice.
                _brokerStatus.text = _server != null && _server.IsRunning && _server.Port != _settings.MCPServerPort
                    ? "Transport: Direct HTTP on port " + _server.Port + "."
                    : "Transport: Direct HTTP.";
                return;
            }

            if (MCPBrokerProcessManager.IsRunning(out var pid, out var port))
            {
                _brokerStatus.text = "Transport: Broker running (pid " + pid + ", port " + port + ").";
                return;
            }

            var error = MCPBrokerProcessManager.LastError;
            _brokerStatus.text = string.IsNullOrEmpty(error)
                ? "Transport: Broker will start with the MCP server."
                : "Transport: Broker not running - " + error;
        }
    }
}
