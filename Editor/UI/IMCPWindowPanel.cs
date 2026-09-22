// Copyright (C) KitWright. All rights reserved.

using System;
using UnityEngine.UIElements;

namespace KitWright.Editor.MCP.Server
{
    internal interface IMCPWindowPanel : IDisposable
    {
        void Build(VisualElement container);
    }
}
