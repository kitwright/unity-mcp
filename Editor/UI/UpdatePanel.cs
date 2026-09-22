// Copyright (C) KitWright. All rights reserved.

using UnityEngine;
using UnityEngine.UIElements;

namespace KitWright.Editor.MCP.Server
{
    internal sealed class UpdatePanel
    {
        private VisualElement _container;
        private Label _statusLabel;
        private Label _percentLabel;
        private VisualElement _progressTrack;
        private VisualElement _progressFill;

        public void AddTo(VisualElement parent)
        {
            _container = new VisualElement();
            _container.style.display = DisplayStyle.None;
            _container.style.backgroundColor = new Color(0.13f, 0.19f, 0.15f);
            _container.style.borderLeftWidth = 3;
            _container.style.borderLeftColor = MCPPalette.AccentGreen;
            _container.Rounded(4);
            _container.Padding(8, 10, 8, 10);
            _container.style.marginBottom = 10;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 6;
            _container.Add(row);

            _statusLabel = new Label();
            _statusLabel.style.fontSize = 12;
            _statusLabel.style.color = new Color(0.80f, 0.92f, 0.82f);
            _statusLabel.style.whiteSpace = WhiteSpace.Normal;
            _statusLabel.style.flexGrow = 1;
            row.Add(_statusLabel);

            _percentLabel = new Label();
            _percentLabel.style.fontSize = 12;
            _percentLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _percentLabel.style.color = new Color(0.55f, 0.85f, 0.60f);
            _percentLabel.style.marginLeft = 8;
            row.Add(_percentLabel);

            _progressTrack = new VisualElement();
            _progressTrack.style.height = 5;
            _progressTrack.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f);
            _progressTrack.Rounded(3);
            _progressTrack.style.overflow = Overflow.Hidden;
            _container.Add(_progressTrack);

            _progressFill = new VisualElement();
            _progressFill.style.height = Length.Percent(100);
            _progressFill.style.width = Length.Percent(0);
            _progressFill.style.backgroundColor = new Color(0.36f, 0.76f, 0.44f);
            _progressFill.Rounded(3);
            _progressTrack.Add(_progressFill);

            parent.Add(_container);
            Refresh();
        }

        public void Refresh()
        {
            if (_container == null || _statusLabel == null || _progressFill == null)
                return;

            var state = UpdateChecker.CurrentState;
            var show = state.IsUpdating || state.UpdateStarted;
            _container.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show)
                return;

            _statusLabel.text = string.IsNullOrEmpty(state.StatusMessage)
                ? $"Updating to v{state.LatestVersion}..."
                : state.StatusMessage;

            var progress = Mathf.Clamp01(state.Progress);
            _percentLabel.text = $"{Mathf.RoundToInt(progress * 100f)}%";
            _progressFill.style.width = Length.Percent(progress * 100f);
        }
    }
}
