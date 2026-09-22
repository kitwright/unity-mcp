// Copyright (C) KitWright. All rights reserved.
using System.Collections.Generic;

namespace KitWright.Editor.Api.Models
{
    internal class ToolDefinition
    {
        public string name;
        public string description;
        public ToolParametersDef parameters;
        public bool readOnly;
    }

    internal class ToolParametersDef
    {
        public Dictionary<string, ToolPropertyDef> properties = new Dictionary<string, ToolPropertyDef>();
        public List<string> required = new List<string>();
    }

    internal class ToolPropertyDef
    {
        public string type;
        public string description;
        public List<string> @enum;
    }
}
