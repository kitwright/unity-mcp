#!/usr/bin/env python3
"""Says what a test run skipped and why, and fails when a test skipped for a reason nobody declared.

A skipped test reads as green in a check run, so a test that stopped running because the runner
lacked something looked the same as one that passed. Every skip in a results file is listed with
its reason (in $GITHUB_STEP_SUMMARY when set), and a skip whose reason matches none of EXPECTED
fails the step: either add the reason there, saying which environment the test needs, or fix the test.

Usage: python3 scripts/report_test_skips.py <label> <results.xml or folder>...
"""
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

# What the CI runners do not have. Anything else is a skip nobody decided on. The tests these
# cover are run by hand before a release: RELEASE_CHECKLIST.md, "Tests CI cannot run".
EXPECTED = [
    r"No graphics device",
    r"No usable screen",
    r"no rendered view",
    r"editor loop does not tick during a batchmode",
    r"Hot Reload is not installed",
    r"VolumeProfile is not available",
    r"URP is not installed",
    r"Needs at least two shaders",
    r"root scope, which is not up here",
    r"Unity-bundled Mono is required",
    r"legacy Input Manager",
    r"Windows-only",
    r"Could not create a junction",
    r"Device Simulator module is not available",
    r"AI module is not installed",
    r"compiled out without the com\.unity\.ugui package",
    r"no Game View to route",
]


def results_files(paths):
    for path in paths:
        if os.path.isdir(path):
            yield from glob.glob(os.path.join(path, "**", "*.xml"), recursive=True)
        elif os.path.exists(path):
            yield path


def main():
    label, paths = sys.argv[1], sys.argv[2:]
    rows, unexpected, totals = [], [], {"total": 0, "passed": 0, "failed": 0, "skipped": 0}
    for path in results_files(paths):
        root = ET.parse(path).getroot()
        if root.tag != "test-run":
            continue
        for key in totals:
            totals[key] += int(root.get(key, "0"))
        for case in root.iter("test-case"):
            if case.get("result") not in ("Skipped", "Inconclusive"):
                continue
            node = case.find("reason/message")
            reason = ((node.text if node is not None else "") or "").strip().splitlines()
            reason = reason[0] if reason else "(no reason given)"
            name = case.get("fullname") or case.get("name")
            rows.append((name, reason))
            if not any(re.search(pattern, reason, re.IGNORECASE) for pattern in EXPECTED):
                unexpected.append((name, reason))

    lines = [f"### {label}: {totals['passed']} passed, {totals['failed']} failed, {totals['skipped']} skipped "
             f"of {totals['total']}", ""]
    if rows:
        lines += ["| Skipped test | Reason |", "|---|---|"]
        lines += [f"| `{name}` | {reason.replace('|', '/')} |" for name, reason in rows]
    if unexpected:
        lines += ["", f"**{len(unexpected)} skipped for a reason not in scripts/report_test_skips.py EXPECTED:**"]
        lines += [f"- `{name}`: {reason}" for name, reason in unexpected]

    text = "\n".join(lines) + "\n"
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(text)
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    print(text)

    if totals["total"] == 0:
        print("::error::No test results were found; the run did not report anything to check.")
        return 1
    for name, reason in unexpected:
        print(f"::error::{name} was skipped: {reason}")
    return 1 if unexpected else 0


if __name__ == "__main__":
    sys.exit(main())
