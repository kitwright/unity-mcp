# Changelog

## [Unreleased]

### Added
- `edit_script_members` — edit a C# file one member at a time, addressing methods by name instead of by text. `patch_script` needs `old_text` to match the file byte for byte, so it breaks on the things that differ between a model's memory of a file and the file itself: indentation, an attribute that moved, a comment. Worse, a near-miss can overwrite part of the next member and only surface as a compile error. Here the span comes from matching braces, so an edit stops at the member's own closing brace. Takes a JSON array of `replace_method` / `insert_method` / `delete_method` ops applied in order and written only if all of them succeed. The replacement is re-indented to the target's nesting, the attributes and comment directly above a method count as part of it, an overloaded name returns `AMBIGUOUS_METHOD` with the signatures rather than guessing, and a file that was uniformly CRLF stays CRLF. Not a parser: it masks literals and comments and works on what is left, so a `#if` that hides a brace is invisible to it and such a file should still be edited with `patch_script`.
- `validate_script` — check that a file (or a proposed replacement for one, via `content`) is structurally sound: balanced braces, parentheses and brackets, no unterminated string or comment, reporting the line of the first problem. `edit_script_members` runs the same check on its result and refuses to write a file it just broke. Deliberately not a compile — the authoritative answer is Unity's own via `request_recompile` + `get_compilation_errors`, which builds the whole assembly with its real define symbols and sibling files. Validating one file in isolation cannot do that: it misreports every partial class and every `#if` branch whose symbol it does not know.
- `get_editor_state` reports `projectName` and `projectPath`. With several editors running an agent had no way to confirm which project the server it is talking to has open, and the name alone does not settle it — two clones share it, which is why the transport's project pin hashes the path.
- The server records which editor is serving which port in a per-user registry directory (`%LOCALAPPDATA%/KitWright/instances` on Windows, the equivalent user data directory elsewhere), one JSON file per project keyed by the project identity pin. With several projects open the port mapping was only known inside each editor — the configured port shifts on a collision, so the second project to start is not on the port its client config names. The entry is written when the transport starts and removed when it stops; an editor that crashes never runs the removal, so entries whose process is gone are hidden from reads and pruned on the next write. Files the registry cannot parse are left alone rather than deleted, since the directory is not exclusively its own.
- `fetch_docs` — fetch Unity documentation pages and return them as plain text, so an agent gets parameter notes, caveats and code examples without a separate web fetch and without pulling a full page of navigation markup into context. Takes a comma-separated list (up to 10 pages per call): a bare name resolves against the ScriptReference (`Physics.Raycast`, `AI.NavMeshAgent`), a `manual:` prefix resolves a Unity Manual slug (`manual:execution-order`). Pages are fetched for the editor's own Unity version, extracted from the first heading to the footer, entity-decoded, truncated to `max_chars` (default 4000) and cached for the domain. A page that does not exist comes back with `found: false` plus a search URL instead of an error, so a misremembered symbol costs one call. Pairs with `reflect_api` — reflection confirms the member exists on this Unity version, `fetch_docs` explains how to use it. Exposed in the `core` profile.
- Project and third-party editor code can now declare MCP tools. `[ToolProvider]`, `[ToolParam]` and `[ReadOnlyTool]` are public, so the attribute-based tool authoring the README has always documented actually compiles outside the package; `ToolRegistry` already scanned every loaded assembly. Tools declared outside the package assemblies are exposed under every profile including the default `core` — writing one is an explicit opt-in, and they would otherwise be invisible to the profile most clients connect with — while an explicitly configured profile in the Tool Exposure panel still takes precedence.
- `reflect_api` — inspect the live C# API by reflection before writing an `execute_code` snippet. Without `member` it lists the declared members of a type by name (enum values for enums), plus the extension methods that apply to it and which of its members are `[Obsolete]`; with `member` it returns full signatures including parameter names, falling back to inherited members and then to extension methods when the type does not declare it. `search` looks a type up by substring across loaded assemblies, ranked exact > prefix > contains and narrowed by `scope` (`unity` / `packages` / `project` / `all`); `project` and `packages` come from the compilation pipeline rather than the assembly name, since a package asmdef compiles into the same `ScriptAssemblies` folder as project code. `include_non_public` widens both listings to private and internal members. Unresolved names come back with candidate suggestions, so a misremembered API costs one read-only call instead of a failed compile. Short names that hit more than one loaded type return `AMBIGUOUS_TYPE` with the fully qualified matches rather than silently picking one. Exposed in the `core` profile.

- `get_component_properties` takes `name_filter`, a comma-separated list of case-insensitive name substrings. Reading one value off a component meant paying for all of them — a `CanvasScaler` costs 11 properties to learn its reference resolution, a `Rigidbody` or a large MonoBehaviour far more. Matching is against Unity's serialized names, so `resolution` finds `m_ReferenceResolution` without the caller knowing the `m_` convention, and the response says `2 of 11 properties (filter: ...)` so a filtered read can never be mistaken for the whole component.
- `get_console_logs` takes `include_timestamps`, prefixing each entry with the `HH:mm:ss` it was logged. Entries came back newest-first with no clock, so an agent could tell the order of two logs but not whether either happened before or after the action it just took; `since_seconds` answered that only as a filter decided up front. Off by default because the stamp is not free on a 200-entry read. Cache and auto sources only — the Editor console's `LogEntries` keeps no per-entry time. It composes with `group_duplicates`: the stamp is deliberately not part of the grouping key, so identical spam still collapses and the surviving line carries the time of its most recent occurrence.
- `set_tool_profile` — switch the exposed tool profile (`minimal` / `core` / `extended` / `full`) from the client instead of from the Unity window. Connected clients are told to refresh via `notifications/tools/list_changed`, so an agent that needs a tool the current profile hides no longer needs the user to change a setting, and neither the editor nor the server restarts. Exposed in the `minimal` and `core` profiles, and toggleable from the Tool Exposure panel like any other tool.
- Read-only tools now advertise `annotations.readOnlyHint` in `tools/list`. The `[ReadOnlyTool]` marker already existed on 100 tool functions and reached `ToolDefinition.readOnly`, but the MCP exporter dropped it, so clients had no way to tell `get_hierarchy` from `delete_asset` and prompted for approval on both.
- `get_editor_dialog` and `dismiss_editor_dialog` — read the modal dialog holding the editor (title, message body, button captions) and click one of its buttons. Until now a modal was a dead end: the editor loop stops, every queued call sits behind it, and only a human at the machine could clear it. These two read window state instead of asking the editor, so they answer while it is stuck. Dismissing requires `expected_title` **and** `button` to match what is actually on screen, and refuses otherwise — clicking "Don't Save" on a dialog the caller mistook for another one would discard the user's work, so a mismatch is an error rather than a guess. Nothing dismisses automatically. Windows only. A Unity IMGUI modal (`ShowModalUtility`) draws its buttons rather than owning Win32 ones, so it reports no captions and takes `button: "Close"` instead, which posts `WM_CLOSE` — the same thing its title-bar X does, and the same meaning as Cancel. A floating undocked editor window makes the blocking window ambiguous, and there both tools decline rather than pick wrong.
- `[OffEditorThread]` marks a tool that runs on the request thread instead of being queued for the editor loop. Every tool went through the editor thread, which is correct for anything touching Unity API and useless for a tool whose whole job is to answer while that thread is blocked. The bypass skips the state, domain-reload and interaction-log bookkeeping too, since all of it writes `SessionState`, which is main-thread only.
- `execute_menu_item` learns which menu paths open a modal instead of relying only on the curated list. A menu item that opens one does not return until a human clicks it, so an execution that took longer than ten seconds is the signal; the path is remembered per project and refused from then on. The curated list only ever covers what someone thought to add, so an unknown dialog used to hang the editor every single time; now it costs one hang, once. A genuinely slow menu item can be learned by mistake — `reset_learned_modal_menu_items` lists and clears them.
- The `EDITOR_NOT_PUMPING` watchdog names the dialog and its buttons rather than guessing at the cause, e.g. `Clear All PlayerPrefs [buttons: Yes | No]`. It found the real reason full test runs kept hanging: the Test Framework raises Unity's save prompt as a run *starts*, before the first test executes.
- `discard_unsaved` clears the modified flag (via the internal `EditorSceneManager.ClearSceneDirtiness`) instead of only skipping this server's own check. For `open_scene`, `create_new_scene` and `close_scene` this changes nothing observable — measured: `OpenScene`, `NewScene` and `CloseScene` called from script all replace a modified scene silently, without Unity's save prompt. It matters for `run_tests`, which is the one path that does *not* replace anything: the scene stays open and modified, and the Test Framework raises "Scene(s) Have Been Modified" as the run starts. Clearing the flag is the only way past that short of writing the file, so the four tools share one guard rather than diverging. If the internal API ever disappears the call is refused with `DISCARD_UNAVAILABLE` rather than proceeding into a dialog nobody can dismiss.
- `open_scene`, `create_new_scene`, `close_scene` and `run_tests` save modified scenes and carry on, which is now the default (`save_first`). The dirty-scene guard already existed, but its only escape hatch was `discard_unsaved` — so the single way past a modified scene was the one that threw the user's work away, and the safe answer, saving it, needed a human. Saving by default matches what a person clicking through Unity's own prompt almost always picks. `discard_unsaved` still wins when passed explicitly, and `save_first=false` restores the old refusal for a caller that wants to decide. Scenes that have never been saved are deliberately still refused with `SCENE_SAVE_FAILED` rather than saved: those have no asset path, so `SaveScene` raises Unity's save file picker, and that modal blocks the editor loop the call has to return on. `load_scene_additive` is unchanged — an additive load replaces nothing and was never guarded.
- `run_tests` guards on unsaved scenes at all, which is the fix for a hang that had nothing to do with any individual test: the Unity Test Framework raises "Scene(s) Have Been Modified" as a run *starts*, before the first test executes, so every run against a modified scene stalled the editor with `testsCompleted: 0` and no indication of why.
- `get_component_properties` takes `descend` (default off) to also return nested and array/list element properties by full path (`m_Sizes.Array.data[0]`), instead of the `<Array length=N>` placeholder.
- `get_component_properties`, `set_component_property` and `set_component_properties` read and write `Vector2Int` / `Vector3Int` properties.
- `get_component_properties` takes `max_properties` (1-5000, default 400) and reports the untruncated total. `descend` on a component holding a few thousand array elements is one property per element, which is a context bomb the caller could not see coming.
- The package compiles without the optional engine modules. `physics`, `physics2d`, `ai`, `terrain`, `particlesystem`, `audio` and `animation` each get a `versionDefine`, and the tools that need them are guarded, so a project that stripped a module (routine on mobile) no longer fails to build the whole assembly and lose all 266 tools with no hint why. The affected tools simply do not appear in `tools/list`.
- `[LongRunningTool(seconds)]` gives a tool its own call budget instead of every call sharing one ceiling. `build_player` gets 1800s and the bake tools 900s, and the transports plus the broker hold long enough to deliver the answer, so a real build no longer comes back as `Request timeout` while it keeps running in the editor.
- Script, file and asmdef writes are atomic: a temp sibling written first, then `File.Replace` through a `.bak`. A failure mid-write used to leave a truncated `.cs` that Unity then compiled. (Approach adapted from CoplayDev/unity-mcp, MIT.)
- `SECURITY.md` with a private reporting channel, plus a `## Security` link from the README.

### Changed
- **Client approval is off by default** (`Require client approval` in the Safety tab still turns it on). The gate asked the user to approve an *executable path*, which is the wrong unit: approving `curl.exe`, `node.exe` or `python.exe` — the runtimes MCP clients actually ship as — grants every script on the machine that uses the same runtime, so the question it asked could not be answered usefully. What it defends against is a local process, and a local process running as the user can simply add itself to `approved-clients.json` (a plain file in the user's own data directory) or skip the server and edit `Assets/` directly. Meanwhile the costs were real: a modal that blocks the editor loop, a pairing window that approves the first arrival without asking, and — until the fix above — a null context that refused every client with no dialog to answer. The protections that stop an attacker who has *no* local code execution are unchanged and remain on: the `Origin` check that keeps a web page from POSTing into the editor, and the project pin that keeps a stale config from reaching a sibling editor.
- `core` profile rebalanced from 38 to 42 tools so the default can build, not only inspect. It exposed one of the fourteen GameObject tools (`find_game_objects`) and no `save_scene`, so an agent on the default profile could read a scene and set properties on what already existed, but could not create an object, parent it, place it, or persist any of it — every one of those had to go through `execute_code`, which costs more tokens, can fail to compile, and returns nothing structured. That was also inconsistent: `set_component_property` is in `core` and `execute_code` covers it just as well. Added `create_game_object`, `create_primitive`, `delete_game_object`, `add_component`, `set_transform`, `set_parent`, `get_game_object_info` (the natural second step after `find_game_objects`, which was in `core` without it) and `save_scene`. Dropped four that a default does not need and `set_tool_profile` can fetch on demand: `get_code_patching_status` (reports a specific third-party plugin, SingularityGroup Hot Reload), `capture_simulator_view` (needs the Device Simulator window open, and screenshots already held four of the thirty-eight slots), and `simulate_editor_window_click` / `simulate_editor_window_key` (EditorWindow automation; the four game-view `simulate_*` tools stay). One asymmetry is left as it was: `enter_play_mode` / `exit_play_mode` without `set_paused` / `step_frame`.
- `execute_code` says in the response message when a snippet logged errors — `[2 logged errors] Command executed. First error: ...` — with `logged_error_count` and `first_logged_error` beside the existing `logs` array. Those errors were already returned, but only inside `logs`, behind a flat `Command executed.`, so a caller that read the message and stopped there took a snippet that reported failures as a clean run. `success` stays true on purpose: a snippet that logs an error deliberately still ran to completion, and flipping that would break every one of them.
- The default server port is derived from the project path (`8765 + sha256(path) % 100 * 10`) instead of being 8765 for every project. With several editors open the port a project ended up on depended on which one started first — `ResolveStartupPort` falls forward to the next free port on a collision — so the port a client config was written against was not the port that project held the next day, and a client aimed at the wrong editor. A derived default is stable across restarts, which lets every project keep the same short `kitwright` entry name in its own `.mcp.json`. Slots are 10 apart because that fall-forward scan probes ten ports: adjacent defaults would let one collision walk onto the next project's reserved port and displace that project in turn, which is the order-dependence this removes. Only a project with no settings file yet takes a derived port — an existing file holding 8765 may hold it because the user typed it, and nothing on disk tells that apart from the old shared default, so existing projects keep the port they have. Two projects can still hash to the same slot, which the fall-forward scan handles as before.
- `show_dialog` shows a Scene View notification and a console log instead of a modal dialog box. The tool blocked the editor — and therefore the MCP request that called it — until someone clicked OK, which is the one thing an agent-driven editor cannot rely on.
- `execute_code` blocks snippets that bind to `EditorUtility.DisplayDialog` / `DisplayDialogComplex`, the file and folder panels, `EditorWindow.ShowModal(Utility)`, `EditorSceneManager.Save*IfUserWantsTo`, or either `EditorApplication.ExecuteMenuItem` overload — the menu route reaches every dialog in the editor by path, and metadata cannot see which path a snippet will pass. Unlike the other blocked members these damage nothing, they hang the request that ran them, so they are refused regardless of the strict filesystem setting and the refusal does not offer `safety_checks=false` as a way through: retrying with checks off would freeze the editor rather than fail. `EditorApplication.Exit` joins the blocked list for the same reason `Environment.Exit` was already on it.
- `write_file` refuses to overwrite an existing file without `expected_sha256`, and `edit_script` now requires it outright. Both replace a file whole, and both used to do it blind: an agent that read a file, thought for a minute, and wrote it back would silently discard whatever the user saved from their IDE in between — the worst kind of failure for an editing tool, because nothing reports it and the work is simply gone. The precondition and its `STALE_FILE` response already existed; the parameter was just optional, so nothing used it. Creating a *new* file still needs no sha, since there is nothing to lose. `patch_script` and `edit_script_members` are deliberately left alone: `old_text` and the member name are preconditions of their own, and a whole-file hash would wrongly reject an edit to a member the user never touched.
- `read_file` returns `{path, length, sha256, content}` instead of bare content, so the hash needed by `write_file` and `edit_script` arrives with the read that the precondition is meant to protect. Fetching it separately just before writing would confirm the file as it is now rather than as it was when read, which is not a precondition at all. A file over the read cap comes back truncated, flagged, and deliberately **without** a sha: a rewrite composed from a truncated copy would otherwise pass the check and delete everything past the cut.
- `batch_execute` collapses everything it does into one Undo step and takes an `undo_label` for the name shown in Unity's Edit menu. Each command registered its own undo entry, and because the batch awaits across editor frames Unity's per-frame grouping did not merge them either, so reverting a twelve-command batch meant pressing Ctrl+Z twelve times — with no way to know how many, and overshooting ate the user's own earlier work. File writes, asset imports and play-mode changes sit outside Unity's undo system and are still not reverted by it.
- Every failed GameObject lookup now returns one code, `GAME_OBJECT_NOT_FOUND`, and carries the near-miss names that do exist. The same condition used to come back as `TARGET_NOT_FOUND` from 16 places and `GAME_OBJECT_NOT_FOUND` from 28, so a client that handled one did not recognise the other, and neither said anything beyond echoing the string that failed — while `reflect_api` had been answering a mistyped *type* with `candidates` and `get_component_properties` a mistyped *component* with the list of components present. The most-used lookup in the server was the one with no recovery information. Resolution is built once in `ObjectsHelper.NotFound`, so all 43 call sites gained it together: existing objects are ranked exact-but-wrong-case, then prefix, then substring, then edit distance, and returned as hierarchy paths. Wrong case is worth calling out — name matching is case-sensitive, so `canvas` fails against `Canvas` and that near-miss is now the first thing reported. A numeric target skips name matching entirely and gets the explanation it actually needs, that instance ids are reassigned by every domain reload. `TARGET_NOT_FOUND` survives in one place that is not a GameObject lookup at all: resolving a native object inside a memory snapshot, which had its own candidate list already. Breaking for any client keying on the old code.
- `Response.Error` accepts a `hint` and puts it beside `code` rather than inside `data`, so a caller reading a top-level `hint` finds one. Errors that already carried `data.hint` are unchanged.
- The URL written into client configs now includes the project pin: `http://127.0.0.1:<port>/p/<pin>/`. The server has always been able to refuse a request meant for another project — `PathTargetsAnotherProject` is tested — but nothing ever wrote a pinned URL, so every config was pinless, pinless is accepted, and the check could not fire. The comment claiming a re-run of Configure would upgrade an old config was wrong; it wrote the same pinless URL. This matters because ports are assigned by a first-come scan: a config written while this project held a port can end up aimed at whichever sibling editor owns it now, and that editor would answer and apply the edits to the wrong project. Existing configs keep working — a path with no pin is still accepted — and the auto-rewrite that repairs stale ports matches on the entry name, so it upgrades them on the next server start without the user re-running Configure.
- `get_hierarchy` prints each object's instance id as `Name #77166`, and takes `include_ids` (default on) to suppress it. The tree was the cheapest way to see a scene but the only thing it could not do was address what it showed: every id-taking tool needs a handle, and a bare name is ambiguous between siblings, so reading the hierarchy always cost a second `find_game_objects` call. The printed id is `ObjectIdCodec.GetSerializableId`, the same form `root_name` and every other target parameter already accept, so it round-trips. Turning it off is worth it only when browsing scene shape at depth over a few hundred objects.
- `get_component_properties` no longer returns `DisplayName` next to every property. It was Unity's inspector label, derived mechanically from the serialized name (`m_UiScaleMode` → `Ui Scale Mode`) and useful only to something drawing a GUI — measured at 31% of the response on a `CanvasScaler`, for a string the reader can already infer from the field it sits next to.
- `create_primitive` reports the created object's components. A primitive arrives with a MeshFilter, MeshRenderer and collider the caller never asked for, so the one thing worth confirming was the one thing the response omitted, and checking cost a second call. `create_game_object` is unchanged — an empty has nothing but its Transform to report.
- `create_game_object`, `create_primitive` and `duplicate_game_object` answer with `instanceId`, `name`, `path` — and, for a primitive, its components as `instanceId` + `type`. They used to return the same full description a read returns: the transform the caller had just passed in, `activeSelf`/`activeInHierarchy`/`isStatic`/`tag`/`layer`/`scene` at their untouched defaults, and each component's `fullType` next to its `type`. Measured against a competing server that answers a create with the handle alone, `create_primitive` cost 219 tokens to its 21; the shape here keeps the components that entry above argues for and still comes in near a third of the old size. Reads are untouched — `get_game_object_info` and `find_game_objects` are where the full description belongs.
- `create_primitive` takes `rotation` and `parent` (with `find_method`), so a primitive arrives where it belongs in one call. It already took `position` and `scale`, and `create_game_object` beside it already took `parent`, so the primitive was the one creator that needed a `set_transform` and a `set_parent` behind it — three round-trips for one object, measured at roughly a second each against this editor. A parent that does not resolve is reported before the primitive is created rather than after, so a rejected call leaves nothing in the scene.
- `get_hierarchy` takes `max_nodes` (default 500) and stops there, ending the tree with `... truncated at max_nodes=N`. Nothing bounded the walk: `depth` capped how deep it went, not how much it printed, so a wide scene returned every object under the depth limit and a scene of a few thousand could fill a response with a tree no one asked to read in full. The cap is spent per printed object, so an object skipped by `include_inactive: false` costs nothing.
- `get_console_logs` strips Unity and TMP rich-text markup from every message. The tags render as color in the Console window but reach a caller as literal `<color=#ff6b6b>` noise, which on a decorated logger line costs more than the message does — and `filter_text` was matching against the markup, so filtering for a word that happened to sit inside a `<b>` span behaved differently from the same word outside one. Only the documented tag names are removed, so a log containing generic type names (`List<int>`) or an XML payload survives intact.
- `execute_code` accepts a bare method body returning any type, not just `string`. The classless form already existed, but the generated wrapper declared `public static string Run()`, so `return 1 + 2;` failed to compile with CS0029 while `return "a" + b;` worked — the shortest snippet form only served one return type. The wrapper now returns `object`, which the invoker already stringified.
- The three docs tools return text with the URL angle-bracketed instead of a JSON object with a `url` field. A client that linkifies the raw result had nothing to stop the URL at, so it swallowed the closing quote and the next field and the rendered link pointed at `...Transform.Rotate.html%22,%22unityVersion%22...` — every docs link was dead on arrival. `>` is not a URL character, so the link now ends where it should. `fetch_docs` also gains from the change on its own: its payload is mostly prose and code, which JSON had to escape newline by newline, and each page is now a markdown section with its code examples repeated in fenced blocks after the prose, so the runnable code survives a `max_chars` truncation.
- `execute_code` safety checks now run a second pass on the compiled assembly, not just on the source text. The source rules anchor on how a call is spelled, so a namespace alias walks past them — `using IO = System.IO; IO.File.WriteAllText(...)` matches neither the bare `File.` rule (its lookbehind rejects the preceding dot) nor the literal `System.IO.File.` one. `CompiledCodeGuard` walks the compiled assembly's TypeRef and MemberRef tables instead and blocks on what the snippet actually binds to, which is the same `System.IO.File.WriteAllText` however it was written. It reuses the `safety_checks` argument and the strict-filesystem setting, and needs no new dependency — the metadata is read through `Module.ResolveType` / `Module.ResolveMember`. The two passes cover different evasions and neither is a sandbox: reflection that looks a member up by name binds only to `Type.GetMethod`, so the guard cannot see it and the source rules are what catch that case.
- `execute_code` now compiles with Unity's `dotnet` + `DotNetSdkRoslyn/csc.dll` toolchain in preference to `mono` + `MonoBleedingEdge` `csc.exe`, falling back to mono when it is absent. Both ship with the editor, but mono JIT-compiles csc.exe on every call: measured 1019 ms mean per compile against 211 ms for the dotnet host. It is also a newer Roslyn (4.3.1 vs 3.7.0), so snippets can use C# 10/11 syntax — `record struct`, raw string literals and list patterns previously failed to compile.
- `execute_code` resolves its compiler references once per domain instead of rescanning the AppDomain and stat-ing several hundred assembly files on every call, and keeps a single assembly per simple name (highest version) so two loaded versions of the same library cannot make snippet types ambiguous.
- `execute_code` waits on the Roslyn csc process with `WaitForExit` instead of polling `HasExited` every 25 ms and then sleeping a further fixed 25 ms, cutting about 40 ms off every call. The second wait is bounded because this runs on the editor thread.
- Tool documentation is generated from the sources rather than maintained by hand. The README table named 153 of the 263 registered tools: seventeen modules were absent entirely (Terrain, Assembly Definition, Prefs, Sprite Atlas, Addressables, Audio, Input Actions, NavMesh, Shader, Scene View, Volume, LOD/Constraint, Texture, Build, Sprite, Batch, Interop), taking `batch_execute` and `validate_script` with them, and a hand-kept table drifts again on the next tool added. `scripts/generate_tools_doc.py` walks the `[ToolProvider]` classes — partials included — and writes `TOOLS.md` with every tool, its `[Description]` and whether the `core` profile exposes it, plus the module summary spliced into both READMEs. `--check` exits non-zero when either file is stale, and the release checklist and PR template run it.
- The server advertises MCP protocol `2025-06-18` (the revision that introduced `structuredContent`) instead of `2024-11-05`, and `initialize` echoes the client's requested version when it is one of `2024-11-05`, `2025-03-26` or `2025-06-18`, falling back to `2025-06-18` otherwise.
- `request_recompile`, `memory_take_full_snapshot`, `simulate_editor_window_click` and `simulate_editor_window_key` are no longer marked `[ReadOnlyTool]`: they change project state, so `tools/list` stops sending `readOnlyHint` for them and clients will prompt instead of auto-approving.
- `delete_asset`, `delete_shader` and `delete_sprite_atlas` move the file to the OS trash (`AssetDatabase.MoveAssetToTrash`) rather than unlinking it, so a wrong delete can be undone by hand from the Recycle Bin. `delete_shader` no longer falls back to a raw `File.Delete`; it refreshes and retries once, then returns `DELETE_FAILED`.
- `list_directory` lost its `recursive` parameter. It never did anything — the listing was always top level — and the description now says so and points at `search_files` for recursion.
- Every file path is now resolved and checked against the project root, so a path outside the project (including one reached with `..`) is refused instead of read or written. `exists` answers `Does not exist (outside the project)` rather than erroring.
- `create_script` refuses to write over an existing file with `SCRIPT_EXISTS`, since it never asked for the hash of what it would destroy; `edit_script` (with `expected_sha256`) or `patch_script` is the way to replace one.
- Truncated results report the real total: `get_compilation_errors` heads its output with `N total, showing first M`, `find_game_objects` says how many it found before the `max` cap, and `list_shaders` returns `total` beside `count`.
- An enum property reads as `{ name, value }` instead of a bare display-name string — `value` is the underlying value and the only correct reading of a `[Flags]` mask, `name` is null when no single entry matches — and an integer written to an enum property is now that underlying value rather than a position in the display list. An object reference gains `assetPath` (null for a scene object) so a read is re-feedable to the write path, and a property type with no reading reads as `<unreadable Gradient>` instead of a bare `Gradient`. Affects `get_component_properties`, `get_scriptable_object` and `get_project_settings`.
- `edit_script` and `patch_script` run the full `validate_script` structural check on the result instead of counting braces only, as `edit_script_members` already did, so an unmatched parenthesis or unterminated literal introduced by the edit is refused too. The error code for a structurally broken result is now `SYNTAX_REGRESSION` rather than `UNBALANCED_BRACES`, its payload carries `problem` (which names the line) in place of the old `line` number, and an edit that truncates a sound file to nothing is now refused.
- A target name that matches more than one GameObject returns `AMBIGUOUS_TARGET` with the candidates instead of silently acting on whichever Unity enumerated first. Read-only tools are included: an agent that asked about `Player` and got an answer about one of three `Player` objects had no way to know it was told about the wrong one. Re-call with `find_method=by_id` and a candidate id, or `find_method=by_path`.
- `create_new_scene` refuses an occupied path with `SCENE_EXISTS` instead of saving over it. Saving over an existing scene asset keeps its GUID, so the build list and every `SceneAsset` reference silently resolved to the new empty scene. The path is also checked against the project root now, and a `save_path` with no trailing slash no longer lands beside the folder instead of inside it.
- A POST carrying an `Mcp-Session-Id` the server does not know returns 404 rather than being served as if the session were valid, so a client whose session died learns to re-initialize instead of continuing against state that no longer exists.
- `get_compilation_errors` heads a clean result with a warning when compiled assemblies are older than their source files, since "no errors" from stale artifacts is the one answer an agent acts on without checking.
- `get_editor_state` reports `isCompiling` only when the compilation pipeline actually said it started, so an agent stops backing off against a raw flag that can stay stuck true.
- `get_counters` returns `UNKNOWN_COUNTERS` with `requested_names` (was `unknown_names`) and `available_names` when none of the requested names match, and says so when the recorders had only just been started because `profiler_start` was never called.

### Removed
- `get_time_scale` — deleted, with `Time.timeScale` folded into `get_editor_state` instead. It returned four values and only one of them earned the round trip: `Time.time` duplicates the `timeSinceStartup` that `get_editor_state` already reports, `Time.fixedDeltaTime` is a project setting rather than runtime state, and `Time.deltaTime` read from a tool handler is whatever the last frame happened to cost at that instant — not a measurement the caller controls, and easy to mistake for a performance figure that `get_frame_timing` actually provides. The remaining value belongs on the tool an agent already calls to ask what the editor is doing; that tool exists precisely so inspecting the editor does not cost an `execute_code` compile. `set_time_scale` is unaffected — it validates its range and performs a real action. This also removes it from the `core` set rebalanced above, which lands at 41.

### Fixed
- The declared `com.unity.test-framework` dependency could not run the package's own tests. It pinned 1.1.33, which has no support for `async Task` test methods, and `McpServerTests` is the only file that uses them — eight tests — so on the declared 2022.3 floor they resolved to that version and came back `NotRunnable: "Method has non-void return value, but no result is expected"`. A newer editor hid it by upgrading the dependency to 1.6.0, which is why the same eight pass on Unity 6. Raised to 1.4.6, which still supports Unity 2019.4 and so cannot lift the package's own floor.
- CI ran its tests against a smaller tool surface than any real project has. The test job wrote a manifest with an empty dependency list; the editor still supplies its built-in modules, so `BoxCollider` compiles and physics works, but the `KITWRIGHT_*` versionDefines key off a manifest entry rather than the module actually being present — so every guarded tool was compiled out and never exercised. The job now declares the modules a real project's manifest lists. Stripping them stays the separate optional-modules job's job.
- Three tests asserted on what the host project happened to contain rather than on what they set up: the capability surface looked up `physics_raycast` unconditionally, the performance snapshot expected `GameObject.CreatePrimitive` to attach a `BoxCollider` (both only true with the physics module), and the shader count-cap test assumed the project ships several `.shader` files, so on a bare project the cap truncated nothing and printed no "showing" line.
- The SSE log-dedup test no longer asserts on machine speed. It sends three identical logs and requires them to collapse into one frame, but the dedup window was a hard-coded 100 real milliseconds and each send is a coroutine round trip, so a slow frame pushed a send outside the window and gave it a frame of its own. `LogDedupWindowMs` is settable now, next to `PingIntervalMs` and `SessionTtl` which are settable for the same reason.
- A dead `Mcp-Session-Id` is refused with HTTP 404 in broker mode, not with a 200 carrying a JSON-RPC error. 404 is the status the MCP spec makes a client re-initialize on, and the direct transport already answered it; the broker could not, because the editor had no way to tell it which status to write, so the refusal travelled as error `-32001` inside a successful response. Clients have no rule for that: the connection kept looking healthy, the client kept sending the dead id, and every tool call failed until the user restarted it — the exact situation after the editor restarts, which is when a session dies. Broker protocol v6 adds `X-KitWright-Broker-Status`, the editor sets it on the refusal, and the broker honours it for 4xx/5xx only so a malformed header cannot rewrite a good response. The body is the same HTML the direct transport sends, so the two transports are now indistinguishable to a client. A v5 broker still running is replaced by the version bump.
- `ping` is answered with an empty result instead of `-32601 Method not found`. The method is a MUST in the MCP spec and it fell through the dispatch switch, so a client using it as a liveness check read a working server as a broken one.
- The broker refuses a request whose pin belongs to another project, as the direct transport already did. Writing the pin into the URL only closed the hole for direct HTTP: the broker is the default transport, and it dropped the request path entirely before handing the body to Unity, so a config left on a stale port still reached whichever sibling editor now owns that port, and that editor answered and applied the edits to the wrong project. The broker is passed the pin it serves at spawn and replies 404 to a mismatch. A pinless path is still accepted by both, since configs written before pinning are in the wild. The broker protocol version is bumped so an already-running broker without the check is replaced rather than reused.
- The `/p/` path marker is matched case-insensitively. The pin comparison beside it always was, so `/P/<pin>/` read as pinless and walked straight past the wrong-project check.
- The status tooltip, the browser status page and the generated skill's `curl` example carry the pinned URL too. All three named a pinless `8765`, which after per-project ports is a URL that reaches a sibling project rather than this one.
- The config auto-rewrite no longer repoints a global entry that belongs to another project. Every project writes the same `kitwright` entry name, and the global file holds one of them, so the editor that started last took the entry over — now an entry already pinned to a sibling project is left alone. A project-scoped `.mcp.json` is still repaired whatever pin it holds, since that file belongs to the project it sits in.
- `save_scene` no longer opens a file picker on a scene that was never saved. `SaveScene(scene)` raises Unity's save panel when the scene has no asset path, and that modal blocks the editor loop this call returns on, so it takes an optional `path` and returns `SCENE_HAS_NO_PATH` when neither is available. That `path` is only for a scene that has none: `SaveScene(scene, otherPath)` is Save As, not a copy — it repoints the open scene at a new asset with a new GUID and leaves the original holding the pre-edit content — so passing a different path for an already-saved scene returns `SCENE_ALREADY_HAS_PATH` instead.
- Console log push survives a modal dialog. `UnityLogsRepository` subscribed to `Application.logMessageReceived`, which only fires while the editor loop runs, so the notifications that exist precisely to reach a client while the loop is stopped were the ones that stopped with it. It subscribes to `logMessageReceivedThreaded` now, delivered on the thread that logged; the cache was already behind a lock and the SSE broadcast already writes off the editor thread.
- A log repeated inside the 100ms dedup window is counted instead of dropped. A client watching the console saw one line and could not tell whether it fired once or five hundred times, which is the difference between a stray error and a loop. The next notification for that message carries `[previous message repeated Nx]`.
- `JsonCodec.Serialize` writes anonymous types as JSON objects. Anything that was not a dictionary, list or scalar fell through to `ToString()`, so `new { a = 1 }` reached the wire as the quoted blob `"{ a = 1 }"` — which is how the SSE notifications shipped malformed until a test caught them. Types Unity formats itself, `Vector3` and enums among them, keep the string fallback: reflecting a `Vector3` recurses forever through its `normalized` property.
- `execute_menu_item` refuses menu paths known to open a modal dialog, a file picker, or quit the editor — `File/Save As`, `File/Open Scene`, `File/Build And Run`, `Assets/Import New Asset`, `File/Exit` and friends. It ran any path at all, and a modal is worse here than a slow call: it stops the editor loop that this very call has to return on, so the request could never complete and only a human clicking the dialog could unstick it. The refusal names the dedicated tool to use instead, and `allow_modal=true` runs one anyway for someone sitting at the editor. A prefix list is a heuristic, not a proof — a localized menu or a third-party item can still open a modal, which is what the `EDITOR_NOT_PUMPING` watchdog is for.
- A call made while a modal dialog is open in Unity now returns `EDITOR_NOT_PUMPING` naming the cause, instead of hanging until the client's own timeout. A modal runs its own message loop, so `EditorApplication.update` stops firing and queued work cannot run — the caller saw only `Tool call timed out after 30000ms`, which says nothing about a dialog waiting for a click in another window. A watchdog fires at 20s, under the 30s most clients allow, and only when the pump itself has been stale for 5s: a tool that is merely slow keeps the loop ticking while it awaits, so it is not mistaken for a blocked editor. The queued call still runs once the editor resumes.
- Two tests no longer open Unity's "Scene(s) Have Been Modified" prompt. `HierarchyAndSceneInfo_IncludeLoadedAdditiveScenes` and the performance multi-scene test replace the open scene with `EditorSceneManager.NewScene`, and guarded only on whether the previous scene setup could be restored — not on whether it had unsaved changes. Run against a dirty scene in an interactive editor they raised the save prompt mid-run, which blocked the editor loop and stalled every MCP call until a human clicked a button. They now skip in that state. The shipped tools were already safe: `open_scene` and `create_new_scene` return `SCENE_HAS_UNSAVED_CHANGES` rather than prompting.
- The server no longer shifts to `port+1` because of a listener it leaked itself. The orphan-reclaim arm — set on the restart paths so the next start can close a listener a Hot Reload patch left bound — was consumed inside the transport's bind, which happens *after* the service has already probed for a free port. So in the one scenario the mechanism exists for, the probe read our own orphan as taken, fell forward, and logged `Port N is in use by another process` naming a process that was us. The sweep now runs before the probe; the transport still consumes the arm, and closing an already-closed listener is a no-op.
- The port-probe socket in `IsPortBindable` now clears its inherit flag like the real listener does. It binds and unbinds in a moment, but a process spawned inside that window still receives a duplicate handle and keeps the port alive after Unity exits — the exact failure `DisableHandleInheritance` was written for, on the one socket that skipped it.
- `cancel_test_run` now clears a job record Unity has no run for, instead of failing and leaving it. `TestRunnerApi.CancelTestRun` returns false when there is nothing to cancel — which is exactly the state after a run whose runner never started — and the old code took that as failure and left the record marked `running`. Every later `run_tests` then returned `TESTS_ALREADY_RUNNING` and pointed at a `cancel_test_run` that could never succeed, so one lost run disabled test running for the rest of the editor session. A false now clears the tracked record and says so; passing a `job_id` that is neither Unity's nor the tracked one still errors.
- The HTTP transport no longer orphans an accepted socket when the listen loop is cancelled at the instant a connection arrives. `Task.Run(..., ct)` drops the delegate outright once `ct` is cancelled, and the `using (client)` that closes the socket lives inside that delegate — so a connection accepted in the last moments of a shutdown was left to the finalizer. The token is no longer passed to `Task.Run`; `HandleClientAsync` already honours it internally.
- Reading a request off an accepted connection is now bounded to 30 seconds. Nothing capped how long a peer could hold a connection open without sending anything: `MaxHeaderBytes` limits size, not time, and no `ReceiveTimeout` was ever set. A loopback client that connected and stayed silent pinned one handler task and one socket for the life of the domain, with nothing limiting how many could accumulate. The read now runs under a linked token that closes the socket when it expires — closing is what unblocks it, since Mono does not abort an in-flight `NetworkStream.ReadAsync` on cancellation alone.
- `fetch_docs` no longer leaves a `<div` fragment at the end of every ScriptReference page. The footer cut anchored on the `class="footer"` attribute, which sits inside the tag, so the opening `<div ` survived with no closing bracket for the tag stripper to match. The cut now moves back to the `<` that opens the element.
- Hot Reload detection no longer assumes a healthy plugin means Unity's compile pipeline is suppressed. The plugin installs its `AssetDatabase.Refresh` / `RequestScriptCompilation` detours only when its own `disableCompilingFromEditorScripts` setting is on *and* its server is healthy, so with that setting off Unity compiles as usual while `get_code_patching_status` still reported `suppresses_compilation: true` — and `request_recompile` / `execute_code` skipped waiting for a compile that was in fact running. The flag now reads `CompileMethodDetourer.detouredMethod`, the plugin's own record of whether the detour is installed, and the status response reports it as `compile_detour_installed`. The old heuristic remains as a fallback for plugin versions where that field cannot be read.
- The CodeDom fallback compiler no longer fails on every snippet when `netstandard.dll` is loaded. `CSharpCodeProvider` cannot follow type forwarding, so referencing netstandard alongside `mscorlib` / `System.Runtime` / `System.Private.CoreLib` / `System.Collections` made common types such as `List<T>` resolve in more than one assembly; those four are now dropped from the CodeDom reference set when netstandard is present.
- Sources under `Library/PackageCache` no longer count as pending script changes. Packages that regenerate a file on import (for example a generated SDK-version provider) always leave it newer than an assembly Unity has no reason to rebuild, so every refresh concluded scripts were stale and escalated to `RequestScriptCompilation(CleanBuildCache)` — which recompiled the project, regenerated the file, and armed the next call to do it again. On an affected project every default `execute_code` triggered a full recompile and a domain reload.
- `request_recompile`, `wait_for_compilation` and `execute_code` return without waiting for a compilation that cannot happen. When nothing looked stale before the refresh and the editor is neither compiling nor importing, the compile-start detection is skipped instead of running to its timeout: measured on an up-to-date project, `execute_code` with its default refresh went from 3209 ms to 771 ms and `wait_for_compilation(force_refresh)` from 2464 ms to 527 ms.
- `request_recompile`, `wait_for_compilation` and `execute_code` no longer stall for a few seconds per call while SingularityGroup Hot Reload is loaded. The plugin detours `AssetDatabase.Refresh` / `RequestScriptCompilation` into no-ops, so the busy-wait that looks for a compile start could only ever run to its timeout; the plugin is now detected before that wait instead of after it, and the wasted script-state scan is skipped too.
- Stopping Hot Reload now restores normal compile handling. Detection only checked whether the plugin assemblies were in the AppDomain, which stays true after the user stops the plugin, so `request_recompile` kept reporting a suppressed compile pipeline and refused to escalate — the exact step its own message tells you to take. It now also requires a healthy plugin server.
- The transport dropdown names the mode that is actually the default. Broker mode has defaulted to on since it shipped and direct HTTP is only the fallback when the broker cannot start, but the dropdown labelled Direct HTTP `(default)` and broker mode `(Experimental)` — the window contradicted the value it had selected, and both READMEs repeated the labels back.
- The docs name the menu the package actually registers. Every setup path told the reader to open `KitWright > MCP Server`, `KitWright > Tool Exposure`, `KitWright > MCP Settings`, `KitWright > Project Skills` or `KitWright > Check for Updates`; the package registers one item, `Window > KitWright > MCP Window`, and those are tabs inside it — a first-time reader looked for a top-level menu that does not exist. The update entry was never a menu item at all, it is a button in the window header. The same fix covers `README_CN.md`, `CONTRIBUTING.md`, `Documentation~/index.md`, the release checklist and the PR template. `Documentation~/index.md` also claimed 91 tools and a separate plugin-settings window, and both READMEs offered LM Studio as a one-click configuration target, which it has never been — its `mcp.json` path varies too much to guess, so the entry is documented for manual paste instead.
- The broker refuses a request whose `Origin` is not loopback, as the direct transport already did. Any web page the browser visits can POST to the broker's loopback port, and the broker read the body without ever looking at the header, so DNS rebinding reached the editor through the default transport.
- Reading a request off a broker connection is bounded to 30 seconds (`ReceiveTimeout`), matching the direct transport. A peer that connected and stayed silent held a broker handler thread and socket open indefinitely.
- The broker holds `initialize` and `tools/list` for a 45 second grace period after the editor detaches, instead of answering `-32001` for the whole domain reload. A reload detaches the session for 10-30 seconds while the broker process stays up, so a client that reconnected or refreshed its tool list in that window was told the server was dead and never re-probed. A cold broker that never had an editor attached still fails fast.
- Compile gates no longer trust `EditorApplication.isCompiling` on its own. The raw flag can stay true with no compile running, which starved the post-reload server restart, the no-throttle lease's expiry, and the pending external-sync recovery — each of them an untimed wait. They now require the compilation pipeline to have reported a start; the timeout-bounded waits deliberately still read the raw flag, so a compile queued in a fresh domain is still waited out.
- `get_console_logs` keeps the whole message body. Only the first line was retained, so a stack-carrying `Debug.LogError` lost its detail, and `filter` never matched anything below the first line. The message/stack split now happens at the first real stack frame.
- A Unity log no longer pays JSON serialization when nothing is listening. The SSE notification was built for every log line whether or not a client had subscribed, which is pure overhead on a project that logs in `Update`.
- Tool discovery no longer loses every builtin because one assembly is broken. A `TypeLoadException` from a foreign assembly, or a `[ToolProvider]` attribute whose constructor throws, aborted the whole scan pass; each type and assembly is now guarded on its own.
- First-connect client approval runs on the broker transport, which is the default — until now the gate only covered the fallback HTTP transport, so the protection most users were told they had never ran. The broker forwards the connecting client's port and the editor resolves the process from it.
- A client's `Mcp-Session-Id` survives the broker, which is the default transport. The broker dropped the header in both directions, so `initialize` never returned an id and every broker client landed in the editor's one sessionless slot: the revision the most recent client negotiated applied to all of them (an old client silently stripped `structuredContent` from everyone else's results), `logging/setLevel` from one client moved everyone's level, and an id the server had never issued was served as if it were live. The id now crosses in both directions under `X-KitWright-Broker-Mcp-Session`, `initialize` mints it in the editor and the broker puts it back on the wire, and a request carrying an unknown id is refused — as a JSON-RPC error rather than the direct transport's 404, since the broker writes its own HTTP status. Broker protocol 5, so a v4 broker left running is replaced rather than reused. The version number now lives only in `MCPBrokerProtocol.Version` and reaches the broker as `--protocol`, beside the `--port` / `--token` / `--pin` it already took: the broker used to declare its own copy, and bumping one side left it answering the old number forever — failing every health probe, being replaced, and failing again, while the transport quietly served requests over in-process HTTP instead.
- A parameter name a tool does not have is refused with `UNKNOWN_PARAM` and the list of names it does take, instead of being dropped in silence. The invoker walked the method's parameters and pulled each one out of the argument dictionary, so anything left in the dictionary was never looked at: the tool ran on its defaults and reported success. `run_tests` called with `test_filter` instead of `test_names` ran the entire suite and answered "all green"; `get_console_logs` with `filter` instead of `filter_text` returned unfiltered logs. Both look like an answer to the question that was asked. Checked before `MISSING_PARAM` so a misspelling is reported as the misspelling rather than as the parameter it failed to fill.
- `find_game_objects` with `find_method=by_component` reports an ambiguous type name instead of returning an empty list. The locator's contract is a list of GameObjects, so the ambiguity `TypeResolver` refuses had nowhere to go and came back as "no matches" — the one answer that reads as a definite fact about the scene. It now raises, and the invoker turns it into the same `AMBIGUOUS_TYPE` with candidates the other tools return.
- The broker compile drains stdout and stderr concurrently. It read stdout to the end first, so a compiler that filled the stderr pipe buffer blocked writing it and never closed stdout — deadlocking the editor thread outright, since the 20 second `WaitForExit` guarding the call was never reached.
- The broker logs to its own file beside its exe rather than to stderr. It is spawned without redirection, so stderr was the handle it inherited from Unity: broker lines landed in the middle of `Editor.log`, interleaved with Unity's own writes and outliving the editor that opened it. Redirecting the pipes instead would eventually block the broker, which survives editor restarts and would fill a pipe nobody is reading.
- `Library/Bee/artifacts` is scanned once per compile instead of once per call. One `request_recompile` captures script-change state five times and each capture walked the artifact tree recursively — thousands of files — for timestamps only a compile can change. Held until the compilation pipeline reports a start or finish.
- A short type name shared by several loaded types is reported as ambiguous instead of missing. `TypeResolver` already refused to guess, but it refused by returning null, and every caller reads null as "no such type" — so `add_component` on a name that genuinely exists twice (`PixelPerfectCamera` in `UnityEngine.U2D` and `UnityEngine.Rendering.Universal`, say) answered `COMPONENT_TYPE_NOT_FOUND`, sending the caller to look for a typo in a name that is right there in the project. Those callers now return `AMBIGUOUS_TYPE` with the fully qualified candidates. `create_scriptable_object` also stopped taking the first namesake it scanned: its fallback assembly walk returned on first hit, which reintroduced exactly the guess `TypeResolver` exists to refuse.
- The approval dialog can actually appear. `[InitializeOnLoad]` runs the gate's static constructor before Unity installs the main-thread `SynchronizationContext`, so the captured context was always null, and every prompt took the null branch: refused, silently, with no dialog and nothing logged. The gate was therefore unanswerable on the default transport — the only way past it was the pairing window, which approves without asking. The context is now captured from `EditorApplication.delayCall`, on the main thread once the editor is up, and a refusal that still finds no context says so in the console instead of failing mute.
- The approval gate stays off when it is off. `RequireApproval()` fell back to `true` whenever the settings service was not resolvable, and during a domain reload it is not: the DI root has not been built yet. Autostart plus broker mode means that is exactly when the first requests land, so a machine with approval switched off still raised the dialog as the editor came up — including for clients the resolver could not name, whose prompt grants every unidentified process. The fallback now reads the shipped default, and that default is one constant shared with the gate rather than a second copy free to disagree with it.
- `get_console_logs` no longer reports "Console is empty" for a console full of entries. `LogEntries` mirrors the Console window, so a user who muted Log and Warning while chasing an error filtered our read too; the three severity bits are forced on for the read and restored after, and a zero result now says the window's search box also filters it.
- A short type name that matches more than one type no longer resolves to whichever was scanned first. `add_component`, `get_component_properties` and friends now refuse the ambiguous name instead of acting on the wrong type; a fully qualified name still resolves, and a Unity type still beats a project namesake.
- A tool call that timed out is dropped from the editor queue instead of running later. The client had already given up, so the work landed after the fact and a retry could apply a mutating tool twice.
- `bake_lightmaps` and `bake_nav_mesh` refuse in Play Mode with `BAKE_REQUIRES_EDIT_MODE` naming `exit_play_mode`, rather than baking into a session that is about to be thrown away.
- The C# source masker no longer mis-lexes a lone apostrophe (`#region Don't touch`) or a C# 11 raw string, which made `validate_script`, `patch_script` and `edit_script_members` call a sound file broken and refuse the edit. An unterminated raw string is now named instead of surfacing as a brace count.
- `structuredContent` is decided per session rather than by one shared field, so a single client on an older revision no longer strips the field from every other client until the next domain reload.
- The CodeDom fallback compiler no longer treats mcs's phantom BOM diagnostic (an entry with no error number) as a compilation failure, and its reference list is deduped.
- The broker is not stopped when a batch-mode editor quits. Our own CI runs `-batchmode -runTests`, so a test run could kill a broker an interactive editor was using.

## [1.0.0] - 2026-08-11

First Unity Asset Store release.

### Changed
- **Renamed the project from GameWright to KitWright.** The package id is now `com.kitwright.unity.mcp`, the menu lives under `Window > KitWright`, and the MCP server entry written into client configs is `kitwright`.

### Fixed
- Broker mode, the reported package version and icon loading now work when the package is installed under `Assets/` instead of `Packages/`, at any folder name. `PackageInfo` returns null for an `Assets/` install and the previous hardcoded `Assets/unity-mcp` / `Packages/com.kitwright.unity.mcp` roots never matched, so broker mode silently fell back to in-process HTTP and the window reported `v0.0.0`. Paths now come from the asset database.
- The broker source path was resolved in a static constructor, before the asset database is queryable, and the resulting null was cached for the whole session. It is resolved lazily and cached only once found.
- The six-hourly background update check no longer logs a warning when it fails. An offline machine, a rate-limited GitHub API or a repository with no published release dropped warnings into the console of users who never asked for a check; failures are only reported for checks the user started.

### Upgrading from 0.6.x
- Remove the old `com.gamewright.unity.mcp` package before installing this one; Unity treats them as two separate packages.
- Reopen the MCP Server window and click **Configure** once. It rewrites the client config and drops the stale `gamewright` entry.
- Project skills installed under the old name (`.claude/skills/gamewright-*`, `.cursor/rules/gamewright-*.mdc`) are not migrated automatically — reinstall them from the Skills tab and delete the old files.
- Local settings reset to defaults, since they now live in `UserSettings/KitWrightMcpSettings.json`.

## [0.6.0] - 2026-08-05

### Added
- Autostart-on-Unity-open toggle for the MCP server, exposed at the top of the Settings tab.

### Removed
- Settings tab `Requirements` checklist (`Run In Background`, `Recompile after finished playing`).
  Nothing read either value. The recompile one warned about a failure `DomainReloadHandler` already
  recovers from and reports back to the client, and the `Run In Background` fix wrote a shipped
  `PlayerSettings` value into `ProjectSettings.asset` for an editor-only convenience.
- Project Skills `Upgrade Skills` and `Refresh` buttons. `Upgrade Skills` called the same
  `ApplyConfiguration` as `Apply Skills` (which always rewrites managed files at the bundled
  versions), and could silently drop pending toggle changes; `Refresh` duplicated the rebuild that
  already happens on tab switch and after every action. `Apply Skills` is now the only action.
- FlowWright (flow record/replay, atom graph), UI reconstruct (`match_sprites_to_image`,
  `capture_match_compare`), and the `match` / `playtest` / `agentplay` skills. These move to a
  separate commercial package; the free package keeps the full MCP server and editor tool surface.

### Fixed
- Legacy tools returning bare JSON without a `success` field had the whole payload escaped into
  `message`, forcing clients to parse twice. The payload is now kept structured under `data`.
- The test assembly could not reference Newtonsoft.Json (it referenced a non-existent
  `Unity.Newtonsoft.Json` assembly instead of the precompiled DLL), so the whole EditMode suite
  had never compiled. Now builds and runs: 349 tests.
- Removed an unreachable `System.IO.File.Delete` safety rule shadowed by the broader
  `File.Delete` rule, and added coverage for the previously untested `System.Diagnostics.Process`
  rule.

### Changed
- Editor window UI moved from `Editor/MCP/Server/` to `Editor/UI/`; `MCP/Server/` now holds only
  transport and server logic.
- Merged single-implementation interface files into their implementations (12 fewer files).
- Recent Activity token counts are labelled `~N tok` — they are a rough `chars / 4` estimate of
  response size, not a real tokenizer.

## [0.5.1] - 2026-07-12

### Added
- The MCP Server window's **Tool Exposure** row now includes a settings button that opens the full Tool Exposure window with the active `core` or `full` profile pre-selected.
- `get_test_job` now reports `possiblyStuck`, the stalled phase, inactivity duration, and current test when Test Runner callbacks stop. Runner startup uses a 30-second threshold while a known running test gets 120 seconds to avoid premature warnings for normal long tests.
- `execute_code` gained a `skip_refresh` option that bypasses the pre-compile `AssetDatabase.Refresh` + wait-for-ready. Use it for read-only inspection snippets or during a live Play Mode session you must not disturb: the default refresh can trigger an import/domain reload (from your own or another actor's pending changes in a shared editor) that wipes Play Mode runtime state. When skipped, external file edits since the last compile are not picked up.
- Screenshot captures now auto-fall back to a file when the payload would exceed a safe transport size, instead of emitting an oversized base64 payload that reliably drops the client socket. The threshold is measured on the raw PNG (512 KB) so the ~1.33x base64 expansion still lands under the drop point; `capture_multiview`'s default (inline) path is covered too -- it spills all frames to files when their combined size is too large. The response carries the same `{ path, bytes, fell_back_to_file: true }` shape as an explicit `save_to_file`. Small captures are still returned inline; `save_to_file` remains an explicit override.
- Added dedicated capability tools for asset import settings, dependency/broken-reference inspection, mesh and material inspection, 2D/3D physics queries, particle preview control, project settings, Undo/Redo, component batch operations, lighting, and reflection-based PlayableDirector evaluation. Reverse dependency scans are bounded and yield between batches; mutating setters validate the whole request before writing.

### Changed
- Project Skills now manage only a delimited KitWright block inside shared `AGENTS.md` and `CLAUDE.md` files, preserving all hand-authored content outside it. Exact legacy generated files migrate automatically; edited legacy single-marker files are left unchanged with an explicit migration error instead of being overwritten.

### Fixed
- Built-in tools that accept a GameObject target now consistently resolve names, hierarchy paths, and instance IDs through `ObjectsHelper`, including inactive objects and additively loaded scenes. `get_console_logs` also supports an opt-in `include_stack_trace` argument with independently capped, normalized stack output.
- `MCPBrokerTransportTests` (8 of the package's 61 EditMode tests) hardcoded the broker source path as `Assets/unity-mcp/Editor/MCP/Server/Broker/keepalive-broker.cs.txt`, which only exists when this repo's own source is checked out directly under `Assets/` -- it always failed in any project that installs the package properly (embedded, git, or registry), since there the source lives under `Packages/<name>` or `Library/PackageCache/<name>@version`. Both the filesystem lookup (`ResolveBrokerSourcePath`, used by 7 tests via `CreateBrokerPaths`) and the `AssetDatabase` lookup (`BrokerSource_IsVisibleToAssetDatabaseForUnityPackageExport`) now resolve through `PackageInfo.FindForAssembly` first and only fall back to the old `Assets/unity-mcp` path when the assembly isn't part of a package (preserving this repo's own dev-checkout layout).
- Tool argument parsing no longer silently coerces a malformed value to `default(T)` / `Vector3.zero` and runs with it. A missing required value now returns `MISSING_PARAM`; a value that cannot be parsed into its parameter type (a non-numeric int, a two-component `'x,y'` passed where an `'x,y,z'` vector is expected, an out-of-range enum, etc.) returns `INVALID_PARAM` with the parameter name, provided value, and expected format. This applies to reflected and manually registered tools, and `set_transform` / `create_primitive` validate vectors before modifying the scene.
- Several `Profiler`/memory tools returned error/precondition conditions (`get_object_memory` with an empty target, `memory_list_snapshots` with no snapshots, `get_frame_timing` with no timing available, `frame_debugger_get_events` with no events) as bare human-readable strings on the success channel, so callers -- and the plugin's own `IsError()` check -- treated the failure as success. They now return structured `{ success: false, code }` errors.
- Every tool response is now uniformly parseable as `{ success, ... }`. Legacy tools that returned a bare human-readable string on success (while only errors were structured JSON) are wrapped in `{ success: true, message }` by the result serializer. Image data URIs and strings that are already a `{ success: ... }` envelope pass through untouched, so screenshots still render as images and error envelopes are never double-wrapped.

### Contributors
- Thanks @dehuaichendragonplus for the fixes and feature proposals behind this release (#30, #31, #32, #33, #34, #35, #36).

## [0.5.0] - 2026-07-07

### Added
- Project Skills now include explicit bundled skill versions in generated `AGENTS.md`, `CLAUDE.md`, Cursor rules, and the KitWright skill manifest, so users can tell when installed project guidance is stale.
- The Project Skills window now shows installed skill file status and exposes an `Upgrade Skills` action when KitWright-managed guidance files are behind the package version.
- `save_to_file` option on all screenshot tools (`capture_game_view`, `capture_scene_view`, `capture_simulator_view`, `capture_multiview`): writes the PNG to disk (default `Library/KitWrightMcp/Screenshots/`, or a custom `output_path` that resolves inside the Unity project root) and returns the file path instead of base64 image data. High-resolution captures previously produced multi-megabyte base64 payloads that could break MCP transports; saving to a file and letting the client read it sidesteps the payload entirely.
- New `capture_editor_window` tool: screenshot any open EditorWindow (Inspector, Console, custom tool windows...) by title or type name. Captures directly from the window's internal GUIView render surface, so the window does not need to be unoccluded on screen.
- New `raycast_at_point` UI diagnostic tool: runs the live EventSystem's RaycastAll at a screen point and reports the full ordered hit chain (hierarchy path, raycast-receiving Graphic, raycastTarget flag, sorting info) plus the `IPointerClickHandler` that would actually receive a click there -- or the element silently swallowing the click when the topmost hit has no handler anywhere up its parent chain (the classic invisible-raycast-blocker bug). Coordinates can be pixels or normalized, with bottom-left or top-left origin; sizes are resolved against the real Game View render resolution rather than the editor window.
- Real Unity memory snapshot (.snap) tools -- the full-detail captures the Memory Profiler package opens for object-level reference-chain analysis, complementing the existing lightweight aggregate-counter snapshots (`memory_take_snapshot`): `memory_take_full_snapshot` captures via `Unity.Profiling.Memory.MemoryProfiler.TakeSnapshot` (configurable CaptureFlags, async completion, written into the Memory Profiler package's snapshot folder), `memory_list_full_snapshots` lists them, and `memory_open_snapshot_in_profiler` loads one into the Memory Profiler window (com.unity.memoryprofiler package required for that last step only; capture itself is a core-engine API). Combine with `capture_editor_window('Memory Profiler')` to inspect the loaded analysis visually.
- Two headless structured-query tools for those same .snap files -- no window, no screenshot: `memory_query_top_objects` ranks native objects (Texture2D, Mesh, RenderTexture, etc.) by size with an optional type-name filter, and `memory_query_references` returns what references a given object or what it references (`referenced_by`/`references_to`), resolving the target by name or by the index `memory_query_top_objects` returned. Both load the snapshot via `SnapshotDataService.LoadWithoutLoadingToUI` (the package's crawler, without opening any UI) and reflect into the crawled `CachedSnapshot`'s native object table and connection graph.

### Changed
- Updated README tool inventory and install snippets for the expanded 128-tool surface.

### Contributors
- Thanks @dehuaichendragonplus for the screenshot/UI diagnostics and real memory snapshot PRs (#28, #29).

## [0.4.9] - 2026-07-03

### Added
- Added 13 Profiler tools (category `Profiler`): `profiler_start`/`profiler_stop`/`profiler_status` for session control; `get_frame_timing`/`get_counters` for CPU/GPU frame timing and persistent `ProfilerRecorder` counters; `get_object_memory` for per-asset/GameObject runtime memory footprint; `get_top_memory_objects` for ranking ALL loaded objects of a type by memory (the "which objects are consuming it" follow-up to a snapshot diff); `memory_take_snapshot`/`memory_list_snapshots`/`memory_compare_snapshots` for lightweight aggregate memory snapshots (not real `.snap` files); `frame_debugger_enable`/`frame_debugger_disable`/`frame_debugger_get_events` for driving the Frame Debugger via reflection. See [PROFILER_TOOLS.md](PROFILER_TOOLS.md) for the full reference, implementation notes, known limitations, and test report.
- Added prefab stage editing tools: `open_prefab_stage` opens a prefab asset in Prefab Mode for isolated editing (hierarchy/component tools and `execute_code` then operate on the prefab contents), `save_prefab_stage` persists edits back to the `.prefab` asset without closing, and `close_prefab_stage` returns to the main stage with an explicit save/discard choice. Closing clears the stage's dirty flag first so a blocking "save changes?" modal can never stall the MCP request.
- `get_console_logs` gained two optional parameters: `group_duplicates` collapses repeated identical messages into one "message (xN)" line (in a real project this compacted 100 cached entries down to 20 unique lines, keeping spammy Animator warnings from drowning out unique entries), and `filter_text` filters entries by a case-insensitive substring. Both apply to the cache and console read paths; default behavior is unchanged.
- Added ScriptableObject asset tools: `create_scriptable_object` creates a new asset of any ScriptableObject-derived type, `get_scriptable_object` reads all serialized properties (including `[SerializeField]` private fields), and `set_scriptable_object_properties` writes fields with a per-field success report and persists via `SaveAssetIfDirty`. Reuses the component property machinery (`ComponentSerializer` signatures widened from `Component` to `UnityEngine.Object`, source-compatible for existing callers).
- Added Animator runtime control tools: `get_animator_state` reads the current state (state name resolved from the controller when possible, including through AnimatorOverrideController) plus all parameters with current values; `set_animator_parameter` sets a parameter by name with automatic Float/Int/Bool/Trigger type detection; `play_animator_state` plays a named state and force-evaluates the animator once in Edit Mode so poses apply without entering Play Mode (useful for driving UI to a known state before a screenshot).
- Added Unity Test Runner integration with an async job pattern: `run_tests` starts an EditMode/PlayMode run (with optional test/category/assembly filters) and returns a job id immediately; `get_test_job` polls progress (completed/total) and final results (pass/fail/skip counts plus failure messages and truncated stack traces); `cancel_test_run` cancels a stuck run (requires com.unity.test-framework 1.3+, resolved by reflection and reported as unsupported on the 1.1.x that Unity 2022.3 bundles). Job state lives in SessionState and the results callback re-registers on every domain load, so PlayMode runs that reload the domain mid-run still report completion.
- Declared `capabilities.tools.listChanged` in the `initialize` response and implemented lazy `notifications/tools/list_changed` delivery: when the exposed tool set changes (Tool Exposure save, newly registered tools after a recompile), the next client request that accepts `text/event-stream` receives an SSE response carrying the notification before the JSON-RPC result, so MCP clients such as Claude Code refresh their tool list without a session restart. Supported on both the direct HTTP transport and broker mode (broker protocol v2 with Accept/Content-Type passthrough).

### Changed
- MCP Server panel UX improvements to the transport/broker controls:
  - The transport selector is now a "Transport Mode" dropdown (`Direct HTTP (default)` / `Broker Mode (Experimental)`) instead of a checkbox, so the two transports read as an explicit mutually-exclusive choice instead of an on/off flag.
  - The "Broker Mono Path" field now shows the real effect of the "leave empty to auto-detect" default instead of always rendering blank: when no override is set, the field displays the actually auto-detected Mono executable path (display-only — it does not persist as an override), and if auto-detection fails, the field stays empty and a red inline hint explains that broker mode needs the path set manually.

  Behavior, defaults, and the underlying settings are unchanged — both are presentation-only improvements.

### Fixed
- Broker manager now gracefully shuts down a stale broker process that no longer passes the health probe (typically a protocol-version mismatch after a package upgrade) instead of leaving it holding the port and failing the server with "Address already in use".
- A failure while handling a single broker-delivered request no longer terminates the broker poll loop (which previously left all subsequent requests queued forever).
- Fixed a broker process leak when the Server Port setting changes while broker mode is active: `MCPBrokerProcessManager.EnsureRunning` only shut down the previously recorded broker process when its recorded port matched the newly requested port, so changing the port left the old broker orphaned (and deleted its pid file, making it unrecoverable by any later cleanup) while a fresh broker started on the new port. The stale-broker shutdown now runs regardless of whether the port changed.
- The "Server Port" field now commits on Enter/blur rather than per keystroke, so the settings-change restart path runs once per committed value instead of once per typed digit. The restart scheduler also uses editor-update fallbacks alongside `delayCall` for both stop and start phases, so port changes made from tools or non-IMGUI callbacks cannot get stuck with a scheduled-but-never-run restart.
- Fixed `get_performance_snapshot` and `analyze_scene_complexity` under-reporting scene stats in multi-scene projects. Both tools sourced root GameObjects from `SceneManager.GetActiveScene()` only, silently excluding any additively loaded scenes (e.g. a bootstrap scene loading a content scene on top); they now walk every loaded scene via `SceneManager.sceneCount`/`GetSceneAt`, and the "Scene:" summary line is renamed "Scene(s):" to list every scene that was counted.
- Fixed `get_hierarchy` and `get_scene_info` silently omitting additively loaded scenes: both sourced content from `SceneManager.GetActiveScene()` only, so in multi-scene projects (e.g. a bootstrap scene additively loading a content scene) everything outside the active scene was invisible. Both tools now walk every loaded scene, label each as `(active)`/`(additive)`, and `get_hierarchy`'s `root_name` inactive-object search fallback also spans all loaded scenes.
- `get_console_logs` now truncates each emitted line to 300 characters (annotated with the remaining length). A single log entry containing a huge one-line payload (observed in the wild: an entire 280KB save-file JSON logged to the console) previously blew up the whole tool response.

### Contributors
- Thanks @dehuaichendragonplus for the feature and fix PRs behind this release (#17, #18, #19, #20, #21, #22, #23, #24, #25, #27).

## [0.4.8] - 2026-06-24

### Fixed
- Fixed vertically flipped Game View screenshots when reading Unity's already-rendered PlayModeView frame.
- Kept camera-rendered screenshots such as Scene View and fallback Game View captures in their native orientation.

## [0.4.7] - 2026-06-23

### Added
- Added a recommended `IKitWrightCommand` template to generated project skills, including traceable `ctx.Log` usage and Undo-aware object modification helpers.
- Added generated skill guidance for Unity fake-null references: avoid `??=` when lazily resolving `UnityEngine.Object` references and use explicit `if (field == null)` checks instead.
- Added a GitHub Actions workflow for publishing the MCP Registry entry with GitHub OIDC after the NuGet package is indexed.

### Changed
- `execute_code` now automatically adds `using KitWright.Editor.Tools.Scripting;` when a full-class snippet implements an unqualified `IKitWrightCommand`, while avoiding duplicate usings when the namespace is already present.

### Fixed
- Fixed the release helper's Unity EditMode test invocation so batchmode waits for Test Runner completion and writes the XML result instead of exiting immediately after import.
- Release unitypackage validation now also rejects `.github` paths so publishing automation files cannot leak into package exports.

## [0.4.6] - 2026-06-17

### Fixed
- Made external script refresh and compilation requests resilient when Unity Auto Refresh is disabled or a hot-reload plugin intercepts the normal refresh path. `request_recompile`, `wait_for_compilation(force_refresh)`, and `execute_code` now share a fallback refresh flow and return `REFRESH_DID_NOT_START_COMPILATION` instead of reporting stale compilation results as success when scripts still look uncompiled. (#15)

## [0.4.5] - 2026-06-17

### Added
- Added `capture_simulator_view` to capture Unity's Device Simulator screen, optionally select the Simulator device by name, and draw a Safe Area outline overlay while preserving the source aspect ratio when only one output dimension is provided.

### Fixed
- Fixed Device Simulator captures being vertically flipped.
- Removed the Game View fallback from Device Simulator captures so device switches no longer return a stale 16:9 Game View image when the Simulator preview texture is not ready.

## [0.4.4] - 2026-06-15

### Added
- Added an optional experimental Broker Mode for the MCP Server. When enabled, a tiny local broker process owns the HTTP port and keeps client requests alive while Unity reloads the scripting domain; direct in-process HTTP remains the default.
- Broker Mode now returns a retryable JSON-RPC error for new requests while the Unity backend is reloading or reconnecting, instead of letting short client timeouts expire silently.

### Fixed
- Improved `execute_code` unexpected failure diagnostics by unwrapping `TargetInvocationException` and returning the underlying exception type, message, and stack trace. (#14)

## [0.4.3] - 2026-06-06

### Changed
- Documented OpenUPM as an optional UPM registry install source for users who want Unity Package Manager to show registry-backed version history.
- Added optional release-script verification for OpenUPM indexing after new tags are published.

### Fixed
- Fixed `capture_game_view` returning black frames in URP/HDRP projects by reading the rendered Game View frame before falling back to `camera.Render()`. (#11, #12)

### Contributors
- Thanks @dehuaichendragonplus for the detailed URP/HDRP Game View capture report and patch.

## [0.4.2] - 2026-06-06

### Changed
- `execute_code` now compiles snippets through Unity's bundled Roslyn csc first while preserving the in-memory compilation/execution flow. This improves support for modern C# syntax such as target-typed `new()` and switch expressions without writing snippet files into the Unity project.
- Release packaging now explicitly rejects local IDE metadata and macOS `.DS_Store` files in addition to tests, local notes, token files, and host-project folders.

## [0.4.1] - 2026-06-03

### Changed
- Narrowed optional `execute_code` project namespace auto-injection to loaded assemblies under `Library/ScriptAssemblies`, reducing wrapper size and type-name ambiguity when the opt-in setting is enabled.

### Fixed
- Downgraded expected response-write failures after client disconnects or domain reloads so `socket has been shut down` no longer appears as a Unity Console error.
- Marked non-resumed tools interrupted by script recompilation as `Interrupted` in Recent Activity instead of showing a misleading green `OK`.

## [0.4.0] - 2026-06-02

### Changed
- `execute_code` no longer auto-injects project namespaces by default. The optional MCP Settings toggle now derives namespaces from loaded project assemblies instead of regex-scanning source files, avoiding source-only, conditional, or asmdef-isolated namespaces that can make every snippet fail with `COMPILATION_FAILED`. (#9)
- Moved `execute_code` safety controls out of the MCP Server window and into **KitWright > MCP Settings** alongside debug logging.

## [0.3.9] - 2026-06-01

### Added
- Added stricter default-on filesystem safety checks for `execute_code`, covering broad `System.IO` writes, raw file streams, absolute/user/system paths, and path traversal patterns while clearly documenting that this is not a full sandbox.
- Added a local release helper script for version bumping, Unity test/export flows, unitypackage pathname validation, release notes, checksums, and optional publishing.

### Changed
- Split the MCP Server window into smaller focused panels and moved related settings, tool exposure, project skills, and skills management classes out of the monolithic window file.
- Standardized tool error results on structured JSON envelopes with `success:false`, `code`, `error`, and optional `data`; legacy `Error:` text is no longer treated as an error signal.
- Disabled verbose plugin debug logging by default and kept high-volume request logs in the Recent Activity UI instead of the Unity Console.

### Fixed
- Filtered release unitypackages through an explicit asset list so local-only files, tests, ProjectSettings, Packages, Library, and token files cannot be included accidentally.
- Hardened release-script cleanup, non-publishing flows, and Unity export handling so a lingering batchmode process does not block package validation after a package has already been written.

## [0.3.8] - 2026-05-23

### Added
- Added a default-on `execute_code` safety checks toggle to the MCP Server window. Clients that omit the `safety_checks` argument now use this project-level default, while explicit tool arguments still override it.

### Fixed
- Reworked the MCP HTTP transport to use a directly owned loopback TCP listener and retry post-domain-reload binds, avoiding Windows/Unity 6 `Address already in use` recovery failures caused by stale listener state.
- Avoided Unity synchronization-context capture during transport bind retries so occupied-port recovery cannot stall the editor when callers synchronously wait on startup.
- Hardened editor-thread queued task cleanup during server disposal so pending work is cancelled cleanly across domain reloads.

## [0.3.7] - 2026-05-22

### Fixed
- Added a project-path-hash identity to MCP `initialize` responses so an existing KitWright listener can be verified as belonging to the same Unity project without exposing the raw local path.
- When HTTP binding finds the configured port already occupied, the transport now probes `initialize` and attaches only if both the KitWright server name and project identity match.
- Attached transports detach without closing the owning listener, while owned transports still stop and close their `HttpListener` normally.
- Probe timeouts and unrelated listeners are treated as probe failures, not as external cancellation.
- The MCP Server window now distinguishes an attached existing server from a listener owned by the current service.

## [0.3.6] - 2026-05-21

### Fixed
- Made MCP server start idempotent across concurrent window, settings, and domain-reload startup paths so repeated Start calls reuse the same in-flight startup instead of creating competing HTTP transports.
- Hardened HTTP transport cleanup during Unity reloads and Stop/Dispose races, including already-disposed `HttpListener` instances.
- Recognize Windows and Mono `HttpListener` address-in-use variants (`10048`, `183`, `Only one usage...`, and `another listener...`) during restart retry detection.
- Clean up partially initialized server transport, request handler, and resource provider state after failed or cancelled starts.

## [0.3.5] - 2026-05-21

### Fixed
- Updated LM Studio one-click configuration to use the official `lmstudio://add_mcp` flow and avoid creating guessed Windows `mcp.json` paths. Existing LM Studio config files are still updated when found.

## [0.3.4] - 2026-05-20

### Added
- Added LM Studio to the MCP Server window's one-click configuration targets. The generated config writes `kitwright` to LM Studio's `mcp.json` using Cursor-compatible `mcpServers` JSON.
- Documented manual LM Studio setup paths for macOS/Linux and Windows.

## [0.3.3] - 2026-05-20

### Fixed
- Unitypackage-based updates now filter downloaded release packages before import and only allow paths under the installed `Assets/unity-mcp` root. This prevents accidental release artifacts from overwriting host-project `ProjectSettings`, `Packages`, or `Library` files during one-click updates.

## [0.3.2] - 2026-05-18

### Added
- `KitWright > MCP Server` window now shows the installed package version, polls GitHub for new releases every 6 hours, and surfaces a one-click update prompt when a newer version is available. Auto-check is skipped in Unity batch mode.

### Fixed
- Post-domain-reload server restart is now resilient to (a) the `[InitializeOnLoad]` vs `afterAssemblyReload` ordering race — the handler also kicks off a restart from its own static ctor if reload bookkeeping is pending, (b) `EditorApplication.isCompiling` still being true when the `delayCall` fires, (c) the service provider not yet being available, (d) duplicate scheduling. The restart now retries via `EditorApplication.update` until the editor is settled.
- `HttpMCPTransport.StartAsync` now retries up to 10 seconds (40 × 250 ms) when the port is briefly held by an unwinding listener after an AppDomain transition. Eliminates residual `Address already in use` failures that 0.3.1 did not fully cover for fast-reload scenarios.
- `DomainReloadHandler.CompletePendingFunction` defers the pending-function clear when the editor is mid-compile / mid-update / about to change Play Mode, instead of clearing immediately and racing the reload. 15-second fallback timeout prevents indefinite deferral.
- Root services and MCP server startup are now no-ops in Unity batch mode (`-batchmode`), so running batch jobs in parallel with a foreground Editor that already binds port 8765 no longer conflicts.
- `request_recompile` now returns a clear error when called while Unity is in Play Mode (Unity does not process script compilation or domain reloads while playing). Call `exit_play_mode` first, then retry.

### Changed
- `unity-mcp-workflow` skill (and the generated `AGENTS.md` / `CLAUDE.md` templates) now document two Play Mode lifecycle pitfalls: (1) after `enter_play_mode`, the HTTP server is briefly unreachable while Unity reloads the domain — poll `tools/list` / `get_reload_recovery_status` until it responds before issuing the next call; (2) `request_recompile` is rejected during Play Mode and must be preceded by `exit_play_mode`. Existing installs should regenerate Project Skills via `KitWright > Project Skills` to pick up the new content.

## [0.3.1] - 2026-05-17

### Fixed
- Compile errors on Unity 6000.3+ where `Object.GetInstanceID()` and `EditorUtility.InstanceIDToObject(int)` are obsolete-as-error (CS0619). Object IDs handed to MCP clients now go through a new internal `ObjectIdHelper` that uses `GetEntityId` / `EditorUtility.EntityIdToObject` on Unity 6000.3+ and the legacy `InstanceID` API on older Unity. (#3)
- HTTP transport could fail to restart after a Unity domain reload with `通常每个套接字地址(协议/网络地址/端口)只允许使用一次。` / `Address already in use`. Root cause was a fire-and-forget `StopAsync` in `beforeAssemblyReload` — Unity unloaded the AppDomain before the listener actually released the port. `MCPServerService` now exposes a synchronous `StopSync` used by both `Dispose` and the domain-reload handler, and `RootScopeServices.Initialize` skips its auto-start during a post-reload restart so only one start path runs. (#1)

### Changed (potentially breaking for downstream clients)
- `instanceId`, `componentInstanceId`, and `fileID` fields in tool responses are now always JSON strings instead of numbers. On Unity 6000.3+ they are `EntityId` text; on older Unity they are decimal `InstanceID` strings. Clients that parsed these fields as integers must accept strings.

## [0.3.0] - 2026-05-06

### Added
- New foundation helpers under `Editor/Tools/Helpers/`: `ObjectsHelper` (unified by_id/by_name/by_path/by_tag/by_layer/by_component locator with searchInactive / searchInChildren / findAll, prefab-stage aware), `ComponentSerializer` (SerializedObject-based read/write that picks up `[SerializeField] private`, Object references via `{"fileID": instanceId}`, Vector/Quaternion/Color/Enum/Array), `TypeResolver` (TypeCache-backed O(1) component type lookup), `Response` (structured `{success, message, data}` / `{success, code, error, data}` envelope), `EditorReadyHelper` (refresh + wait for compilation), `GameObjectSerializer` (structured payloads with `instanceId` so agents can chain `by_id` calls).
- New `EditorState` tool provider: `get_editor_state`, `get_selection`, `set_selection`, `get_prefab_stage`, `get_active_tool`, `set_active_tool`, `get_windows`, `get_tags`, `add_tag`, `remove_tag`, `get_layers`, `add_layer`, `get_build_settings`.
- New `MenuItem` tool provider: `execute_menu_item`, `validate_menu_item` — drive any editor menu including third-party packages without writing dedicated wrappers.
- New `IKitWrightCommand` + `ExecutionContext` API for `execute_code`. Snippets that implement `IKitWrightCommand` get `ctx.RegisterObjectCreation` / `RegisterObjectModification` / `DestroyObject` (auto-Undo + tracked) and `ctx.Log` / `LogWarning` / `LogError` (returned in the response).
- `ComponentPropertyFunctions`: new `component_instance_id` parameter lets tools target a specific component when a GameObject has multiple of the same type.

### Changed
- All `GameObject` tools now resolve targets through `ObjectsHelper` and accept a new `find_method` parameter (defaults to auto-detect: id → path → name).
- `GameObject` and `ComponentProperty` tools now return structured JSON (`Response.Success(...)`) instead of free-form strings, with `instanceId` included so agents can chain `by_id` lookups reliably.
- `ComponentPropertyFunctions.SetComponentProperty(ies)` now writes through `SerializedObject`, so `[SerializeField] private` fields and Object references work; partial writes return per-field success.
- `execute_code` now calls `EditorReadyHelper.RefreshAndWaitForReady` before compiling, so external file edits are picked up automatically — no separate `request_recompile` needed in most flows.
- `FunctionInvokerController` now serializes non-string tool returns to JSON via Newtonsoft, so tools can return `Response.Success(...)` or any object.
- `unity-mcp-workflow` project skill rules updated to cover structured JSON returns, `instanceId` chaining, `find_method`, the new SerializedProperty-backed component setter, the IKitWrightCommand template, editor-state tools, and `execute_menu_item` as the preferred fallback before `execute_code`. Generated `AGENTS.md` / `CLAUDE.md` templates updated to match. Existing installed skills must be regenerated via `KitWright > Project Skills` to pick up the new content.
- `core` profile expanded from 19 to 29 tools: added `get_editor_state`, `get_selection`, `set_selection`, `get_prefab_stage`, `find_game_objects`, `list_components`, `get_component_properties`, `set_component_property`, `set_component_properties`, `execute_menu_item`. Lower-frequency editor-state tools (tag/layer mutation, window listing, build settings, active-tool control, `validate_menu_item`) remain `full`-only.

### Breaking
- `GameObjectFunctions` parameter renames for clarity now that resolution is method-driven: `name` → `target` (delete/duplicate/rename/set_transform/set_active/add_component/set_tag_and_layer/get_game_object_info), `parent_name` → `parent`, `child_name` → `child`. The new `find_method` parameter is optional everywhere.

## [0.2.0] - 2026-04-30

### Changed
- Limited Project Skills to the verified default `unity-mcp-workflow` skill and removed unverified optional skills from the catalog.
- Moved Codex project skill installation from `.agents/skills/` to project-root `.codex/skills/`.
- Moved Claude project skill installation from `.claude/commands/` to project-root `.claude/skills/`.
- Renamed Project Skills to use the final feature name across UI and docs.
- Added a one-click `Configure + Skills` action for supported MCP clients.
- Added `KitWright > Tool Exposure` for editing which tools `core` and `full` expose.
- Grouped the Tool Exposure editor by tool category with per-category selection controls.
- Updated the default Unity MCP workflow skill to cover default `core`, default `full`, and customized tool exposure.
- Rendered screenshot tool results as image previews in Recent Activity.
- Added `KitWright > Plugin Settings` with a toggle for verbose plugin debug logging.
- Enabled plugin debug logging by default and expanded the default Unity MCP workflow skill with safer scene, prefab, and readback validation guidance.

## [0.1.10] - 2026-04-17

### Added
- Added `KitWright > Project Skills` as a dedicated window for project-level skills setup
- Added built-in and optional project skills management for supported AI clients, with per-platform generated file visibility
- Added persistence for the currently selected one-click configuration target so related tools stay aligned across sessions

### Changed
- Moved project skills management out of the MCP Server window into its own dedicated menu entry
- Improved the Project Skills window layout with clearer sections and installed-file visibility
- Removed automatic port fallback so the MCP server now starts only on the configured port
- Replaced Unity editor star-prompt emoji with plain text for better font compatibility across Unity versions

## [0.1.9] - 2026-04-16

### Fixed
- Fixed one-click MCP configuration paths on Windows by resolving the real user profile directory
- Fixed VS Code one-click configuration to use the platform-specific user config directory with a macOS fallback
- Ensured one-click MCP configuration writes the currently running server port after automatic port fallback

## [0.1.8] - 2026-04-15

### Changed
- Rebranded the open-source package and documentation from GameBooom to KitWright
- Moved the public Git repository to `WrightAI/kitwright-unity-mcp`
- Updated Unity menu paths to `KitWright/MCP Server` and `KitWright/Check for Updates`
- Reorganized the README quick start and one-click client configuration guidance

## [0.1.7] - 2026-04-10

### Changed
- Repurposed `request_recompile` into the default AI-facing sync flow for external file edits, compilation, and domain reload recovery
- Removed `sync_external_changes` from the exposed MCP tool list to avoid duplicate AI pathways
- Prevented MCP transport restarts from running on a background thread after settings changes
- Avoided redundant settings change notifications and UI initialization callbacks in the MCP Server window

## [0.1.6] - 2026-04-08

### Added
- Updated `request_recompile` to import external file edits and wait through compilation/domain reload recovery

### Changed
- Strengthened `request_recompile` tool guidance so AI clients treat it as the default follow-up after external file edits
- Improved `request_recompile` behavior to return an explicit compilation/reload message instead of failing ambiguously during domain reload
- Persist and report recovery results for external sync operations through `get_reload_recovery_status`

## [0.1.5] - 2026-04-01

### Added
- Performance analysis tools: `get_performance_snapshot` and `analyze_scene_complexity`

### Changed
- Core MCP tool profile now includes lightweight performance inspection by default

## [0.1.4] - 2026-04-01

### Added
- Built-in update checking from `KitWright/Check for Updates` with install-source aware behavior
- Automatic Git package refresh for Git-based installs
- Automatic latest `.unitypackage` download and import for asset-import installs

### Changed
- Game View screenshots now default to the current Game View render size instead of a fixed 512x512 capture
- Mouse click simulation now maps coordinates against the real Game View render size for more reliable UI and physics hits
- Package version resolution now prefers the actual installed package location so Git installs report the correct version
- Package metadata now points to the `WrightAI/kitwright-unity-mcp` repository and `0.1.4`

## [0.1.2] - 2026-03-30

### Added
- MCP prompts support with `prompts/list` and `prompts/get`
- Rich MCP resources with project context, scene/selection/error summaries, interaction history, and resource templates
- `execute_code` as the primary high-flexibility orchestration tool
- Input simulation tools for key press, key combo, mouse click, and mouse drag workflows
- Lightweight editor context builder and package version resolver for richer MCP context output

### Changed
- Default MCP tool exposure now uses a `core` profile to reduce tool-list noise, with optional `full` exposure in the MCP Server window
- Tools exposed by the open-source build now execute directly without an extra approval toggle
- Play Mode MCP requests no longer stall on the editor thread dispatch path
- MCP server info now reports the package version dynamically instead of a hard-coded version

## [0.1.1] - 2026-03-19

### Added
- Minimal MCP resources support with `resources/list`, `resources/read`, and project/scene resource endpoints
- Reload recovery reporting via `get_reload_recovery_status`
- Cached Unity console log access via `get_console_logs`

### Changed
- Bind and document the default local MCP endpoint as `http://127.0.0.1:8765/` for better Codex compatibility
- Auto-start the MCP server on editor load when it is enabled in settings
- Improve compilation tracking and persist interrupted tool execution across domain reloads

## [0.1.0] - 2026-03-12

### Added
- Initial release of KitWright MCP for Unity (Community Edition)
- MCP Server with HTTP JSON-RPC 2.0 transport
- 60+ built-in tool functions across 15 modules (scene, asset, script, UI, camera, animation, etc.)
- Reflection-based tool discovery with attribute annotations
- Custom tool support via `[ToolProvider]` attribute
- MCP Client for connecting to external MCP servers
- One-click MCP config generation for Claude Code, Cursor, VS Code, Trae, Kiro, and Codex
- Domain reload survival across Unity recompilations
- UPM package distribution via Git URL
