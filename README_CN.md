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
    中文 | <a href="./README.md">English</a>
  </p>
  <p align="center">
    <img src="./Documentation~/Video_Logo.gif" alt="The Most Advanced MCP Server for Unity" width="60%">
  </p>
</p>

> 💖 如果这个项目对你有帮助，欢迎顺手点一个 Star。它能帮助更多 Unity 开发者发现这个项目，也能支持后续持续维护。

---

KitWright MCP for Unity 是一个采用 MIT 协议的 Unity 编辑器 MCP 服务器，让 Claude Code、Cursor、LM Studio、Windsurf、Codex、VS Code Copilot 等 AI 助手直接操作正在运行的 Unity 项目。

一句话描述你的游戏 — AI 助手通过 KitWright MCP for Unity 的 266 个内置工具自动创建场景、编写脚本、验证运行态、模拟输入、分析性能并完成编辑器自动化，把所有逻辑串联起来。

> *"做一个贪吃蛇游戏，10x10 网格，食物随机生成，计分 UI，游戏结束界面"*
>
> AI 助手通过 KitWright MCP for Unity 全程处理：创建场景、生成全部脚本、搭建 UI、配置游戏逻辑 — 只需一句话。

<p align="center">
  <img src="./Documentation~/demo.gif" alt="KitWright MCP for Unity — 16 秒 demo" width="100%">
</p>
<p align="center"><em>16 秒 demo — AI 生成 3D 模型并端到端集成进场景。<a href="https://github.com/kitwright/unity-mcp/raw/main/Documentation~/demo.mp4">观看高清 MP4</a>。</em></p>

## 快速开始

如果你只想尽快跑起来，先做这三步：

- 用 Git URL 安装 Unity 包
- 打开 `Window > KitWright > MCP Window`，在 **Server** 页签启动服务
- 使用内置的一键客户端配置

### 1. 通过 UPM 安装 (Git URL)

在 Unity 中，打开 **Window → Package Manager → + → Add package from git URL**：

```
https://github.com/kitwright/unity-mcp.git
```

> 💡 在 clone 或安装之前，如果你愿意顺手点一个 ⭐，会非常感谢。

### 可选方案：通过 OpenUPM 安装

如果你希望 Unity Package Manager 显示 registry 提供的完整“版本历史记录”并能选择历史版本，可以改用 OpenUPM 安装。

使用 OpenUPM CLI：

```bash
openupm add com.kitwright.unity.mcp
```

或者手动在 `Packages/manifest.json` 中添加 scoped registry：

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

如果之前是用 Git URL 安装的，先移除 Git dependency，再从 OpenUPM 安装。Git 来源的包在 Unity 中只会显示当前解析到的 Git 版本，不会显示 registry 提供的完整 Version History。

### 可选方案：从 Unity Asset Store 安装

导入 Asset Store 包时会弹出 **This Unity Package has Package Manager dependencies** 对话框，请选择 **Install/Upgrade**。本包需要 `com.unity.nuget.newtonsoft-json`，跳过该提示会导致项目缺少该依赖，KitWright 的所有脚本都无法编译。

如果已经点了 Skip，插件会在下一次域重载时询问是否自动安装该依赖；你也可以自己从 **Window → Package Manager → + → Add package by name** 添加：

```
com.unity.nuget.newtonsoft-json
```

### 2. 启动 MCP Server

**菜单：Window → KitWright → MCP Window**，然后在 **Server** 页签启动服务。

默认从 `http://127.0.0.1:8765/` 启动。

默认传输是 **Broker Mode**。它会用 Unity 自带 Mono 启动一个很小的本地 broker，客户端仍然连接同一个 `127.0.0.1` 端口，不需要改 MCP 配置，并且能在 Unity 脚本重编译或进入 Play Mode 触发域重载时尽量保持 MCP 客户端连接。如果 broker 启动失败，服务器会自动回退到进程内 Direct HTTP；你也可以在 **Server** 页签关闭 broker 模式，始终使用 Direct HTTP。

窗口共有五个页签：**Server**、**Settings**、**Skills**、**Tool Exposure**、**Integrations**。

如果你想编辑 `core` 或 `full` 各自暴露哪些工具，可以打开 **Tool Exposure** 页签。

如果需要调整 `execute_code` 安全默认值或插件 debug 日志，可以打开 **Settings** 页签。

### 3. 配置 AI 客户端

优先使用 **Server** 页签里的 **一键 MCP 配置**。

选择目标客户端后点击 **Configure**，插件会直接帮你写入推荐的 MCP 配置项。

对于 Claude Code、Cursor 和 Codex，也可以点击 **Configure + Skills**，同时安装默认的项目级 MCP 工作流 skill。

如果你希望为当前 Unity 项目配置项目级 AI 指引，可以打开 **Skills** 页签，为支持的平台安装默认的 `unity-mcp-workflow` skill。

如果你更想手动编辑配置文件，再参考下面这些示例：

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

LM Studio 不在一键配置的目标列表中——它的 `mcp.json` 路径会随版本和平台变化。请在 LM Studio 中通过 **Program > Install > Edit mcp.json** 打开配置文件，手动粘贴下面的配置项。

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

除非你本地 Windsurf 版本要求不同的 MCP 配置格式，否则可直接使用与 Cursor 相同的 JSON 结构。

</details>

### 4. 验证连接

先在 AI 客户端里试几个安全请求：

> “调用 `get_scene_info`，告诉我当前打开的是哪个场景。”

> “读取 `unity://project/context`，总结当前编辑器状态。”

> “调用 `execute_code`，返回当前激活场景名。”

如果这些都正常返回，说明 MCP server、resources 和主执行工具都已经连通。

### 5. 开始构建

打开你的 AI 客户端，试试：*"创建一个 3D 平台跳跃关卡，包含 5 个浮空平台"*

## 开始前说明

- 这是一个 **仅限 Editor** 的包，不会向最终构建产物添加运行时代码。
- MCP Server 默认从 `http://127.0.0.1:8765/` 启动。
- 本地 MCP Server 配置保存在 `UserSettings/KitWrightMcpSettings.json`。
- 插件默认使用 `core` MCP 工具暴露配置，减少 AI 客户端的工具噪音；`core` 当前暴露 38 个高频工具，覆盖 `execute_code`、运行模式控制、输入模拟、截图、性能检查、日志、编译检查、结构化对象定位与组件编辑、编辑器选中与 prefab stage 状态读写，以及 `execute_menu_item` 兜底入口。如果你需要完整工具集，可在 **Server** 页签切换到 `full`，暴露全部 266 个工具。
- `execute_code` safety checks 和更严格的文件系统 guard 现在可在 **Settings** 页签设置默认值，默认开启；它会阻止明显破坏性片段、宽泛的 `System.IO` 写入、原始文件流、绝对路径、用户/系统目录路径和 `../` 穿越路径，但它不是完整沙箱。客户端仍可在单次调用中用可选 `safety_checks` 参数显式覆盖。
- 插件 debug 日志默认关闭，也可在 **Settings** 页签中开启；Warning 和 Error 始终会输出到 Unity Console。
- 所有已暴露的 MCP 工具都会直接执行，不再提供额外的 approval 开关。
- 窗口会在后台检查新版本，有更新时在标题栏显示 **Update** 按钮：Git 安装会直接重新拉取，`.unitypackage` 导入会自动下载并导入最新版。

## 能力概览

- **`execute_code` 主工具优先** — 核心体验围绕一个内存 C# 执行工具构建，适合复杂编辑器/运行态编排。详见下方 [`execute_code`：内存 C# 执行](#execute_code内存-c-执行)。
- **默认安全检查** — `execute_code` 现在有持久化、默认开启的 safety toggle，并包含更严格的文件系统 guard，适合 LM Studio 这类不明显暴露单次参数的客户端
- **Play Mode 自动化闭环** — 进入运行模式、模拟键鼠输入、截图、查看日志、验证行为都能在同一 MCP 会话里完成
- **内建项目上下文** — 直接提供项目状态、当前场景、选择对象、编译错误、控制台输出和 MCP 交互记录资源
- **默认聚焦，必要时全量** — 默认 `core` 工具集更利于 AI 选工具，需要时可切到 `full` 暴露全部 266 个工具
- **单 Unity 包落地** — 不需要额外 approval 开关，Unity 侧也不依赖单独 Python 守护进程
- **可扩展** — 支持 Attribute 发现自定义工具，也支持连接外部 MCP 服务

## 核心特性

- **266 个内置工具** — 覆盖场景编辑、脚本、资产、运行态控制、截图、性能分析、Prompts、Resources、结构化对象定位、SerializedObject 组件编辑、编辑器状态读写、菜单项兜底以及编辑器自动化，共 57 个模块。完整清单见 [TOOLS.md](TOOLS.md)
- **结构化返回 + `instanceId` 链式调用** — 工具返回 `{success, message, data}` JSON 并附带稳定的 `instanceId`，agent 后续直接 `by_id` 调用，不再受重名困扰
- **`execute_code` 的 `IKitWrightCommand` 模板** — 新模板自动 Undo（`ctx.RegisterObjectCreation` / `ctx.RegisterObjectModification` / `ctx.DestroyObject`）、结构化日志（`ctx.Log/LogWarning/LogError`），并把改动列表回传给 agent
- **Resources 与 Prompts** — 暴露实时项目上下文、场景/选择/错误资源、资源模板，以及常见 Unity 工作流的可复用 MCP Prompt
- **输入模拟 + 截图验证** — 在 Play Mode 中模拟键盘/鼠标，再用 Game View / Scene View 截图验证结果
- **内置更新** — MCP Window 会提示新版本，并根据安装方式自动重新拉取 Git 包或导入最新 `unitypackage`
- **Integrations 页签** — 自动检测 Hot Reload、Memory Profiler、Addressables、Input System、Timeline、URP 和 Test Framework，并显示各自解锁了哪些工具
- **一键客户端配置** — 直接在 Unity 窗口里为 19 个目标写入 MCP 配置：Claude Code、Cursor、VS Code（含 Insiders）、Codex、Windsurf、Cline、Kiro、Trae、Rider（含 Junie）、Kimi Code、Qwen Code、Antigravity、Kilo Code、OpenCode、GitHub Copilot CLI、CodeBuddy CLI、Roo Code
- **工具暴露控制** — 编辑 `core` 和 `full` 各自暴露的具体工具
- **项目 Skills 管理器** — 为支持的 AI 客户端配置项目级 skills，目前安装默认的 `unity-mcp-workflow` skill
- **插件设置** — 排查 MCP 连接或工具执行问题时，可开关详细 debug 日志
- **厂商无关** — 兼容任意支持 MCP 的 AI 客户端：Claude Code、Cursor、LM Studio、Windsurf、Codex、VS Code Copilot 等

## `execute_code`：内存 C# 执行

`execute_code` 是 KitWright MCP for Unity 的核心工具。AI 写一段 C#，通过 Roslyn 优先的内存编译流程完成编译，并在编辑器线程直接执行——agent 拿到 Unity Editor 与 Runtime 的全套 API，但完全不需要往项目里写 file。

- **零项目落盘编译** —— 优先使用 Unity 自带 Roslyn csc 编译，同时保留内存编译/内存执行流程。`Assets/` 下不会多出 `.cs` 文件，不会触发 domain reload，除非 snippet 自己显式改，否则项目状态不动。
- **运行前自动就绪** —— 每次调用都会先刷新 AssetDatabase 并等待 pending compilation 完成，外部文件编辑会被自动拾取，不需要额外 `request_recompile`。
- **自动 Undo + 结构化日志（推荐模板）** —— 实现 `IKitWrightCommand`，用注入的 `ExecutionContext`：所有新建/修改/销毁的对象都自动进 editor Undo，改动列表也会回传给 agent。

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
        ctx.RegisterObjectCreation(go);          // 自动 Undo + 追踪
        ctx.Log("Created {0}", go.name);
        ctx.ReturnValue = GameObjectSerializer.Describe(go, includeComponents: false);
    }
}
```

返回里带 `{ logs, created, modified, destroyed, returnValue }`，agent 不用再回查场景就能确认改动。

旧模板（`public static string Run()`）仍然兼容，适合一次性 inspection snippet——不需要结构化追踪的场景。

**什么时候用 `execute_code` vs 专门工具** —— `execute_code` 适合多步编排、新颖查询、或者会被拆成 5-10 个细粒度调用的场景，一段 snippet 比一连串小工具更省。要是单字段组件修改、简单选中切换，或者已有专门工具能搞定的，优先用专门工具——对 LLM 调用成本更低、验证更直接。

## 与 Coplay 的对比

下表基于 Coplay 官方公开 GitHub 仓库（v10.0.0）与文档站所描述的能力与安装方式进行对比。

| 维度 | KitWright MCP for Unity | Coplay `unity-mcp` |
|------|-------------------------|--------------------|
| Unity 侧架构 | Unity 包内置 HTTP MCP server | Unity bridge + Python MCP server，可本地运行或部署为带 API Key 鉴权的远程服务 |
| 额外本地依赖 | `core` 工作流下只需要 Unity 包本身 | 官方 quick start 要求 Python 3.10+ 与 `uv` |
| 工具面 | 266 个细粒度工具、57 个模块，靠 `instanceId` 链式调用 | 约 51 个大粒度 `manage_*` 入口，每个工具内部再用 `action` 枚举分派（README 表述为 47 个入口）|
| 主要交互模型 | 以 `execute_code` 为主，再配合少量高频辅助工具 | 以 `manage_*` 工具族为主；`execute_code` 位于 `scripting_ext` 工具组 |
| 默认工具暴露 | 默认 `core` 精简工具集，可切 `full` | 通过 `manage_tools` 按会话开关工具组 |
| 资产生成 | 不内置（可用 `execute_code` 组合外部 API）| `generate_image` / `generate_audio` / `generate_model`，接入 fal.ai、Tripo、Meshy，并支持 Sketchfab 导入 |
| UI Toolkit 与 ProBuilder | 不内置（只有 uGUI 创建工具）| `manage_ui` 处理 UXML/USS/UIDocument，`manage_probuilder` 提供编辑器内建模 |
| 上下文能力 | 内建项目资源、资源模板、工作流 prompts、交互历史 | 公开 README 主要强调 bridge/server 与工具族 |
| Play Mode 验证 | 包内置运行模式控制、截图、日志、输入模拟 | 公开 README 强调广泛 Unity 管理与自动化能力 |
| C# API 反射查询 | `reflect_api`：声明成员、单成员完整签名、扩展方法、过时成员、接口，以及按子串搜索类型（范围 `unity` / `packages` / `project` / `all`）；另有解析失败时的候选名、忽略大小写的成员匹配，以及 `include_non_public` 查看私有与内部成员 | `unity_reflect`：`get_type` / `get_member` / `search`，同样支持范围过滤、扩展方法解析、接口与过时成员清单 |
| 文档 | `README.md` 与 [TOOLS.md](TOOLS.md)，均由 `[ToolProvider]` 源码生成 | 自动生成的文档站，每个工具一页，含参数、action 与示例 |
| 客户端切换工具集 | `set_tool_profile` 可从客户端切换 `minimal` / `core` / `extended` / `full`，并推送 `tools/list_changed` | `manage_tools` 按会话启用/停用工具组 |
| 工具注解 | 只读工具输出 `annotations.readOnlyHint` | 已注解工具输出 `readOnlyHint` 与 `destructiveHint` |
| 定位 | 轻量、直接、MIT 协议的 Unity MCP 服务器 | Coplay 维护的全功能 Unity bridge 方案 |

Coplay 信息来源：[CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp) 及其[工具文档](https://coplaydev.github.io/unity-mcp/reference/tools)

## 与 Unity AI Assistant 的对比

下表对比本仓库与 Unity Technologies 官方包 `com.unity.ai.assistant`（2026-05 时点 v2.7.0-pre.2）。

| 维度 | KitWright MCP for Unity | Unity AI Assistant |
|------|-------------------------|--------------------|
| 最低 Unity 版本 | 2022.3 | 6000.3（仅 Unity 6）|
| 协议 / License | MIT 开源 | Unity Terms of Service，私有 |
| 部署 | Editor 内嵌 HTTP MCP server，纯本地 | Editor + 原生 Relay 子进程 + Unity Cloud 后端 |
| 计费 | 免费，用户自带 AI 客户端 | Credits 点数制（Unity Dashboard）|
| 工具暴露 | 266 工具 / 57 模块，`core` (38) / `full` profile | ~15 个 MCP 工具（多数为 `Manage*` 大粒度族）|
| 通用逃生口 | `execute_code` — Roslyn 优先内存编译、`IKitWrightCommand` + Undo、无沙箱（客户端层审批）| `RunCommand` — 命名空间黑名单沙箱 |
| Play Mode 验证 | 完整闭环：进入 / 模拟输入 / 截图 / 读日志 / 退出 | 仅进入/退出，无输入模拟 |
| 资产生成器 | 不内建（通过 `execute_code` 组合外部 API）| 内建 Image / Mesh / PBR / Sound / Animation 五类生成器 |
| 主要客户端模型 | BYO 任意 MCP 客户端（Claude Code / Cursor / LM Studio / Codex / VS Code）| 自带对话窗口 + ACP 经 Gateway 接 Claude/Gemini |
| 离线可用 | ✅ 工具调用本身全本地（推理依赖所选客户端）| ❌ 推理必须连 Unity Cloud |

长文对比见 [KitWright Unity MCP 与 Unity AI Assistant 详细对比](https://blog.csdn.net/m0_62670368/article/details/161039766)。

## MCP 能力结构

当前开源包有四层高价值能力：

- **Tools** — `full` 下共 266 个工具，`core` 下 38 个高频工具
- **Primary execution** — `execute_code` 用于复杂编辑器/运行态编排
- **Prompts** — 包括 `fix_compile_errors`、`runtime_validation`、`create_playable_prototype` 等工作流 Prompt
- **Resources** — 项目上下文、场景摘要、选择状态、编译错误、控制台错误、MCP 交互记录，以及按对象/组件/资源路径展开的模板资源

## 内置工具

<!-- tools-summary:start -->
KitWright MCP for Unity 当前提供 **265 个工具函数，覆盖 57 个模块**（`core` profile 暴露其中 41 个）。

| 模块 | 工具数 | 模块 | 工具数 |
|------|--------|------|--------|
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

> 📖 每个工具及其说明见 [TOOLS.md](TOOLS.md)。
<!-- tools-summary:end -->

> 📊 完整的 Profiler 工具参考、实现细节、已知限制和测试报告见 [PROFILER_TOOLS_CN.md](PROFILER_TOOLS_CN.md)。

## 添加自定义工具

通过简单的 Attribute 标注即可创建自定义工具：

```csharp
using System.ComponentModel;

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

方法会被自动发现，名称转换为 snake_case（`spawn_enemies`），并通过 MCP 自动生成 JSON Schema 定义暴露给 AI。

## 架构

```
MCP Server (HTTP JSON-RPC 2.0)
    └─ MCPRequestHandler (协议处理)
        └─ MCPExecutionBridge
            └─ FunctionInvoker (反射式调用)
                └─ Tool Functions (266 个内置工具，57 个模块)
```

```
外部 AI 客户端 → HTTP 请求 → MCPRequestHandler → MCPExecutionBridge → FunctionInvoker → 工具方法
```

## 环境要求

- Unity 2022.3 或更高版本
- `com.unity.nuget.newtonsoft-json` —— UPM 和 OpenUPM 安装会自动拉取；Asset Store 导入时会在 Package Manager 依赖对话框中提示安装
- `com.unity.ugui` 与 `com.unity.test-framework` —— 包清单中声明的依赖，默认 Unity 项目已自带

## 参与贡献

欢迎贡献！提交 PR 前请阅读 [贡献指南](CONTRIBUTING.md)。

## 许可证

[MIT](LICENSE) — 可自由使用、修改、分发，也可集成到商业或开源项目中。
