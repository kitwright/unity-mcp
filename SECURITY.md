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
- Bypassing the `Origin` check or the project pin — a web page reaching the server through a browser, or a request landing in a project it was not written for
- Bypassing the first-connect client approval prompt when that setting is enabled (it ships off, and its identity unit is the client executable, so approving a shared runtime covers every script using it — that breadth is by design, not a vulnerability)
- Credential or token leakage in logs, exported configs, or error responses

## What Doesn't Count

- Tools that intentionally modify the Unity project — `execute_code`, `create_script`, and
  `delete_asset` do exactly what they say, and any client the user approved can call them
- Issues that require the attacker to already run code as the user on the same machine
- Vulnerabilities in Unity or in third-party packages — report those upstream first; we bump our pins
  once the upstream fix lands
