// Copyright (C) KitWright. Licensed under MIT.

using KitWright.Editor.Services;
using KitWright.Editor.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace KitWright.Editor.MCP.Server
{
    internal sealed class HeaderStatusPanel
    {
        private readonly SettingsController _settings;
        private readonly MCPServerService _server;
        private Label _statusLabel;
        private Label _alertLabel;
        private Label _versionLabel;

        public HeaderStatusPanel(SettingsController settings, MCPServerService server)
        {
            _settings = settings;
            _server = server;
        }

        public void AddTo(VisualElement parent, VisualElement statusHost = null)
        {
            var titleRow = new VisualElement().Card().Padding(6, 10, 6, 10);
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.alignItems = Align.Center;
            parent.Add(titleRow);

            var icon = PluginIcon.LogoTextTexture;
            if (icon != null)
            {
                var logo = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit };
                var h = 28;
                logo.style.height = h;
                logo.style.width = h * icon.width / icon.height;
                logo.style.flexShrink = 0;
                titleRow.Add(logo);
            }
            else
            {
                var title = new Label("KitWright");
                title.style.fontSize = 18;
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                title.style.color = Color.white;
                titleRow.Add(title);
            }

            _statusLabel = new Label();
            _statusLabel.style.fontSize = 14;
            if (statusHost != null)
            {
                // Everything that explains a broken connection -- the transport line, the port it
                // actually bound -- lives inside the foldout, so collapsing it hides the problem
                // while the row still reads as healthy. This line rides the header instead.
                _alertLabel = new Label();
                _alertLabel.style.fontSize = 12;
                _alertLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
                _alertLabel.style.marginRight = 8;
                _alertLabel.style.flexShrink = 1;
                _alertLabel.style.color = new Color(1f, 0.75f, 0.3f);
                _alertLabel.style.display = DisplayStyle.None;
                _alertLabel.Ellipsize();
                statusHost.Add(_alertLabel);

                _statusLabel.style.fontSize = 13;
                _statusLabel.style.unityTextAlign = TextAnchor.MiddleRight;
                _statusLabel.style.marginRight = 0;
                _statusLabel.Ellipsize();
                statusHost.style.marginRight = 0;
                statusHost.Add(_statusLabel);

                var spacer = new VisualElement();
                spacer.style.flexGrow = 1;
                titleRow.Add(spacer);
            }
            else
            {
                _statusLabel.style.flexGrow = 1;
                _statusLabel.style.marginLeft = 10;
                _statusLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
                titleRow.Add(_statusLabel);
            }

            _versionLabel = new Label();
            _versionLabel.style.fontSize = 13;
            _versionLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
            _versionLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            titleRow.Add(_versionLabel);

            Refresh();
        }

        public void Refresh()
        {
            RefreshVersion();
            RefreshStatus();
        }

        // Only states the user has to act on: a transport that is not carrying traffic, and a port
        // that is not the configured one -- clients written for the configured port reach nothing
        // and the failure looks like a dead server rather than a moved one.
        private void RefreshAlert()
        {
            if (_alertLabel == null)
                return;

            var message = DescribeProblem();
            _alertLabel.text = message ?? string.Empty;
            _alertLabel.tooltip = message;
            _alertLabel.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private string DescribeProblem()
        {
            if (_server == null)
                return null;

            var brokerMode = _settings != null && _settings.MCPBrokerModeEnabled;
            return DescribeProblem(
                _server.IsRunning,
                _server.IsTransitioning,
                brokerMode,
                brokerMode && MCPBrokerProcessManager.IsRunning(out _, out _),
                MCPBrokerProcessManager.LastError,
                _settings?.MCPServerPort ?? 0,
                _server.Port);
        }

        // Kept free of the editor state it describes so the wording and the precedence between the
        // two problems can be tested without a running server, the way ResolveIsCompiling is.
        internal static string DescribeProblem(
            bool isRunning,
            bool isTransitioning,
            bool brokerMode,
            bool brokerRunning,
            string brokerError,
            int configuredPort,
            int boundPort)
        {
            // Stopped and Connecting already read on the right; repeating them here is noise.
            if (!isRunning || isTransitioning)
                return null;

            // A broker that is not up outranks a moved port: nothing is being served at all.
            if (brokerMode && !brokerRunning)
            {
                return string.IsNullOrEmpty(brokerError)
                    ? "⚠ Broker not running"
                    : "⚠ Broker not running - " + brokerError;
            }

            if (configuredPort > 0 && boundPort != configuredPort)
                return "⚠ Port " + configuredPort + " was in use - serving on " + boundPort;

            return null;
        }

        public void RefreshVersion()
        {
            if (_versionLabel != null)
                _versionLabel.text = $"v{UpdateChecker.CurrentState.CurrentVersion ?? PackageVersion.Current}";
        }

        public void RefreshStatus()
        {
            RefreshAlert();

            if (_statusLabel == null)
                return;

            // The Connect button already reads Connecting..., so anything here just repeats it.
            if (_server?.IsTransitioning == true)
            {
                _statusLabel.text = string.Empty;
                _statusLabel.tooltip = null;
                return;
            }

            if (_server?.IsRunning == true)
            {
                var attached = _server.IsAttachedToExistingTransport;
                // Pinned, like the client configs: a URL copied out of this tooltip has to reach
                // this project rather than whichever editor holds the port.
                var url = ClientConfigPanel.BuildServerUrl(_server.Port);

                var rawProfile = _settings?.MCPToolExportProfile ?? "core";
                var profileDisplay = string.IsNullOrEmpty(rawProfile) ? "Core" : char.ToUpperInvariant(rawProfile[0]) + rawProfile.Substring(1);
                var isCustom = _settings != null && _settings.IsProfileConfigured(rawProfile);
                var customTag = isCustom ? " (Custom)" : "";

                // Port already has its own field right below and 127.0.0.1 is fixed, so the
                // URL in status would just repeat it — keep it in the tooltip so this line doesn't get cut.
                _statusLabel.text = $"{(attached ? "Attached" : "Running")} · {profileDisplay}{customTag}";
                _statusLabel.tooltip = attached
                    ? $"Attached to an existing listener on {url}"
                    : $"Running on {url}";
                _statusLabel.style.color = new Color(0.4f, 1f, 0.4f);
            }
            else
            {
                _statusLabel.text = "Stopped";
                _statusLabel.tooltip = null;
                _statusLabel.style.color = new Color(0.9f, 0.35f, 0.35f);
            }
        }
    }
}
