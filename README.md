<p align="center">
  <h1 align="center">KitWright MCP for Unity</h1>
  <p align="center">
    <strong>The Most Advanced MCP Server for Unity Editor</strong>
  </p>
  <p align="center">
    <a href="#"><img src="https://img.shields.io/badge/Unity-2022.3%2B-black?logo=unity" alt="Unity 2022.3+"></a>
    <a href="#"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT"></a>
    <a href="#"><img src="https://img.shields.io/badge/MCP-Compatible-green" alt="MCP Compatible"></a>
    <a href="#"><img src="https://img.shields.io/badge/Platform-Editor%20Only-orange" alt="Editor Only"></a>
  </p>
  <p align="center">
    <a href="./README_CN.md">中文</a> | English
  </p>
  <p align="center">
    <img src="./Documentation~/Video_Logo.gif" alt="The Most Advanced MCP Server for Unity" width="60%">
  </p>
</p>

> 💖 If you find this project useful, please consider giving it a Star. It helps more Unity developers discover it and supports ongoing development.

---

KitWright MCP for Unity is an MIT-licensed Unity Editor MCP server that lets AI assistants like Claude Code, Cursor, LM Studio, Windsurf, Codex, and VS Code Copilot operate directly inside your running Unity project.

Describe your game in one sentence — your AI assistant builds it in Unity through KitWright MCP for Unity's 266 built-in tools for scene creation, script generation, runtime validation, input simulation, performance analysis, and editor automation.

> *"Build a snake game with a 10x10 grid, food spawning, score UI, and game-over screen"*
>
> Your AI assistant handles it through KitWright MCP for Unity: creates the scene, generates all scripts, sets up the UI, and configures the game logic — all from a single prompt.

<p align="center">
  <img src="./Documentation~/demo.gif" alt="KitWright MCP for Unity — demo" width="100%">
</p>
<p align="center"><em>Demo — AI searches project prefabs and builds a city scene through MCP tools. <a href="https://github.com/kitwright/unity-mcp/raw/main/Documentation~/demo.mp4">Watch HD MP4</a>.</em></p>

## Quick Start

If you just want to get connected fast, do these three things:

- Install the Unity package from the Git URL
- Open `Window > KitWright > MCP Window` and start the server from the **Server** tab
- Use the built-in one-click client configuration

### 1. Install via UPM (Git URL)

In Unity, go to **Window → Package Manager → + → Add package from git URL**:

```
https://github.com/kitwright/unity-mcp.git
```

> 💡 Before you clone or install, a quick ⭐ on GitHub would be greatly appreciated.

### Optional: Install via OpenUPM

If you want Unity Package Manager to show registry-backed package version history and allow version selection, install from OpenUPM instead of Git.

Using the OpenUPM CLI:

```bash
openupm add com.kitwright.unity.mcp
```

Or add the scoped registry manually in `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "OpenUPM",
      "url": "https://package.openupm.com",
      "scopes": [
        "com.kitwright"
      ]
    }
  ],
  "dependencies": {
    "com.kitwright.unity.mcp": "1.0.0"
  }
}
```

If you installed from a Git URL before, remove the Git dependency first, then install from OpenUPM. Git-installed packages only show the resolved Git version in Unity and do not get the registry-backed Version History list.

### Optional: Install from the Unity Asset Store

Importing the Asset Store package shows a **This Unity Package has Package Manager dependencies** dialog. Choose **Install/Upgrade** — the package needs `com.unity.nuget.newtonsoft-json`, and skipping the prompt leaves the project without it, which stops every KitWright script from compiling.

If you already chose Skip, the package offers to install the dependency for you on the next domain reload; you can also add it yourself from **Window → Package Manager → + → Add package by name**:

```
com.unity.nuget.newtonsoft-json
```

### 2. Start the MCP Server

**Menu: Window → KitWright → MCP Window**, then start the server from the **Server** tab.

The server starts on `http://127.0.0.1:8765/` by default.

**Broker Mode** is the default transport. It runs a tiny local broker with Unity's bundled Mono, keeps the same `127.0.0.1` port for MCP clients, requires no client config change, and holds the connection across Unity script recompiles and Play Mode domain reloads. If the broker cannot start, the server falls back to direct in-process HTTP automatically; you can also turn broker mode off in the **Server** tab to use direct HTTP always.

The window has five tabs: **Server**, **Settings**, **Skills**, **Tool Exposure**, and **Integrations**.

Open the **Tool Exposure** tab if you want to edit the exact tools exposed by `core` or `full`.

Open the **Settings** tab if you need to adjust `execute_code` safety defaults or plugin debug logging.

### 3. Configure Your AI Client

Use the built-in **One-Click MCP Configuration** in the **Server** tab first.

Select your target client, click **Configure**, and the package writes the recommended MCP config entry for you.

For Claude Code, Cursor, and Codex, click **Configure + Skills** to also install the default project MCP workflow skill.

If you want project-specific AI guidance for the current Unity project, open the **Skills** tab to choose supported platforms and install the default `unity-mcp-workflow` skill.

If you prefer to edit config files manually, use the examples below as fallback references:

<details>
<summary>Claude Code / Claude Desktop</summary>

```json
{
  "mcpServers": {
    "kitwright": {
      "type": "http",
      "url": "http://127.0.0.1:8765/"
    }
  }
}
```

</details>

<details>
<summary>Cursor</summary>

```json
{
  "mcpServers": {
    "kitwright": {
      "url": "http://127.0.0.1:8765/"
    }
  }
}
```

</details>

<details>
<summary>LM Studio</summary>

LM Studio is not one of the one-click targets — its `mcp.json` location varies by version and platform. Open **Program > Install > Edit mcp.json** in LM Studio and paste the entry below.

```json
{
  "mcpServers": {
    "kitwright": {
      "url": "http://127.0.0.1:8765/"
    }
  }
}
```

</details>

<details>
<summary>VS Code</summary>

```json
{
  "servers": {
    "kitwright": {
      "type": "http",
      "url": "http://127.0.0.1:8765/"
    }
  }
}
```

</details>

<details>
<summary>Trae</summary>

```json
{
  "mcpServers": {
    "kitwright": {
      "url": "http://127.0.0.1:8765/"
    }
  }
}
```

</details>

<details>
<summary>Kiro</summary>

```json
{
  "mcpServers": {
    "kitwright": {
      "type": "http",
      "url": "http://127.0.0.1:8765/"
    }
  }
}
```

</details>

<details>
<summary>Codex</summary>

```toml
[mcp_servers.kitwright]
url = "http://127.0.0.1:8765/"
```

</details>

<details>
<summary>Windsurf</summary>

Use the same JSON structure as Cursor unless your local Windsurf version requires a different MCP config format.

</details>

### 4. Verify the Connection

Open your AI client and try a few safe requests first:

- "Call `get_scene_info` and tell me what scene is open."
- "Read `unity://project/context` and summarize the current editor state."
- "Use `execute_code` to return the active scene name."

If those work, the MCP server, resources, and primary execution tool are connected correctly.

### 5. Start Building

Open your AI client and try: *"Create a 3D platformer level with 5 floating platforms"*

## Before You Start

- This package is **Editor-only**. It does not add runtime components to your built game.
- The MCP server starts on `http://127.0.0.1:8765/` by default.
- Local MCP server settings are stored in `UserSettings/KitWrightMcpSettings.json`.
- The package defaults to the `core` MCP tool profile to reduce tool-list noise for AI clients. `core` currently exposes 38 high-signal tools centered on `execute_code`, play mode control, input simulation, screenshots, performance inspection, logs, compilation checks, structured object location and component editing, editor selection / prefab-stage state, live C# API reflection, Unity documentation lookup, and `execute_menu_item` as a low-friction fallback. Switch to `full` in the **Server** tab if you want all 266 tools exposed.
- `execute_code` safety checks and the stricter filesystem guard are enabled by default from the **Settings** tab. The guard blocks obvious destructive snippets, broad `System.IO` writes, raw file streams, and absolute/user/system/traversal paths, but it is not a complete sandbox. Clients may still override the default per call with the optional `safety_checks` argument.
- Plugin debug logging is off by default and can also be enabled from the **Settings** tab. Warnings and errors are always written to the Unity Console.
- All exposed MCP tools run directly. There is no extra approval toggle.
- The window checks for updates in the background and shows an **Update** button in its header when a newer release exists — it can refresh Git installs in place or download and import the latest `unitypackage` automatically.

## Why This Project

- **`execute_code` First** — Optimized around one in-memory C# execution tool for rich editor/runtime orchestration. See [`execute_code`: In-Memory C# Execution](#execute_code-in-memory-c-execution) below for details.
- **Default Safety Checks** — `execute_code` now has persistent default-on safety toggles, including a stricter filesystem guard for clients that do not expose per-call arguments clearly
- **Play Mode Automation** — Enter play mode, simulate keyboard/mouse input, capture screenshots, inspect logs, and validate behavior from the same MCP session
- **Project Context Built In** — Exposes live resources for project state, active scene, selection, compilation, console output, and MCP interaction history
- **Focused by Default, Full When Needed** — `core` exposes a compact high-signal toolset; `full` exposes 266 tools
- **Single Unity Package** — No extra approval UI, no external daemon to click through, and no Python requirement for the Unity-side plugin itself
- **Extensible** — Add custom tools with attribute-based discovery, or connect Unity to external MCP services when needed

## Highlights

- **266 Built-in Tools** — Scene editing, assets, scripts, play mode control, screenshots, performance analysis, prompts, resources, structured object location, SerializedObject-based component editing, editor-state inspection, menu-item fallback, and editor automation across 57 modules. Full list: [TOOLS.md](TOOLS.md)
- **Structured Returns + `instanceId` Chaining** — Tools return `{success, message, data}` JSON with stable `instanceId` fields so agents can chain `by_id` calls reliably instead of re-resolving by name
- **`IKitWrightCommand` for `execute_code`** — New snippet template with auto-Undo (`ctx.RegisterObjectCreation` / `ctx.RegisterObjectModification` / `ctx.DestroyObject`), structured logs (`ctx.Log/LogWarning/LogError`), and a tracked changelog returned to the agent
- **Resources & Prompts** — Live project context, scene/selection/error resources, resource templates, and reusable workflow prompts
- **Input Simulation + Screenshots** — Drive play mode with keyboard/mouse simulation and verify results with game/scene captures
- **Built-in Updating** — The MCP Window surfaces new releases and either re-pulls the Git package or auto-imports the latest `unitypackage`
- **Integrations Tab** — Detects Hot Reload, Memory Profiler, Addressables, Input System, Timeline, URP, and Test Framework, and shows which tools each one unlocks
- **One-Click Client Configuration** — Write MCP config entries for 19 targets directly from the Unity window: Claude Code, Cursor, VS Code (+ Insiders), Codex, Windsurf, Cline, Kiro, Trae, Rider (+ Junie), Kimi Code, Qwen Code, Antigravity, Kilo Code, OpenCode, GitHub Copilot CLI, CodeBuddy CLI, and Roo Code
- **Tool Exposure Control** — Edit the exact tools exposed by `core` and `full`
- **Project Skills Manager** — Configure project-level skills for supported AI clients, currently installing the default `unity-mcp-workflow` skill
- **MCP Settings** — Adjust `execute_code` safety defaults and enable verbose plugin debug logging when troubleshooting MCP connections or tool execution
- **Vendor Agnostic** — Works with any AI client that supports MCP: Claude Code, Cursor, LM Studio, Windsurf, Codex, VS Code Copilot, etc.

## `execute_code`: In-Memory C# Execution

`execute_code` is the heart of KitWright MCP for Unity. It lets an AI write a C# snippet, compile it through a Roslyn-first in-memory flow, and run it on the editor thread — the agent gets the full Unity Editor and runtime API surface without writing any project files to disk.

- **Zero project footprint compilation** — Snippets are compiled with Unity's bundled Roslyn csc first while preserving the in-memory compilation/execution flow. No `.cs` files are written under `Assets/`, no domain reload is triggered, no project state is touched beyond what the snippet itself does.
- **Editor-ready before it runs** — Each call refreshes the AssetDatabase and waits for any pending compilation to settle before compiling the snippet, so external file edits are picked up automatically without a separate `request_recompile`.
- **Auto-Undo + structured logs (recommended template)** — Implement `IKitWrightCommand` and use the injected `ExecutionContext` so every created / modified / destroyed object participates in editor Undo, and the changelog is returned to the agent.

```csharp
using UnityEngine;
using UnityEditor;
using KitWright.Editor.Tools.Helpers;
using KitWright.Editor.Tools.Scripting;

public class CommandScript : IKitWrightCommand
{
    public void Execute(ExecutionContext ctx)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ctx.RegisterObjectCreation(go);          // auto-Undo + tracked
        ctx.Log("Created {0}", go.name);
        ctx.ReturnValue = GameObjectSerializer.Describe(go, includeComponents: false);
    }
}
```

The response carries `{ logs, created, modified, destroyed, returnValue }`, so the agent can verify exactly what changed without re-querying the scene.

The legacy template (`public static string Run()`) is still supported — useful for one-off inspection snippets where structured tracking is overkill.

**When to reach for `execute_code` vs a specialized tool** — `execute_code` shines for multi-step orchestration, novel reads, and situations where chaining 5–10 narrow tool calls would be noisier than one snippet. For single-field component edits, simple selection changes, or anything covered by an existing tool, prefer the dedicated tool — it is cheaper for the LLM to call and easier to verify.

## Comparison With Coplay

The table below compares this repository with the publicly documented behavior of Coplay's open-source `unity-mcp` repository on GitHub (checked against v10.0.0 and the public docs site).

| Area | KitWright MCP for Unity | Coplay `unity-mcp` |
|------|--------------------------|--------------------|
| Unity-side architecture | Embedded Unity Editor package with built-in HTTP MCP server | Unity bridge plus a Python MCP server, run locally or remote-hosted with API-key auth |
| Extra local prerequisites | Unity package only for core workflows | Unity + Python 3.10+ + `uv` according to the public quick start |
| Tool surface | 266 fine-grained tools across 57 modules, chained by `instanceId` | ~51 wide `manage_*` entrypoints with an `action` enum per tool (README states "47 focused entrypoints") |
| Primary workflow style | `execute_code` first, then focused helper tools | `manage_*` families first; `execute_code` is available in the `scripting_ext` group |
| Default tool exposure | Compact `core` profile with optional `full` expansion | Tool groups toggled per session via `manage_tools` |
| Asset generation | Not built-in (compose external APIs via `execute_code`) | `generate_image` / `generate_audio` / `generate_model` via fal.ai, Tripo, Meshy, plus Sketchfab import |
| UI Toolkit and ProBuilder | Not built-in (uGUI creation tools only) | `manage_ui` for UXML/USS/UIDocument and `manage_probuilder` for in-editor modeling |
| Built-in context model | Project resources, resource templates, workflow prompts, interaction history | Public README emphasizes tool families and bridge/server workflow |
| Play mode validation | Built-in play mode control, screenshots, logs, and input simulation in the package | Public README emphasizes broad Unity management and automation tools |
| C# API introspection | `reflect_api` — declared members, full signatures per member, extension methods, obsolete members, interfaces, and `search` by substring scoped to `unity` / `packages` / `project` / `all`. Adds candidate suggestions when a type or member name does not resolve, case-insensitive member matching, and `include_non_public` for private and internal members | `unity_reflect` — `get_type` / `get_member` / `search` with a comparable scope filter, extension-method resolution, and interface and obsolete-member listings |
| Documentation | `README.md` plus [TOOLS.md](TOOLS.md), both generated from the `[ToolProvider]` sources | Auto-generated docs site with a detail page per tool covering parameters, actions, and examples |
| Tool exposure switching | `set_tool_profile` switches `minimal` / `core` / `extended` / `full` from the client and pushes `tools/list_changed` | `manage_tools` enables and disables tool groups per session |
| Tool annotations | `annotations.readOnlyHint` on read-only tools | `readOnlyHint` and `destructiveHint` on annotated tools |
| Positioning | Lightweight, direct, MIT-licensed Unity MCP server for AI-driven editor control | Full-featured Unity bridge maintained by Coplay with Python-backed server setup |

Source for Coplay column: [CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp) and its [tool reference](https://coplaydev.github.io/unity-mcp/reference/tools)

## Comparison With Unity AI Assistant

The table below compares this repository with Unity Technologies' official `com.unity.ai.assistant` package (v2.7.0-pre.2 as of 2026-05).

| Area | KitWright MCP for Unity | Unity AI Assistant |
|------|--------------------------|--------------------|
| Minimum Unity version | 2022.3 | 6000.3 (Unity 6 only) |
| License | MIT, open source | Unity Terms of Service, proprietary |
| Deployment | Local HTTP MCP server in Editor, no cloud | Editor + native Relay subprocess + Unity Cloud backend |
| Billing | Free, user brings their own AI client | Credits-based (Unity Dashboard) |
| Tool exposure | 266 tools across 57 modules, `core` (38) / `full` profiles | ~15 MCP tools (mostly `Manage*` families) |
| Generic escape hatch | `execute_code` — Roslyn-first in-memory compile, `IKitWrightCommand` + Undo, no sandbox (client-side approval) | `RunCommand` — namespace blacklist sandbox |
| Play mode validation | Full loop: enter / simulate input / capture / read logs / exit | Enter/Exit only; no input simulation |
| Asset generators | Not built-in (compose external APIs via `execute_code`) | Native Image / Mesh / PBR / Sound / Animation generators |
| Primary client model | BYO any MCP client (Claude Code / Cursor / LM Studio / Codex / VS Code) | Built-in chat window + ACP for Claude/Gemini via Gateway |
| Offline-capable | Yes for tool calls (inference depends on chosen client) | No (inference requires Unity Cloud) |

For a long-form comparison of the two approaches see [KitWright MCP for Unity vs Unity AI Assistant detailed comparison](https://blog.csdn.net/m0_62670368/article/details/161039766) (Chinese).

## MCP Capabilities

The current open-source package exposes four high-value capability layers:

- **Tools** — 266 total tools in `full`, 38 focused tools in `core`
- **Primary execution** — `execute_code` for rich editor/runtime orchestration
- **Prompts** — workflow prompts like `fix_compile_errors`, `runtime_validation`, and `create_playable_prototype`
- **Resources** — project context, scene summaries, selection state, compile errors, console errors, MCP interaction history, plus resource templates for scene objects, components, and asset paths

## Built-in Tools

<!-- tools-summary:start -->
KitWright MCP for Unity ships **265 tool functions across 57 modules** (`core` profile exposes 41 of them).

| Module | Tools | Module | Tools |
|--------|-------|--------|-------|
| **EditorState** | 18 | **Script** | 4 |
| **GameObject** | 14 | **Texture** | 4 |
| **Profiler** | 13 | **Build** | 3 |
| **Scene** | 13 | **ComponentBatch** | 3 |
| **Prefab** | 10 | **Docs** | 3 |
| **Asset** | 8 | **Lighting** | 3 |
| **Terrain** | 8 | **Package** | 3 |
| **AssemblyDefinition** | 7 | **Physics** | 3 |
| **Prefs** | 7 | **ScriptableObject** | 3 |
| **SpriteAtlas** | 7 | **Sprite** | 3 |
| **Visual** | 7 | **Testing** | 3 |
| **Addressable** | 6 | **Undo** | 3 |
| **Animation** | 6 | **AssetImport** | 2 |
| **Audio** | 6 | **EditorDialog** | 2 |
| **Code** | 6 | **EditorWindowInteraction** | 2 |
| **InputActions** | 6 | **Material** | 2 |
| **NavMesh** | 6 | **MenuItem** | 2 |
| **Shader** | 6 | **Performance** | 2 |
| **UI** | 6 | **References** | 2 |
| **File** | 5 | **Batch** | 1 |
| **MemorySnapshot** | 5 | **Hierarchy** | 1 |
| **SceneView** | 5 | **Interop** | 1 |
| **Volume** | 5 | **Mesh** | 1 |
| **Camera** | 4 | **Particle** | 1 |
| **Compilation** | 4 | **ProjectSettings** | 1 |
| **ComponentProperty** | 4 | **Reflection** | 1 |
| **InputSimulation** | 4 | **Timeline** | 1 |
| **LodConstraint** | 4 | **ToolExposure** | 1 |
| **Screenshot** | 4 |  |  |

> 📖 Every tool with its description: [TOOLS.md](TOOLS.md).
<!-- tools-summary:end -->

> 📊 See [PROFILER_TOOLS.md](PROFILER_TOOLS.md) for the full Profiler tool reference, implementation notes, known limitations, and test report.

## Adding Custom Tools

Your project can declare its own MCP tools with the same attributes the built-in tools use. Put the class in an **Editor** assembly — `KitWright.Editor` is `autoReferenced`, so no asmdef reference setup is needed:

```csharp
using System.ComponentModel;
using KitWright.Editor.Tools;

[ToolProvider("MyTools")]
public static class MyCustomTools
{
    [Description("Spawns enemies at random positions in the scene")]
    public static string SpawnEnemies(
        [ToolParam("Number of enemies to spawn", Required = true)] int count,
        [ToolParam("Prefab path in Assets")] string prefabPath)
    {
        // Your implementation here
        return $"Spawned {count} enemies";
    }
}
```

Public static methods on the class are discovered automatically, converted to snake_case (`spawn_enemies`), and exposed via MCP with JSON Schema definitions generated from the parameter list. Mark a method `[ReadOnlyTool]` when it does not modify the scene or project, and clients receive `annotations.readOnlyHint` for it.

Project-declared tools are exposed under **every** profile, including the default `core`, since writing one is already an explicit opt-in — a custom tool would otherwise be invisible to the profile most clients connect with. They still appear in the **Tool Exposure** tab, so a profile configured there can turn them off like any built-in tool.

## Architecture

```
MCP Server (HTTP JSON-RPC 2.0)
    └─ MCPRequestHandler (protocol handling)
        └─ MCPExecutionBridge
            └─ FunctionInvoker (reflection-based invocation)
                └─ Tool Functions (266 built-in tools across 57 modules)
```

```
External AI Client → HTTP Request → MCPRequestHandler → MCPExecutionBridge → FunctionInvoker → tool method
```

## Requirements

- Unity 2022.3 or later
- `com.unity.nuget.newtonsoft-json` — pulled in automatically by UPM and OpenUPM installs; Asset Store imports offer it in the Package Manager dependency dialog
- `com.unity.ugui` and `com.unity.test-framework` — declared dependencies, already present in a default Unity project

## Contributing

Contributions are welcome! Please read the [Contributing Guide](CONTRIBUTING.md) before submitting a PR.

## Security

Found a vulnerability? Report it privately — see the [Security Policy](SECURITY.md).

## License

[MIT](LICENSE) — Free to use, modify, distribute, and integrate into commercial or open-source projects.

The KitWright name and logo are trademarks of the KitWright project and are **not** covered by the MIT license. You may not use the name or logo to brand derivative works or imply endorsement without prior written permission. All rights to the brand assets (files under `Editor/Icons/`) are reserved.
