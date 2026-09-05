// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace KitWright.Editor.MCP.Server
{
    /// <summary>
    /// A labelled iOS-style on/off switch (green on, red off) used in place of the default
    /// Unity checkbox across the MCP window tabs.
    /// </summary>
    internal sealed class MCPSwitchToggle : VisualElement
    {
        // Shared with the Tool Exposure list, which builds the same switch but drives its state
        // from outside. Keeping the look in one place is what stops the two drifting apart.
        internal static readonly Color OnTrack = MCPPalette.AccentGreen;
        internal static readonly Color OffTrack = new Color(0.62f, 0.26f, 0.26f);
        internal static readonly List<TimeValue> Slide = new List<TimeValue> { new TimeValue(0.1f, TimeUnit.Second) };
        internal static readonly List<TimeValue> Instant = new List<TimeValue> { new TimeValue(0, TimeUnit.Second) };

        internal const int KnobLeftOn = 18;
        internal const int KnobLeftOff = 2;

        private static readonly List<StylePropertyName> TrackTransition = new List<StylePropertyName> { "background-color" };
        private static readonly List<StylePropertyName> KnobTransition = new List<StylePropertyName> { "left" };
        private static readonly List<EasingFunction> KnobEasing = new List<EasingFunction> { new EasingFunction(EasingMode.EaseOutCubic) };

        /// <summary>Track with its knob as the first child. Colour, knob side and duration are the caller's.</summary>
        internal static VisualElement CreateTrack()
        {
            var track = new VisualElement();
            track.style.width = 34;
            track.style.height = 18;
            track.style.flexShrink = 0;
            track.style.backgroundColor = OffTrack;
            track.Rounded(9);
            track.style.transitionProperty = TrackTransition;
            track.style.transitionDuration = Instant;

            var knob = new VisualElement();
            knob.style.position = Position.Absolute;
            knob.style.width = 14;
            knob.style.height = 14;
            knob.style.top = 2;
            knob.style.left = KnobLeftOff;
            knob.style.backgroundColor = Color.white;
            knob.Rounded(7);
            knob.style.transitionProperty = KnobTransition;
            knob.style.transitionDuration = Instant;
            knob.style.transitionTimingFunction = KnobEasing;
            track.Add(knob);

            return track;
        }

        private readonly Label _label;
        private readonly VisualElement _track;
        private readonly VisualElement _knob;
        private Action<bool> _onChanged;
        private bool _value;

        public MCPSwitchToggle(string label)
        {
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;

            _label = new Label(label);
            _label.style.flexGrow = 1;
            _label.style.fontSize = 13;
            _label.style.color = new Color(0.85f, 0.85f, 0.85f);
            Add(_label);

            _track = CreateTrack();
            _knob = _track[0];

            // Every change here is a click, so it always slides.
            _track.style.transitionDuration = Slide;
            _knob.style.transitionDuration = Slide;
            Add(_track);

            RegisterCallback<ClickEvent>(_ =>
            {
                _value = !_value;
                UpdateVisual();
                _onChanged?.Invoke(_value);
            });

            UpdateVisual();
        }

        public bool value => _value;

        public void SetValueWithoutNotify(bool newValue)
        {
            _value = newValue;
            UpdateVisual();
        }

        public void RegisterValueChangedCallback(Action<bool> callback)
        {
            _onChanged = callback;
        }

        private void UpdateVisual()
        {
            _track.style.backgroundColor = _value ? OnTrack : OffTrack;
            _knob.style.left = _value ? KnobLeftOn : KnobLeftOff;
        }
    }
}
