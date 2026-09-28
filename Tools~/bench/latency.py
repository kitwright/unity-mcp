#!/usr/bin/env python3
"""Z1: request latency against a live editor, as a client sees it.

Scenarios, in the order the transcripts rank the tools (transcript.py): ping as the floor,
get_editor_state, execute_code with and without skip_refresh, get_compilation_errors,
request_recompile with nothing to compile, wait_for_compilation with force_refresh on and off,
get_console_logs, get_test_job, capture_game_view. Then S3 (8 read-only calls at once) and S4
(head-of-line: a ping sent while a slow read-only call is in flight).

--nudge is Z5: a thread posts WM_NULL to the editor's windows every 2 ms while a call is in flight,
which says what waking the editor loop from outside the main thread would save (M4c). Windows only.

The URL comes from --url or from the kitwright entry of an MCP config (--config, default
./.mcp.json), since it carries the project pin and the access token.

Usage: python3 latency.py [--config path/.mcp.json | --url URL] [--label name] [--runs 10] [--nudge] [--json out.json]
"""
import argparse
import ctypes
import http.client
import json
import os
import statistics
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from urllib.parse import urlparse


class Client:
    def __init__(self, url):
        parsed = urlparse(url)
        self.host, self.port, self.path = parsed.hostname, parsed.port, parsed.path.rstrip("/") + "/mcp"
        self.session = None
        self._id = 0
        self._lock = threading.Lock()

    def post(self, method, params=None, notify=False):
        message = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        if not notify:
            with self._lock:
                self._id += 1
                message["id"] = self._id
        headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
        if self.session:
            headers["Mcp-Session-Id"] = self.session
        started = time.perf_counter()
        connection = http.client.HTTPConnection(self.host, self.port, timeout=300)
        connection.request("POST", self.path, body=json.dumps(message).encode(), headers=headers)
        response = connection.getresponse()
        body = response.read()
        elapsed = (time.perf_counter() - started) * 1000
        if method == "initialize" and response.getheader("Mcp-Session-Id"):
            self.session = response.getheader("Mcp-Session-Id")
        connection.close()
        return elapsed, len(body), response.status, self._parse(response.getheader("Content-Type"), body)

    @staticmethod
    def _parse(content_type, body):
        if not body:
            return None
        if "text/event-stream" in (content_type or ""):
            events = [json.loads(line[5:]) for line in body.decode("utf-8").splitlines() if line.startswith("data:")]
            return next((e for e in events if "id" in e), events[-1] if events else None)
        try:
            return json.loads(body)
        except ValueError:
            return None

    def initialize(self):
        self.post("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                 "clientInfo": {"name": "kw-latency", "version": "1"}})
        self.post("notifications/initialized", notify=True)

    def call(self, name, arguments=None):
        return self.post("tools/call", {"name": name, "arguments": arguments or {}})


def summary(times):
    ordered = sorted(times)
    return {"runs": len(ordered), "p50": round(statistics.median(ordered), 1),
            "p90": round(ordered[min(len(ordered) - 1, int(0.9 * len(ordered)))], 1),
            "min": round(ordered[0], 1), "max": round(ordered[-1], 1)}


class Nudger:
    """Posts WM_NULL to every top-level window of the editor process while a call is in flight."""

    def __init__(self, port):
        self.windows = self._windows_of(self._pid_listening_on(port)) if sys.platform == "win32" else []
        self._stop = threading.Event()
        self._thread = None

    @staticmethod
    def _pid_listening_on(port):
        import subprocess
        out = subprocess.run(["netstat", "-ano", "-p", "TCP"], capture_output=True, text=True).stdout
        pids = [line.split()[-1] for line in out.splitlines() if f":{port} " in line and "LISTENING" in line]
        return int(pids[0]) if pids else 0

    @staticmethod
    def _windows_of(pid):
        user32 = ctypes.windll.user32
        found = []
        callback_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

        def visit(hwnd, _):
            owner = ctypes.c_ulong()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            if owner.value == pid:
                found.append(hwnd)
            return True

        user32.EnumWindows(callback_type(visit), 0)
        return found

    def __enter__(self):
        if self.windows:
            self._stop.clear()
            self._thread = threading.Thread(target=self._run, daemon=True)
            self._thread.start()
        return self

    def __exit__(self, *_):
        self._stop.set()
        if self._thread:
            self._thread.join()

    def _run(self):
        while not self._stop.is_set():
            for hwnd in self.windows:
                ctypes.windll.user32.PostMessageW(hwnd, 0x0000, 0, 0)
            time.sleep(0.002)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url")
    parser.add_argument("--config", default=".mcp.json")
    parser.add_argument("--label", default="run")
    parser.add_argument("--runs", type=int, default=10)
    parser.add_argument("--nudge", action="store_true")
    parser.add_argument("--json")
    args = parser.parse_args()

    url = args.url or json.load(open(args.config))["mcpServers"]["kitwright"]["url"]
    client = Client(url)
    client.initialize()
    nudger = Nudger(client.port) if args.nudge else None
    results = {"label": args.label, "nudge": bool(args.nudge)}

    def measure(label, run, runs=args.runs):
        times, sizes = [], []
        for _ in range(runs):
            if nudger:
                with nudger:
                    elapsed, size, _, _ = run()
            else:
                elapsed, size, _, _ = run()
            times.append(elapsed)
            sizes.append(size)
        results[label] = dict(summary(times), bytes=max(sizes))
        print(f"{label:48s} {results[label]}", flush=True)

    measure("ping", lambda: client.post("ping"))
    measure("get_editor_state", lambda: client.call("get_editor_state"))
    measure("execute_code skip_refresh=true", lambda: client.call("execute_code", {"code": "return 1;", "skip_refresh": True}))
    measure("execute_code skip_refresh=false", lambda: client.call("execute_code", {"code": "return 1;"}), max(3, args.runs // 3))
    measure("get_compilation_errors", lambda: client.call("get_compilation_errors"))
    measure("request_recompile (nothing to do)", lambda: client.call("request_recompile"), max(3, args.runs // 3))
    measure("wait_for_compilation force_refresh=true", lambda: client.call("wait_for_compilation"), max(3, args.runs // 3))
    measure("wait_for_compilation force_refresh=false", lambda: client.call("wait_for_compilation", {"force_refresh": False}))
    measure("get_console_logs count=20", lambda: client.call("get_console_logs", {"count": 20}))
    measure("get_test_job (unknown id)", lambda: client.call("get_test_job", {"job_id": "kw-latency-none"}))
    measure("capture_game_view", lambda: client.call("capture_game_view"), max(3, args.runs // 3))

    def parallel(label, run, count=8):
        started = time.perf_counter()
        with ThreadPoolExecutor(count) as pool:
            each = list(pool.map(lambda _: run()[0], range(count)))
        results[label] = {"wall": round((time.perf_counter() - started) * 1000, 1), "each": sorted(round(t, 1) for t in each)}
        print(f"{label:48s} {results[label]}", flush=True)

    parallel("S3 8x get_editor_state at once", lambda: client.call("get_editor_state"))
    parallel("S3 8x ping at once", lambda: client.post("ping"))

    # S4: a ping queued behind a slow read-only call. capture_game_view is read-only and takes
    # hundreds of milliseconds, which is the shape of the wait head-of-line blocking costs.
    slow = threading.Thread(target=lambda: client.call("capture_game_view"))
    slow.start()
    time.sleep(0.05)
    elapsed, _, _, _ = client.post("ping")
    slow.join()
    results["S4 ping behind capture_game_view"] = round(elapsed, 1)
    print(f"{'S4 ping behind capture_game_view':48s} {results['S4 ping behind capture_game_view']}", flush=True)

    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(results, handle, indent=2)


if __name__ == "__main__":
    main()
