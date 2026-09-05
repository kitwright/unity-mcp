// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace KitWright.Editor.MCP.Server
{
    internal sealed class RecentActivityPanel : IDisposable
    {
        private readonly MCPServerService _server;
        private readonly List<Texture2D> _previewTextures = new List<Texture2D>();
        private readonly Dictionary<string, Button> _filterButtons = new Dictionary<string, Button>();
        private readonly Dictionary<string, Label> _filterLabels = new Dictionary<string, Label>();
        private ScrollView _scrollView;
        private Label _totalTokensLabel;
        private readonly HashSet<string> _enabled = new HashSet<string> { "Success", "Interrupted", "Error" };
        private const string ScrollOffsetKey = "KitWright.MCP.RecentActivity.ScrollY";
        private const float BottomThreshold = 20f;
        private bool _stickToBottom = true;

        public RecentActivityPanel(MCPServerService server)
        {
            _server = server;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        // Leaving play mode resets the GPU context, dropping the textures LoadImage uploaded, so the
        // previews go black until something rebuilds them. Re-render from the on-disk PNGs (which are
        // intact) to re-upload — this is what a manual editor refresh does.
        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Rebuild synchronously: RenderEntries uploads fresh textures from the on-disk PNGs, so it
            // replaces the context-lost (black) ones immediately. A delayCall waits for the next editor
            // tick, which on an idle editor can be a few hundred ms -- long enough to see a black flash.
            if (change == PlayModeStateChange.EnteredEditMode || change == PlayModeStateChange.EnteredPlayMode)
                RenderEntries();
        }

        public void AddTo(VisualElement parent)
        {
            ClearPreviewTextures();

            var (section, foldout) = MCPSection.Create("Recent Activity", "RecentActivity", labelColor: new Color(0.75f, 0.75f, 0.75f));
            parent.Add(section);

            foldout.Add(BuildFilterBar());

            _scrollView = new ScrollView(ScrollViewMode.Vertical);
            _scrollView.style.backgroundColor = new Color(0.14f, 0.14f, 0.14f);
            _scrollView.Rounded(4);
            _scrollView.Padding(4, 6, 4, 6);
            // Persist scroll position so it survives the panel rebuilds caused by domain
            // reload (play mode with reload enabled) and play-mode transitions.
            _scrollView.verticalScroller.valueChanged += v => SessionState.SetFloat(ScrollOffsetKey, v);

            // The window wraps every panel in its own ScrollView whose content grows with its
            // children, so there is no leftover height for flex-grow to claim: left alone this
            // ScrollView stretches to fit every entry, never scrolls, and hands the scrolling to
            // the window. A max height against the window is what makes it scroll internally.
            _scrollView.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                var outer = _scrollView.GetFirstAncestorOfType<ScrollView>();
                if (outer != null)
                    outer.contentViewport.RegisterCallback<GeometryChangedEvent>(evt => ApplyMaxHeight(evt.newRect.height));
            });

            // Stick-to-bottom is re-evaluated only on user-driven scrolling, never on the
            // programmatic offset changes that adding a row or resizing the panel triggers.
            // Deferred one frame: wheel and drag events fire before the ScrollView applies
            // the new offset, so measuring immediately reads the pre-scroll position.
            _scrollView.RegisterCallback<WheelEvent>(_ => _scrollView.schedule.Execute(UpdateStickToBottom));
            _scrollView.verticalScroller.RegisterCallback<PointerUpEvent>(_ => _scrollView.schedule.Execute(UpdateStickToBottom));
            foldout.Add(_scrollView);

            // Deferred: building ~100 entry cards synchronously adds ~80ms to CreateGUI after
            // every domain reload, which the user sees as a blank-window flash on Play.
            _scrollView.schedule.Execute(RenderEntries);
        }

        // Measured off the window's own ScrollView viewport: that is the only element here whose
        // height is the visible area rather than the content it holds.
        private void ApplyMaxHeight(float viewportHeight = 0f)
        {
            if (viewportHeight <= 0)
                viewportHeight = _scrollView?.GetFirstAncestorOfType<ScrollView>()?.contentViewport.layout.height ?? 0f;

            if (_scrollView == null || viewportHeight <= 0)
                return;

            _scrollView.style.maxHeight = Mathf.Max(160f, viewportHeight - 260f);
        }

        private void UpdateStickToBottom()
        {
            if (_scrollView == null)
                return;

            var maxScroll = _scrollView.contentContainer.layout.height - _scrollView.layout.height;
            _stickToBottom = maxScroll <= 0 || _scrollView.scrollOffset.y >= maxScroll - BottomThreshold;
        }

        private VisualElement BuildFilterBar()
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.marginBottom = 6;

            _filterButtons.Clear();
            _filterLabels.Clear();
            AddFilterButton(bar, "Success", "console.infoicon.sml");
            AddFilterButton(bar, "Interrupted", "console.warnicon.sml");
            AddFilterButton(bar, "Error", "console.erroricon.sml");

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            bar.Add(spacer);

            _totalTokensLabel = new Label();
            _totalTokensLabel.style.fontSize = 11;
            _totalTokensLabel.style.color = new Color(0.55f, 0.55f, 0.62f);
            _totalTokensLabel.style.marginRight = 6;
            _totalTokensLabel.tooltip = "Rough total response size this session (chars / 4). Not a real tokenizer.";
            bar.Add(_totalTokensLabel);

            var clearButton = new Button(() =>
            {
                _server.InteractionLog.Clear();
                ClearPreviewTextures();
                _scrollView?.contentContainer.Clear();
                RefreshFilterCounts();
            });
            clearButton.text = "Clear";
            clearButton.style.height = 22;
            clearButton.style.width = 50;
            clearButton.Margin(0, 0, 0, 4);
            bar.Add(clearButton);

            RefreshFilterCounts();
            return bar;
        }

        private void AddFilterButton(VisualElement bar, string filter, string iconName)
        {
            var button = new Button(() =>
            {
                if (!_enabled.Remove(filter))
                    _enabled.Add(filter);
                UpdateFilterButtonStyles();
                RenderEntries();
            });
            button.style.flexDirection = FlexDirection.Row;
            button.style.alignItems = Align.Center;
            button.style.height = 22;
            button.Margin(0, 0, 0, _filterButtons.Count == 0 ? 0 : 4);
            button.style.paddingLeft = 8;
            button.style.paddingRight = 8;
            button.Rounded(4);

            var icon = LoadConsoleIcon(iconName);
            if (icon != null)
            {
                var iconEl = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit };
                iconEl.style.width = 16;
                iconEl.style.height = 16;
                iconEl.style.marginRight = 4;
                iconEl.style.flexShrink = 0;
                button.Add(iconEl);
            }

            var label = new Label();
            label.style.fontSize = 11;
            button.Add(label);

            _filterButtons[filter] = button;
            _filterLabels[filter] = label;
            bar.Add(button);
        }

        private static Texture2D LoadConsoleIcon(string iconName)
        {
            if (string.IsNullOrEmpty(iconName))
                return null;

            var content = EditorGUIUtility.IconContent(iconName);
            return content?.image as Texture2D;
        }

        private void RefreshFilterCounts()
        {
            if (_filterButtons.Count == 0)
                return;

            var entries = _server.InteractionLog.GetEntries();
            var success = 0;
            var interrupted = 0;
            var error = 0;
            long totalTokens = 0;
            foreach (var entry in entries)
            {
                totalTokens += entry.EstimatedTokens;
                switch (entry.Status)
                {
                    case MCPToolCallStatus.Success: success++; break;
                    case MCPToolCallStatus.Interrupted: interrupted++; break;
                    default: error++; break;
                }
            }

            SetFilterText("Success", success.ToString());
            SetFilterText("Interrupted", interrupted.ToString());
            SetFilterText("Error", error.ToString());
            if (_totalTokensLabel != null)
                _totalTokensLabel.text = totalTokens > 0 ? $"Σ ~{totalTokens:N0} tok" : "";
            UpdateFilterButtonStyles();
        }

        private void SetFilterText(string filter, string text)
        {
            if (_filterLabels.TryGetValue(filter, out var label))
                label.text = text;
        }

        private void UpdateFilterButtonStyles()
        {
            foreach (var entry in _filterButtons)
            {
                var isActive = _enabled.Contains(entry.Key);
                var accent = FilterAccentColor(entry.Key);
                entry.Value.style.backgroundColor = isActive ? accent : MCPPalette.Surface;

                if (_filterLabels.TryGetValue(entry.Key, out var label))
                {
                    label.style.color = isActive ? Color.white : MCPPalette.TextMuted;
                    label.style.unityFontStyleAndWeight = isActive ? FontStyle.Bold : FontStyle.Normal;
                }
            }
        }

        private static Color FilterAccentColor(string filter)
        {
            switch (filter)
            {
                case "Success": return new Color(0.3f, 0.6f, 0.38f);
                case "Interrupted": return new Color(0.78f, 0.56f, 0.22f);
                case "Error": return new Color(0.75f, 0.32f, 0.32f);
                default: return new Color(0.24f, 0.42f, 0.58f);
            }
        }

        private bool PassesFilter(MCPToolCallStatus status)
        {
            switch (status)
            {
                case MCPToolCallStatus.Success: return _enabled.Contains("Success");
                case MCPToolCallStatus.Interrupted: return _enabled.Contains("Interrupted");
                default: return _enabled.Contains("Error");
            }
        }

        private void RenderEntries()
        {
            if (_scrollView == null)
                return;

            ClearPreviewTextures();
            _scrollView.contentContainer.Clear();

            // GetEntries is newest-first; the cards read oldest-first so a live append lands at
            // the bottom, where stick-to-bottom and ScrollToBottom expect the newest one.
            var entries = _server.InteractionLog.GetEntries();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (PassesFilter(entries[i].Status))
                    AddRow(entries[i]);
            }

            RefreshFilterCounts();
            ApplyMaxHeight();

            // Deferred so the cards have a layout: restore the persisted position, or the bottom
            // when there is none.
            var scroll = _scrollView;
            scroll.schedule.Execute(() =>
            {
                if (_scrollView != scroll)
                    return;

                var saved = SessionState.GetFloat(ScrollOffsetKey, -1f);
                scroll.scrollOffset = new Vector2(0, saved >= 0 ? saved : float.MaxValue);
                UpdateStickToBottom();
            }).ExecuteLater(32);
        }

        public void OnEntryAdded(MCPLogEntry entry)
        {
            EditorApplication.delayCall += () =>
            {
                if (_scrollView == null)
                    return;

                RefreshFilterCounts();

                if (!PassesFilter(entry.Status))
                    return;

                AddRow(entry);

                // Stick-to-bottom: only auto-scroll if user was already at the bottom.
                if (_stickToBottom)
                    ScrollToBottom();
            };
        }

        // Assigning float.MaxValue is clamped by the scroller's high value, which is still the
        // pre-add maximum until the new card has a layout — and a card's height is not final on
        // the next frame either: an image preview and a wrapped summary settle later. So scroll
        // to the last card itself (ScrollTo measures the child) and repeat over a few frames.
        private void ScrollToBottom()
        {
            var scroll = _scrollView;
            for (var delay = 0; delay <= 64; delay += 32)
            {
                scroll.schedule.Execute(() =>
                {
                    var content = scroll.contentContainer;
                    if (_scrollView == scroll && content.childCount > 0)
                        scroll.ScrollTo(content[content.childCount - 1]);
                }).ExecuteLater(delay);
            }
        }

        public void Dispose()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            ClearPreviewTextures();
            _filterButtons.Clear();
            _filterLabels.Clear();
            _scrollView = null;
        }

        private void AddRow(MCPLogEntry entry)
        {
            var badgeText = GetBadgeText(entry.Status);
            var accentColor = GetAccentColor(entry.Status);

            var card = new VisualElement();
            var cardBg = new Color(0.19f, 0.19f, 0.19f);
            var cardHoverBg = new Color(0.23f, 0.23f, 0.24f);
            card.style.backgroundColor = cardBg;
            card.Rounded(4);
            card.style.borderLeftWidth = 3;
            card.style.borderLeftColor = accentColor;
            card.Padding(5, 8, 5, 8);
            card.style.marginBottom = 3;
            card.style.transitionProperty = new List<StylePropertyName> { "background-color" };
            card.style.transitionDuration = new List<TimeValue> { new TimeValue(0.1f, TimeUnit.Second) };
            card.RegisterCallback<MouseEnterEvent>(_ => card.style.backgroundColor = cardHoverBg);
            card.RegisterCallback<MouseLeaveEvent>(_ => card.style.backgroundColor = cardBg);
            card.tooltip = "Double-click to copy this log entry";

            var topRow = new VisualElement();
            topRow.style.flexDirection = FlexDirection.Row;
            topRow.style.alignItems = Align.Center;

            var toolLabel = new Label(entry.ToolName);
            toolLabel.style.fontSize = 13;
            toolLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            toolLabel.style.color = new Color(0.88f, 0.88f, 0.88f);
            toolLabel.style.flexGrow = 1;
            topRow.Add(toolLabel);

            card.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button == 0 && evt.clickCount == 2)
                {
                    CopyEntryToClipboard(entry);
                    FlashCopied(card, cardBg, toolLabel);
                    evt.StopPropagation();
                }
            });

            if (entry.EstimatedTokens > 0)
            {
                var tokenLabel = new Label($"~{entry.EstimatedTokens:N0} tok");
                tokenLabel.style.fontSize = 10;
                tokenLabel.style.color = new Color(0.55f, 0.55f, 0.62f);
                tokenLabel.style.marginRight = 6;
                tokenLabel.style.flexShrink = 0;
                tokenLabel.tooltip = "Rough response size (chars / 4). Not a real tokenizer.";
                topRow.Add(tokenLabel);
            }

            var statusIconTex = LoadConsoleIcon(GetStatusIconName(entry.Status));
            if (statusIconTex != null)
            {
                var statusIcon = new Image { image = statusIconTex, scaleMode = ScaleMode.ScaleToFit };
                statusIcon.style.width = 16;
                statusIcon.style.height = 16;
                statusIcon.style.flexShrink = 0;
                statusIcon.tooltip = badgeText;
                topRow.Add(statusIcon);
            }
            else
            {
                var badge = new Label(badgeText);
                badge.style.fontSize = 11;
                badge.style.unityFontStyleAndWeight = FontStyle.Bold;
                badge.style.color = Color.white;
                badge.style.backgroundColor = accentColor;
                badge.Rounded(3);
                badge.Padding(1, 5, 1, 5);
                badge.style.unityTextAlign = TextAnchor.MiddleCenter;
                topRow.Add(badge);
            }

            card.Add(topRow);

            var timePrefix = $"[{entry.Timestamp:HH:mm:ss}] ";
            var summaryText = string.IsNullOrEmpty(entry.ResultSummary)
                ? timePrefix.TrimEnd()
                : timePrefix + entry.ResultSummary;

            var summaryLabel = new Label(summaryText);
            summaryLabel.style.fontSize = 13;
            summaryLabel.style.color = MCPPalette.TextDim;
            summaryLabel.style.marginTop = 3;
            summaryLabel.style.whiteSpace = WhiteSpace.Normal;
            summaryLabel.style.overflow = Overflow.Hidden;
            card.Add(summaryLabel);

            if (!string.IsNullOrEmpty(entry.ImageFilePath) &&
                TryCreateImagePreview(entry.ImageFilePath, out var preview))
            {
                card.Add(preview);
            }

            _scrollView?.contentContainer.Add(card);
        }

        private static void CopyEntryToClipboard(MCPLogEntry entry)
        {
            var text = $"[{entry.Timestamp:HH:mm:ss}] {entry.ToolName} ({GetBadgeText(entry.Status)})";
            if (!string.IsNullOrEmpty(entry.ResultSummary))
                text += "\n" + entry.ResultSummary;

            EditorGUIUtility.systemCopyBuffer = text;
        }

        // Brief inline confirmation: green tint + "Copied ✓" right after the tool name, then restore.
        private static void FlashCopied(VisualElement card, Color originalBg, VisualElement toolLabel)
        {
            var flashBg = new Color(0.16f, 0.32f, 0.2f);
            card.style.backgroundColor = flashBg;

            var copiedLabel = new Label("Copied ✓");
            copiedLabel.style.fontSize = 11;
            copiedLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            copiedLabel.style.color = new Color(0.45f, 0.9f, 0.55f);
            copiedLabel.style.marginLeft = 6;
            copiedLabel.style.flexGrow = 1;
            copiedLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            toolLabel.style.flexGrow = 0;
            var parent = toolLabel.parent;
            parent.Insert(parent.IndexOf(toolLabel) + 1, copiedLabel);

            card.schedule.Execute(() =>
            {
                card.style.backgroundColor = originalBg;
                toolLabel.style.flexGrow = 1;
                copiedLabel.RemoveFromHierarchy();
            }).ExecuteLater(1000);
        }

        private static string GetStatusIconName(MCPToolCallStatus status)
        {
            switch (status)
            {
                case MCPToolCallStatus.Success:
                    return "console.infoicon.sml";
                case MCPToolCallStatus.Interrupted:
                    return "console.warnicon.sml";
                default:
                    return "console.erroricon.sml";
            }
        }

        internal static string GetBadgeText(MCPToolCallStatus status)
        {
            switch (status)
            {
                case MCPToolCallStatus.Success:
                    return "OK";
                case MCPToolCallStatus.Interrupted:
                    return "INT";
                default:
                    return "ERR";
            }
        }

        private static Color GetAccentColor(MCPToolCallStatus status)
        {
            switch (status)
            {
                case MCPToolCallStatus.Success:
                    return new Color(0.3f, 0.75f, 0.4f);
                case MCPToolCallStatus.Interrupted:
                    return new Color(0.95f, 0.68f, 0.25f);
                default:
                    return new Color(0.9f, 0.35f, 0.35f);
            }
        }

        private bool TryCreateImagePreview(string imageFilePath, out Image preview)
        {
            preview = null;
            if (string.IsNullOrEmpty(imageFilePath) || !File.Exists(imageFilePath))
                return false;

            preview = new Image { scaleMode = ScaleMode.ScaleToFit };
            preview.style.height = 220;
            preview.style.marginTop = 6;
            preview.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f);
            preview.Rounded(3);
            preview.tooltip = "Double-click to open in the default image viewer";
            preview.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button == 0 && evt.clickCount == 2)
                {
                    OpenImageExternally(imageFilePath);
                    evt.StopPropagation();
                }
            });

            // Decode after the window has painted: synchronous PNG decode for every entry adds
            // ~100ms to CreateGUI after each domain reload, which shows as a blank-window flash.
            var target = preview;
            preview.schedule.Execute(() =>
            {
                try
                {
                    var bytes = File.ReadAllBytes(imageFilePath);
                    // HideAndDontSave: without it Unity destroys editor-created textures on
                    // play-mode enter / scene load, leaving the preview black.
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    if (!texture.LoadImage(bytes))
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                        target.RemoveFromHierarchy();
                        return;
                    }

                    _previewTextures.Add(texture);
                    target.image = texture;
                }
                catch
                {
                    target.RemoveFromHierarchy();
                }
            });
            return true;
        }

        private static void OpenImageExternally(string imageFilePath)
        {
            try
            {
                EditorUtility.OpenWithDefaultApp(Path.GetFullPath(imageFilePath));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KitWright MCP] Failed to open image: {ex.Message}");
            }
        }

        private void ClearPreviewTextures()
        {
            foreach (var texture in _previewTextures)
            {
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
            }

            _previewTextures.Clear();
        }
    }
}
