// Copyright (C) KitWright. All rights reserved.

using System.Collections.Generic;

namespace KitWright.Editor.State
{
    internal enum KitWrightState
    {
        Initialized,
        ExecutingFunction
    }

    internal class StateController
    {
        private readonly Stack<KitWrightState> _stateHistory = new Stack<KitWrightState>();
        private KitWrightState _currentState = KitWrightState.Initialized;

        public KitWrightState CurrentState => _currentState;

        public void SetState(KitWrightState state)
        {
            if (_currentState == state) return;

            _stateHistory.Push(_currentState);
            _currentState = state;
        }

        public void ReturnToPreviousState()
        {
            _currentState = _stateHistory.Count > 0
                ? _stateHistory.Pop()
                : KitWrightState.Initialized;
        }

        public void ClearState()
        {
            _stateHistory.Clear();
            _currentState = KitWrightState.Initialized;
        }
    }
}
