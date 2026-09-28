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

- 271 built-in tool functions across 57 modules — see [TOOLS.md](../TOOLS.md) for the full list
- Structured `{success, message, data}` JSON returns with stable `instanceId` fields so agents can chain `by_id` lookups
- `IKitWrightCommand` template for `execute_code` with auto-Undo, structured logs, and a returned changelog of created/modified/destroyed objects
- Default-on `execute_code` safety checks, skippable per call through the optional `safety_checks` argument
- Loopback-only server with an `Origin` check and a per-project pin in the request path, so a web page cannot POST into the editor and a stale client config cannot reach a sibling project
- No approval prompts: the first connection from a new client executable is named in the Unity console so you can see what is driving the editor, and never blocks on a dialog
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

`execute_code` runs its safety checks by default. They block obvious destructive snippets, broad `System.IO` writes, raw file streams, and absolute/user/system/traversal paths before compilation. This catches accidents, not intent: `safety_checks` is a tool argument, so any client that can call `execute_code` can pass `safety_checks=false` and skip both the source blocklist and the compiled-assembly guard. It is neither a sandbox nor a security boundary; to keep an agent from passing `safety_checks=false`, require approval for `execute_code` in your MCP client.

## Requirements

- Unity 2022.3 or later
- `com.unity.nuget.newtonsoft-json`, `com.unity.ugui`, `com.unity.test-framework`
