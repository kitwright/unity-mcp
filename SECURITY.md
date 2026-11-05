# Security Policy

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Use GitHub's private reporting on this repository (**Security → Report a vulnerability**), or email
**contact@kitwright.dev** with:

- A clear description of the issue
- Steps to reproduce, or a proof-of-concept
- The package version affected (`package.json` → `version`)
- Your OS, Unity Editor version, and MCP client
- Optional: a suggested fix

We aim to acknowledge reports within **3 business days** and to share an initial assessment within
**10 business days**. Fixes ship as patch releases.

## Guarantees We Make

These are the properties a report can hold us to:

- **Loopback only.** The HTTP transport binds `IPAddress.Loopback` and the keepalive broker listens on
  `127.0.0.1`. There is no LAN bind option, so anything reachable off-host is a bug.
- **Project containment.** File-touching tools route through `PathSafety`, which resolves the target and
  rejects anything outside the project root with `PATH_OUTSIDE_PROJECT`.
- **No credentials in logs.** Broker tokens, session ids, and client config secrets must not appear in
  the Unity console, the MCP Server window's interaction log, or a tool's error payload.

## What Counts as a Security Issue

- Reaching the server or the broker from another host
- Reading or writing files outside the Unity project root, including through symlinks, junctions, or
  `..` traversal that gets past `PathSafety`
- Executing code without a tool call that asked for it — for example a crafted MCP message that reaches
  `execute_code`'s compile step, or a resource read that evaluates its payload
- Bypassing the `Origin` check, or reaching a project whose pin a request does not carry — a web page
  posting through a browser, or a pinned URL answered by a sibling editor. A URL with no `/p/<pin>/` at
  all is served on purpose, for configs written before pinning; the sweep at server start rewrites those,
  so a pinless request being answered is a stale config, not a bypass
- Credential or token leakage in logs, exported configs, or error responses

## What Doesn't Count

- Tools that intentionally modify the Unity project — `execute_code`, `write_file`, and
  `delete_asset` do exactly what they say, and any client that reaches the server can call them
- Issues that require the attacker to already run code as the user on the same machine. The server
  authenticates no client: every local process that clears the `Origin` check and the project pin can
  call every tool, and the console log naming each new client is observability, not a gate
- Getting past `execute_code`'s `safety_checks`. The flag is a tool argument, so any client that can
  call `execute_code` can pass `safety_checks=false` and skip both the source blocklist and the
  compiled-assembly guard — no bypass is needed, and finding a pattern the blocklist misses is not a
  vulnerability. It catches accidents, never a caller that wants through. What does count is code
  running without a tool call that asked for it, above
- Vulnerabilities in Unity or in third-party packages — report those upstream first; we bump our pins
  once the upstream fix lands
