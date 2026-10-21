#!/usr/bin/env python3
"""Exercise a staged proxy DLL without changing production services.

Synthetic replay uses a CPU-only HTTP fixture. Live mode uses only the existing
loopback model. All requests are sequential. Returned tools are never executed.
"""
from __future__ import annotations

import argparse
import contextlib
import http.client
import ipaddress
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import urlsplit

import strict_contract as contract

MODEL = "DeepSeek-V4.1-Flash-1017"
LITERAL = '    <tool_call>{"name":"not_offered","arguments":{}}</tool_call>'
CALL = {"id": "fixture-call", "type": "function", "function": {"name": "get_status", "arguments": "{}"}}
ERROR = {"error": {"type": "model_output_error", "code": "unavailable_tool_call", "source": "tensorfold.tool_output_guard", "reason": "invalid_executable_tool_output"}}
TOOLS = [{"type": "function", "function": {"name": "get_status", "description": "Return synthetic status. Never execute this fixture.", "parameters": {"type": "object", "properties": {}, "additionalProperties": False}}}]


def chunk(delta: dict, finish: str | None = None) -> dict:
    return {"id": "fixture", "object": "chat.completion.chunk", "created": 1, "model": MODEL, "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}


def sse(value: dict, newline: str = "\n") -> bytes:
    return ("data: " + json.dumps(value, ensure_ascii=False) + newline + newline).encode()


def response(message: dict) -> bytes:
    return json.dumps({"id": "fixture", "choices": [{"index": 0, "message": {"role": "assistant", **message}, "finish_reason": "tool_calls" if "tool_calls" in message else "stop"}]}).encode()


def fixtures() -> list[dict]:
    finish = sse(chunk({}, "stop"))
    done = b"data: [DONE]\n\n"
    role = sse(chunk({"role": "assistant"}))
    return [
        {"name": "prose", "body": sse(chunk({"content": "one "})) + sse(chunk({"content": "two"})) + finish + done, "stream": True},
        {"name": "usage", "body": role + finish + sse({"choices": [], "usage": {"completion_tokens": 2}}) + done, "stream": True},
        {"name": "crlf-utf8-multiline", "body": b": comment\r\n" + 'data: {"choices":\r\ndata: [{"delta":{"content":"日本語"},"finish_reason":null}]}\r\n\r\n'.encode() + sse(chunk({}, "stop"), "\r\n") + b"data: [DONE]\r\n\r\n", "stream": True},
        {"name": "native-rejection", "body": role + sse(ERROR) + done, "stream": True},
        {"name": "done-only", "body": done, "stream": True},
        {"name": "truncated-event", "body": sse(chunk({"content": "partial"})) + b'data: {"choices":', "stream": True},
        {"name": "literal-stream", "body": sse(chunk({"content": LITERAL})) + finish + done, "stream": True},
        {"name": "native-stream", "body": sse(chunk({"tool_calls": [{"index": 0, **CALL}]})) + sse(chunk({}, "tool_calls")) + done, "stream": True},
        {"name": "literal-json", "body": response({"content": LITERAL}), "stream": False},
        {"name": "native-json", "body": response({"tool_calls": [CALL]}), "stream": False},
        {"name": "rejection-json", "body": json.dumps(ERROR).encode(), "stream": False, "status": 500},
        {"name": "transport-fault", "body": role + sse(chunk({"content": "partial"})), "stream": True, "fault": True},
    ]


class FixtureHandler(BaseHTTPRequestHandler):
    def do_POST(self) -> None:
        self.rfile.read(int(self.headers.get("Content-Length", "0")))
        self.server.request_count += 1
        case = self.server.case
        body = case["body"]
        self.send_response(case.get("status", 200))
        self.send_header("Content-Type", "text/event-stream" if case["stream"] else "application/json")
        self.send_header("Content-Length", str(len(body) + (100 if case.get("fault") else 0)))
        self.send_header("Connection", "close")
        self.end_headers()
        # Small writes also split UTF-8 and CRLF boundaries at the transport layer.
        for offset in range(0, len(body), 7):
            self.wfile.write(body[offset:offset + 7])
        self.wfile.flush()
        self.close_connection = True

    def log_message(self, *_args: object) -> None:
        pass


def free_port() -> int:
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def health(url: str) -> dict:
    parsed = urlsplit(url)
    with contextlib.closing(http.client.HTTPConnection(parsed.hostname, parsed.port, timeout=2)) as connection:
        connection.request("GET", "/health")
        reply = connection.getresponse()
        if reply.status != 200:
            raise RuntimeError("canary health unavailable")
        return json.loads(reply.read(1024 * 1024))


@contextlib.contextmanager
def staged_proxy(dll: Path, root: Path, upstream: str, profile: str, model: str):
    port = free_port()
    settings = {
        "RoutingOptions": {"ListenUrl": f"http://127.0.0.1:{port}", "VerboseRequests": False, "PrimaryBackend": {"BaseUrl": upstream, "RewriteModel": model, "ToolOutputContract": "StrictNative", "CureRawToolCalls": False, "Temperature": 0.4, "TopP": 1.0}},
        "LoopGuardOptions": {"Enabled": True, "WedgeEnabled": profile != "safe"},
        "UnavailableToolRetryOptions": {"Enabled": profile == "retry", "MaxRetries": 1, "IncludeRequestContext": True, "RetryRawToolCallAborts": False, "MaxResponseBytes": 16777216},
        "MultimodalOptions": {"Enabled": False},
        "Logging": {"LogLevel": {"Default": "Warning"}},
    }
    with tempfile.TemporaryDirectory(prefix="proxy-canary-", dir=root) as directory:
        cwd = Path(directory)
        (cwd / "appsettings.json").write_text(json.dumps(settings))
        env = {key: os.environ[key] for key in ("HOME", "PATH", "DOTNET_ROOT", "LANG") if key in os.environ}
        env["DOTNET_GCHeapHardLimit"] = "0x10000000"
        with (cwd / "proxy.log").open("wb") as log:
            process = subprocess.Popen(["dotnet", str(dll)], cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT)
            url = f"http://127.0.0.1:{port}"
            try:
                deadline = time.monotonic() + 20
                while True:
                    if process.poll() is not None:
                        raise RuntimeError("staged proxy exited before health check")
                    try:
                        resolved = health(url)
                        break
                    except (OSError, ValueError):
                        if time.monotonic() > deadline:
                            raise RuntimeError("staged proxy startup deadline exceeded") from None
                        time.sleep(0.1)
                if resolved.get("primary", {}).get("toolOutputContract") != "StrictNative" or resolved["primary"].get("cureRawToolCalls") is not False:
                    raise RuntimeError("staged proxy strict contract mismatch")
                yield url, resolved
            finally:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)


def fetch(url: str, payload: dict, timeout: int = 90) -> tuple[int, bytes, bool]:
    encoded = json.dumps(payload, sort_keys=True, separators=(",", ":")).encode()
    try:
        status, raw = contract.open_request(url + "/v1/chat/completions", encoded, timeout)
        return status, raw, False
    except (OSError, http.client.HTTPException):
        return 0, b"", True


def synthetic(args: argparse.Namespace, output: Path) -> list[dict]:
    server = HTTPServer(("127.0.0.1", 0), FixtureHandler)
    server.request_count = 0
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    records = []
    try:
        upstream = f"http://127.0.0.1:{server.server_port}"
        for profile in ("wedge", "retry", "safe"):
            with staged_proxy(args.dll, args.scratch_root, upstream, profile, MODEL) as (url, _resolved):
                for case in fixtures():
                    server.case = case
                    before = server.request_count
                    payload = {"model": MODEL, "stream": case["stream"], "messages": [{"role": "user", "content": "Synthetic replay only."}], "tools": TOOLS, "tool_choice": "auto"}
                    if case["name"] == "prose":
                        payload.pop("tools")
                        payload.pop("tool_choice")
                    status, raw, failed = fetch(url, payload, 10)
                    attempts = server.request_count - before
                    expected_attempts = 2 if profile == "retry" and case["name"] == "rejection-json" else 1
                    passed = failed if case.get("fault") else not failed and raw == case["body"] and status == case.get("status", 200)
                    passed = passed and attempts == expected_attempts
                    name = f"{profile}-{case['name']}"
                    if not case.get("fault"):
                        (output / (name + (".sse" if case["stream"] else ".json"))).write_bytes(raw)
                    records.append({"case": name, "outcome": "PASS" if passed else "FAIL", "byte_identical": raw == case["body"], "transport_failure": failed, "upstream_attempts": attempts, "expected_attempts": expected_attempts})
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)
    return records


def live(args: argparse.Namespace, output: Path) -> list[dict]:
    records = []
    base_request = {"model": args.model, "stream": True, "temperature": 0.4, "top_p": 1.0, "max_tokens": 256, "chat_template_kwargs": {"enable_thinking": False}, "messages": [{"role": "user", "content": "Write four short sentences about testing software. Answer in plain prose. Do not call tools."}]}
    for profile in ("wedge", "retry", "safe"):
        with staged_proxy(args.dll, args.scratch_root, args.upstream, profile, args.model) as (url, resolved):
            (output / f"{profile}-health.json").write_text(json.dumps(resolved, indent=2) + "\n")
            probes = [("no-tools-1", base_request), ("tools-prose", {**base_request, "tools": TOOLS, "tool_choice": "auto"}), ("native-call", {**base_request, "tools": TOOLS, "tool_choice": "required", "messages": [{"role": "user", "content": "Call get_status exactly once with an empty JSON object. Do not execute it."}]})]
            if profile == "wedge":
                probes[1:1] = [("no-tools-2", base_request), ("no-tools-3", base_request)]
            for name, payload in probes:
                for side, target in (("direct", args.upstream), ("canary", url)):
                    status, raw, failed = fetch(target, payload)
                    state, reasons, facts = contract.classify_sse(raw, {"kind": "complete_success", "incremental_prose": name != "native-call"})
                    if status != 200 or failed:
                        state, reasons = "FAIL", ["HTTP or transport failure"]
                    if name == "native-call":
                        _events, decoded, _separated = contract.decoded_events(raw)
                        calls = contract.calls_of(decoded)
                        try:
                            valid_call = len(calls) == 1 and calls[0]["function"]["name"] == "get_status" and json.loads(calls[0]["function"]["arguments"]) == {}
                        except (KeyError, TypeError, ValueError):
                            valid_call = False
                        if not valid_call:
                            state, reasons = "FAIL", ["required offered call or exact fixture arguments missing"]
                    elif facts.get("call_count"):
                        state, reasons = "FAIL", ["unexpected executable call for prose task"]
                    key = f"{profile}-{name}-{side}"
                    (output / f"{key}.sse").write_bytes(raw)
                    records.append({"case": key, "outcome": state, "reasons": reasons, "facts": facts, "request_sha256": contract.sha256_bytes(json.dumps(payload, sort_keys=True, separators=(",", ":")).encode()), "response_sha256": contract.sha256_bytes(raw)})
    return records


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--scratch-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--mode", choices=("synthetic", "live"), default="synthetic")
    parser.add_argument("--upstream", default="http://127.0.0.1:8100")
    parser.add_argument("--model", default=MODEL)
    args = parser.parse_args()
    args.dll = args.dll.resolve(strict=True)
    parsed = urlsplit(args.upstream)
    if parsed.scheme != "http" or parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in ("", "/") or not parsed.hostname or not ipaddress.ip_address(parsed.hostname).is_loopback:
        parser.error("upstream must be a loopback HTTP origin without credentials")
    args.scratch_root.mkdir(parents=True, exist_ok=True)
    args.output.mkdir(parents=True, exist_ok=False)
    records = synthetic(args, args.output) if args.mode == "synthetic" else live(args, args.output)
    summary = {state: sum(record["outcome"] == state for record in records) for state in ("PASS", "FAIL", "INCONCLUSIVE")}
    report = {"mode": args.mode, "generated_tools_executed": False, "sequential": True, "profiles": {"wedge": {"wedge": True, "retry": False}, "retry": {"wedge": True, "retry": True}, "safe": {"wedge": False, "retry": False}}, "limits": "Text/tool forwarding only. Multimodal disabled in isolated canaries. No production, SDK, agent, STT or rollback acceptance.", "summary": summary, "records": records}
    (args.output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(summary))
    return 0 if summary["FAIL"] == 0 and summary["INCONCLUSIVE"] == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
