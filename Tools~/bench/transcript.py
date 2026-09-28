#!/usr/bin/env python3
"""Z0: what agents actually do with this server, read out of Claude Code transcripts.

Streams every ~/.claude/projects/*/*.jsonl, pairs each tool_use of a KitWright server with its
tool_result, and reports the numbers the upgrade plan ranks work by:
  - calls per tool, and how large their results are (share above the 64 KB spill threshold)
  - results that say the call could not run: compiling, off the main thread, cut by a reload, ...
  - how often the add-on's tools are used (FlowWright, the match pipeline, the runtime player)
  - wasted pairs: execute_code retried after a compile error, get_compilation_errors straight
    after request_recompile / wait_for_compilation, get_compilation_errors polled twice in a row,
    get_test_job polls per run_tests, wait_for_compilation left on its force_refresh default
Run it again after a release with --since to see whether a change moved its number.

Usage: python3 transcript.py [--since 2026-09-27] [--server-pattern REGEX] [--json out.json] [roots...]
"""
import argparse
import collections
import glob
import json
import os
import re
import statistics
import sys

DEFAULT_SERVERS = r"^(kitwright|gamewright|funplay)(-.*)?$"
SPILL_BYTES = 64 * 1024
MARKERS = {
    "compiling": "Currently compiling",
    "main_thread": "can only be called from the main thread",
    "domain_reload": "interrupted by a domain reload",
    "timeout": "timed out",
    "not_exposed": "TOOL_NOT_EXPOSED",
}
PRO_TOOL = re.compile(r"^(flowwright_|match_sprites_to_image|find_sprite_for_region|solve_match_params|"
                      r"capture_match_compare|finalize_match_panel|render_sprite_sheet|player_|runtime_)")
COMPILE_ERROR = re.compile(r"COMPILATION_FAILED|error CS\d{4}")
# Claude Code stores a result over its token cap as a stub naming the original size, so the size
# measured off the transcript is the stub's unless it is read back out of it.
CLIENT_CUT = re.compile(r"result \(([\d,]+) characters\) exceeds maximum allowed tokens")


def text_of(content):
    if isinstance(content, str):
        return content, 0
    texts, images = [], 0
    for block in content or []:
        if not isinstance(block, dict):
            continue
        if block.get("type") == "text":
            texts.append(block.get("text") or "")
        elif block.get("type") == "image":
            images += 1
    return "".join(texts), images


def calls_in(path, servers, since):
    """The session's KitWright calls in order: (tool, input, result text, image count)."""
    pending, order = {}, []
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            if '"tool_use"' not in line and '"tool_result"' not in line:
                continue
            try:
                entry = json.loads(line)
            except ValueError:
                continue
            if since and (entry.get("timestamp") or "") < since:
                continue
            content = (entry.get("message") or {}).get("content")
            if not isinstance(content, list):
                continue
            for block in content:
                if not isinstance(block, dict):
                    continue
                if block.get("type") == "tool_use":
                    parts = str(block.get("name", "")).split("__")
                    if len(parts) >= 3 and parts[0] == "mcp" and servers.match(parts[1]):
                        call = {"tool": parts[2], "input": block.get("input") or {}, "text": None, "images": 0}
                        pending[block.get("id")] = call
                        order.append(call)
                elif block.get("type") == "tool_result" and block.get("tool_use_id") in pending:
                    call = pending.pop(block["tool_use_id"])
                    call["text"], call["images"] = text_of(block.get("content"))
    return [c for c in order if c["text"] is not None]


def percentile(values, share):
    if not values:
        return 0
    values = sorted(values)
    return values[min(len(values) - 1, int(share * len(values)))]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("roots", nargs="*", default=[os.path.expanduser("~/.claude/projects")])
    parser.add_argument("--since", help="ISO date; only entries at or after it")
    parser.add_argument("--server-pattern", default=DEFAULT_SERVERS)
    parser.add_argument("--json", help="also write the numbers here")
    args = parser.parse_args()
    servers = re.compile(args.server_pattern)

    per_tool = collections.Counter()
    sizes = collections.defaultdict(list)
    markers = collections.Counter()
    marker_tools = collections.defaultdict(collections.Counter)
    pairs = collections.Counter()
    sessions = runs = polls = 0

    for root in args.roots:
        for path in glob.glob(os.path.join(root, "**", "*.jsonl"), recursive=True):
            calls = calls_in(path, servers, args.since)
            if not calls:
                continue
            sessions += 1
            for index, call in enumerate(calls):
                tool, text = call["tool"], call["text"]
                per_tool[tool] += 1
                cut = CLIENT_CUT.search(text)
                sizes[tool].append(int(cut.group(1).replace(",", "")) if cut else len(text.encode("utf-8")))
                if cut:
                    markers["cut_by_client"] += 1
                    marker_tools["cut_by_client"][tool] += 1
                for key, needle in MARKERS.items():
                    if needle in text:
                        markers[key] += 1
                        marker_tools[key][tool] += 1
                if tool == "run_tests":
                    runs += 1
                if tool == "get_test_job":
                    polls += 1
                if tool == "wait_for_compilation" and "force_refresh" not in call["input"]:
                    pairs["wait_for_compilation_default_force_refresh"] += 1

                following = calls[index + 1]["tool"] if index + 1 < len(calls) else None
                if tool == "execute_code" and following == "execute_code" and COMPILE_ERROR.search(text):
                    pairs["execute_code_retry_after_compile_error"] += 1
                if tool in ("request_recompile", "wait_for_compilation") and following == "get_compilation_errors":
                    pairs[f"{tool}_then_get_compilation_errors"] += 1
                if tool == "get_compilation_errors" and following == "get_compilation_errors":
                    pairs["get_compilation_errors_twice"] += 1

    every = [size for values in sizes.values() for size in values]
    spilled = collections.Counter({tool: sum(1 for s in values if s > SPILL_BYTES) for tool, values in sizes.items()})
    report = {
        "sessions": sessions,
        "calls": sum(per_tool.values()),
        "per_tool": per_tool.most_common(),
        "result_bytes": {"p50": percentile(every, 0.5), "p90": percentile(every, 0.9),
                         "p99": percentile(every, 0.99), "max": max(every) if every else 0,
                         "over_64kb": sum(spilled.values()),
                         "over_64kb_share": round(sum(spilled.values()) / len(every), 4) if every else 0},
        "over_64kb_by_tool": [(t, n) for t, n in spilled.most_common() if n],
        "median_bytes_by_tool": sorted(((t, int(statistics.median(v))) for t, v in sizes.items()),
                                       key=lambda item: -item[1])[:15],
        "markers": dict(markers),
        "marker_tools": {k: v.most_common(5) for k, v in marker_tools.items()},
        "pro_calls": sorted(((t, n) for t, n in per_tool.items() if PRO_TOOL.match(t)), key=lambda item: -item[1]),
        "pairs": dict(pairs),
        "get_test_job_per_run_tests": round(polls / runs, 2) if runs else None,
    }

    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    print(f"{report['calls']} calls in {sessions} sessions")
    for key in ("result_bytes", "markers", "pairs", "get_test_job_per_run_tests"):
        print(f"{key}: {report[key]}")
    print("top tools:", report["per_tool"][:20])
    print("over 64 KB by tool:", report["over_64kb_by_tool"][:10])
    print("largest median results:", report["median_bytes_by_tool"][:8])
    print("add-on tools:", report["pro_calls"])
    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(report, handle, indent=2)


if __name__ == "__main__":
    main()
