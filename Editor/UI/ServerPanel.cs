// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.Settings;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace KitWright.Editor.MCP.Server
{
    internal sealed class ServerPanel : IMCPWindowPanel
    {
        private readonly SettingsController _settingsController;
        private readonly MCPServerService _mcpServer;

        private VisualElement _container;
        private HeaderStatusPanel _headerStatusPanel;
        private UpdatePanel _updatePanel;
        private RecentActivityPanel _activityPanel;

        public ServerPanel(
            SettingsController settingsController,
            MCPServerService mcpServer)
        {
            _settingsController = settingsController;
            _mcpServer = mcpServer;
        }

        public void Build(VisualElement container)
        {
            _container = container;

            UpdateChecker.StateChanged -= OnUpdateStateChanged;
            UpdateChecker.StateChanged += OnUpdateStateChanged;

            _mcpServer.InteractionLog.OnEntryAdded -= OnLogEntryAdded;
            _mcpServer.InteractionLog.OnEntryAdded += OnLogEntryAdded;

            BuildUI();
            UpdateChecker.MaybeCheckForUpdatesInBackground();
        }

        private void BuildUI()
        {
            DisposePanels();

            _container.Clear();

            var connectionSection = CreateSection();
            var connectionFoldout = new Foldout { text = "Connection", value = true }.Persist("Connection");
            var connToggle = connectionFoldout.Q<Toggle>();
            var connToggleLabel = connToggle?.Q<Label>();
            if (connToggleLabel != null)
            {
                connToggleLabel.style.fontSize = 12;
                connToggleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                connToggleLabel.style.color = MCPPalette.HeadingBlue;
                connToggleLabel.style.flexGrow = 1;
            }

            _headerStatusPanel = new HeaderStatusPanel(_settingsController, _mcpServer);
            _headerStatusPanel.AddTo(_container, connToggle);

            _updatePanel = new UpdatePanel();
            _updatePanel.AddTo(_container);

            connectionSection.Add(connectionFoldout);
            new ServerControlsPanel(
                    _settingsController,
                    _mcpServer,
                    () => _headerStatusPanel?.RefreshStatus(),
                    BuildUI)
                .AddTo(connectionFoldout);
            _container.Add(connectionSection);

            var clientSection = CreateSection();
            new ClientConfigPanel(
                    _settingsController,
                    _mcpServer,
                    BuildUI)
                .AddTo(clientSection);
            _container.Add(clientSection);

            _activityPanel = new RecentActivityPanel(_mcpServer);
            _activityPanel.AddTo(_container);
        }

        private static VisualElement CreateSection()
        {
            var section = new VisualElement().Card().Padding(8, 10, 10, 10);
            section.style.minWidth = 0;
            section.style.marginBottom = 10;
            return section;
        }

        public void Dispose()
        {
            if (_mcpServer?.InteractionLog != null)
                _mcpServer.InteractionLog.OnEntryAdded -= OnLogEntryAdded;

            UpdateChecker.StateChanged -= OnUpdateStateChanged;
            DisposePanels();
        }

        private void DisposePanels()
        {
            _activityPanel?.Dispose();
            _activityPanel = null;
        }

        private void OnUpdateStateChanged()
        {
            EditorApplication.delayCall += () =>
            {
                _headerStatusPanel?.RefreshVersion();
                _updatePanel?.Refresh();
            };
        }

        private void OnLogEntryAdded(MCPLogEntry entry)
        {
            _activityPanel?.OnEntryAdded(entry);
        }
    }
}
