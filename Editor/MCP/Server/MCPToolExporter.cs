// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using KitWright.Editor.Tools;
using KitWright.Editor.Settings;

namespace KitWright.Editor.MCP.Server
{
    /// <summary>
    /// Exports KitWright tool definitions to MCP tool schema format.
    /// </summary>
    internal class MCPToolExporter
    {
        private readonly SettingsController _settings;

        public MCPToolExporter(SettingsController settings)
        {
            _settings = settings;
        }

        public List<Dictionary<string, object>> ExportTools()
        {
            var mcpTools = new List<Dictionary<string, object>>();
            var tools = ToolSchemaBuilder.BuildAll();
            var profile = MCPToolExportPolicy.Parse(_settings?.MCPToolExportProfile);
            var profileKey = MCPToolExportPolicy.ToSettingValue(profile);
            var profileConfigured = _settings?.IsProfileConfigured(profileKey) ?? false;
            var profileTools = _settings?.GetProfileTools(profileKey);
            var compact = _settings?.MCPCompactSchemaEnabled ?? false;

            tools.Sort((left, right) =>
            {
                var leftRank = MCPToolExportPolicy.GetSortRank(left.name, profile);
                var rightRank = MCPToolExportPolicy.GetSortRank(right.name, profile);
                var compareRank = leftRank.CompareTo(rightRank);
                return compareRank != 0
                    ? compareRank
                    : string.Compare(left.name, right.name, StringComparison.OrdinalIgnoreCase);
            });

            PluginDebugLogger.Log($"[KitWright MCP Server] Exporting tools with profile '{MCPToolExportPolicy.ToSettingValue(profile)}'");

            foreach (var tool in tools)
            {
                if (!MCPToolExportPolicy.IsToolAllowed(
                        tool.name,
                        profile,
                        profileConfigured,
                        profileTools))
                    continue;

                var description = compact ? FirstSentence(tool.description) : tool.description;

                var mcpTool = new Dictionary<string, object>
                {
                    ["name"] = tool.name,
                    ["description"] = MCPToolExportPolicy.BuildDescriptionPrefix(tool.name, profile) + description,
                    ["inputSchema"] = ConvertParametersToJsonSchema(tool.parameters, compact)
                };

                // readOnlyHint lets clients skip an approval prompt for tools that cannot change the
                // project. Only the true case is emitted: the MCP default for an absent hint is
                // "not read-only", which is already the safe answer for everything else.
                if (tool.readOnly)
                    mcpTool["annotations"] = new Dictionary<string, object> { ["readOnlyHint"] = true };

                mcpTools.Add(mcpTool);
            }

            return mcpTools;
        }

        private static string FirstSentence(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var end = text.IndexOf(". ", StringComparison.Ordinal);
            if (end < 0)
                return text.TrimEnd().TrimEnd('.');

            return text.Substring(0, end + 1);
        }

        private Dictionary<string, object> ConvertParametersToJsonSchema(
            KitWright.Editor.Api.Models.ToolParametersDef parameters,
            bool compact)
        {
            var properties = new Dictionary<string, object>();

            foreach (var prop in parameters.properties)
            {
                var propertySchema = new Dictionary<string, object>
                {
                    ["type"] = prop.Value.type
                };

                if (!compact)
                    propertySchema["description"] = prop.Value.description;

                if (prop.Value.@enum != null && prop.Value.@enum.Count > 0)
                    propertySchema["enum"] = prop.Value.@enum;

                properties[prop.Key] = propertySchema;
            }

            var schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties
            };

            if (parameters.required != null && parameters.required.Count > 0)
                schema["required"] = parameters.required;

            return schema;
        }
    }
}
