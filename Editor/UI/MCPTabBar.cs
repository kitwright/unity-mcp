// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace KitWright.Editor.MCP.Server
{
    /// <summary>
    /// A row of full-width tab buttons with an active accent bar, hover feedback, and a press
    /// scale animation. Keyed by an arbitrary type (usually a window's Tab enum). Icons are
    /// optional per tab. Clicking a tab fires onSelect and repaints the active state.
    /// </summary>
    internal sealed class MCPTabBar<TKey>
    {
        private static readonly Color ActiveTabColor = MCPPalette.Surface;
        private static readonly Color InactiveTabColor = new Color(0.145f, 0.145f, 0.145f);
        private static readonly Color HoverTabColor = new Color(0.18f, 0.18f, 0.185f);
        private static readonly Color AccentColor = new Color(0.30f, 0.55f, 0.85f);
        private static readonly Color DividerColor = new Color(0.07f, 0.07f, 0.07f);

        public VisualElement Root { get; }

        private static readonly EqualityComparer<TKey> Eq = EqualityComparer<TKey>.Default;

        private readonly Action<TKey> _onSelect;
        private readonly Dictionary<TKey, (VisualElement btn, Label label, VisualElement accent, Image icon)> _tabs =
            new Dictionary<TKey, (VisualElement, Label, VisualElement, Image)>();
        private TKey _active;

        public MCPTabBar(IReadOnlyList<(TKey key, string label, string icon)> tabs, Action<TKey> onSelect)
        {
            _onSelect = onSelect;

            Root = new VisualElement();
            Root.style.flexDirection = FlexDirection.Row;
            Root.style.flexShrink = 0;
            Root.style.backgroundColor = new Color(0.115f, 0.115f, 0.115f);
            Root.style.borderBottomWidth = 1;
            Root.style.borderBottomColor = DividerColor;

            for (var i = 0; i < tabs.Count; i++)
                Root.Add(CreateTabButton(tabs[i].key, tabs[i].label, tabs[i].icon, i < tabs.Count - 1));
        }

        public void Select(TKey key)
        {
            _active = key;
            foreach (var entry in _tabs)
            {
                var isActive = Eq.Equals(entry.Key, key);
                var (btn, label, accent, _) = entry.Value;
                btn.style.backgroundColor = isActive ? ActiveTabColor : InactiveTabColor;
                label.style.color = isActive ? Color.white : new Color(0.72f, 0.72f, 0.72f);
                label.style.unityFontStyleAndWeight = isActive ? FontStyle.Bold : FontStyle.Normal;
                accent.style.opacity = isActive ? 1f : 0f;
            }
            _onSelect?.Invoke(key);
        }

        private VisualElement CreateTabButton(TKey key, string label, string iconName, bool showDivider)
        {
            // A VisualElement (not Button) so the label centers cleanly and an accent bar can sit
            // flush at the bottom edge; Button's default padding/min-height clips small text.
            var tabEl = new VisualElement();
            tabEl.style.flexGrow = 1;
            tabEl.style.flexBasis = 0;
            tabEl.style.height = 32;
            tabEl.style.flexDirection = FlexDirection.Row;
            tabEl.style.justifyContent = Justify.Center;
            tabEl.style.alignItems = Align.Center;
            tabEl.style.backgroundColor = InactiveTabColor;
            if (showDivider)
            {
                tabEl.style.borderRightWidth = 1;
                tabEl.style.borderRightColor = DividerColor;
            }
            tabEl.style.transitionProperty = new List<StylePropertyName> { "background-color" };
            tabEl.style.transitionDuration = new List<TimeValue> { new TimeValue(0.12f, TimeUnit.Second) };
            tabEl.style.transitionTimingFunction = new List<EasingFunction> { new EasingFunction(EasingMode.EaseOutCubic) };

            var iconTex = string.IsNullOrEmpty(iconName) ? null : EditorGUIUtility.IconContent(iconName)?.image as Texture2D;
            Image iconEl = null;
            if (iconTex != null)
            {
                iconEl = new Image { image = iconTex, scaleMode = ScaleMode.ScaleToFit };
                iconEl.style.width = 14;
                iconEl.style.height = 14;
                iconEl.style.marginRight = 5;
                tabEl.Add(iconEl);
            }

            var text = new Label(label);
            text.style.fontSize = 13;
            text.style.unityTextAlign = TextAnchor.MiddleCenter;
            text.style.color = new Color(0.72f, 0.72f, 0.72f);
            text.style.transitionProperty = new List<StylePropertyName> { "color", "scale" };
            text.style.transitionDuration = new List<TimeValue>
            {
                new TimeValue(0.12f, TimeUnit.Second),
                new TimeValue(0.08f, TimeUnit.Second)
            };
            text.style.transitionTimingFunction = new List<EasingFunction> { new EasingFunction(EasingMode.EaseOutCubic) };
            tabEl.Add(text);

            var accent = new VisualElement();
            accent.style.position = Position.Absolute;
            accent.style.left = 0;
            accent.style.right = 0;
            accent.style.bottom = 0;
            accent.style.height = 2;
            accent.style.backgroundColor = AccentColor;
            accent.style.opacity = 0f;
            accent.style.transitionProperty = new List<StylePropertyName> { "opacity" };
            accent.style.transitionDuration = new List<TimeValue> { new TimeValue(0.12f, TimeUnit.Second) };
            tabEl.Add(accent);

            tabEl.RegisterCallback<ClickEvent>(_ => Select(key));
            tabEl.RegisterCallback<MouseEnterEvent>(_ =>
            {
                if (!Eq.Equals(key, _active))
                    tabEl.style.backgroundColor = HoverTabColor;
            });
            tabEl.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                if (!Eq.Equals(key, _active))
                    tabEl.style.backgroundColor = InactiveTabColor;
                text.style.scale = new StyleScale(new Scale(Vector3.one));
            });
            tabEl.RegisterCallback<PointerDownEvent>(_ =>
                text.style.scale = new StyleScale(new Scale(new Vector3(0.94f, 0.94f, 1f))));
            tabEl.RegisterCallback<PointerUpEvent>(_ =>
                text.style.scale = new StyleScale(new Scale(Vector3.one)));

            _tabs[key] = (tabEl, text, accent, iconEl);
            return tabEl;
        }

        public void TintIcon(TKey key, Color color)
        {
            if (_tabs.TryGetValue(key, out var entry) && entry.icon != null)
                entry.icon.tintColor = color;
        }
    }
}
