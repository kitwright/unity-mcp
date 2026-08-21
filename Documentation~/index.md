# KitWright MCP for Unity

KitWright MCP for Unity is an open-source MCP server for the Unity Editor.

## Getting Started

1. Install via UPM using the Git URL for this repository
2. Open **Window > KitWright > MCP Window**
3. Start the server from the **Server** tab and use the built-in one-click client configuration
4. Connect your AI client to the endpoint shown in the window (`http://127.0.0.1:8765/` by default)
5. Open the **Tool Exposure** tab to edit the exact tools exposed by `core` or `full`
6. For Claude Code, Cursor, and Codex, use **Configure + Skills** or open the **Skills** tab to install the default `unity-mcp-workflow` skill
7. Open the **Settings** tab to adjust `execute_code` safety defaults or enable debug logging when troubleshooting

## Highlights

- 263 built-in tool functions across 56 modules — see [TOOLS.md](../TOOLS.md) for the full list
- Structured `{success, message, data}` JSON returns with stable `instanceId` fields so agents can chain `by_id` lookups
- `IKitWrightCommand` template for `execute_code` with auto-Undo, structured logs, and a returned changelog of created/modified/destroyed objects
- Default-on `execute_code` safety checks in the **Settings** tab, with per-call override support through the optional `safety_checks` argument
- Loopback-only server with an `Origin` check and a per-project pin in the request path, so a web page cannot POST into the editor and a stale client config cannot reach a sibling project
- Optional client approval (off by default, **Safety** tab): the first connection from an unknown client executable asks Allow/Deny and is remembered per user. Identity is the executable path, so approving a shared runtime such as `node` or `python` covers every script that uses it
- MCP `structuredContent` on tool results: the `{success, message, data}` envelope is returned as structured output alongside text, with `isError` set on failed calls
- HTTP JSON-RPC 2.0 MCP server compatible with Claude Code, Cursor, LM Studio, Windsurf, Codex, VS Code Copilot, and other MCP clients
- Reflection-based tool discovery via `[ToolProvider]`
- One-click local MCP config generation for 19 client targets (Claude Code, Cursor, VS Code, Codex, Windsurf, Cline, Kiro, Trae, Rider, and more)
- Tool exposure editing for `core` and `full` profiles
- Project skills management for supported AI clients, currently installing the default `unity-mcp-workflow` skill
- Integrations tab detecting Hot Reload, Memory Profiler, Addressables, Input System, Timeline, URP, and Test Framework
- Plugin debug logging toggle, off by default
- Persisted MCP server settings in `UserSettings/KitWrightMcpSettings.json`
- Domain reload recovery for the MCP server during Unity recompilation

## Custom Tools

Add a public static class marked with `[ToolProvider("CategoryName")]`, then expose `public static` methods with `[ToolParam]` metadata. Method return types may be `string` (legacy text response) or any object — non-string returns are serialized to JSON via Newtonsoft. Use `KitWright.Editor.Tools.Helpers.Response.Success/Error` for the structured envelope. Tool names are exported in snake_case automatically.

`execute_code` safety checks and the strict filesystem guard are enabled by default in the **Settings** tab. They block obvious destructive snippets, broad `System.IO` writes, raw file streams, and absolute/user/system/traversal paths before compilation. This is a defensive guard, not a complete sandbox; trusted clients can still pass `safety_checks=false` for a single call.

## Requirements

- Unity 2022.3 or later
- `com.unity.nuget.newtonsoft-json`, `com.unity.ugui`, `com.unity.test-framework`
