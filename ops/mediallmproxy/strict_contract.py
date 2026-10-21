#!/usr/bin/env python3
"""Run bounded strict-native SSE contracts without executing returned tools.

Fixture mode is fully offline and evaluates deterministic synthetic SSE supplied in
JSON fixtures.  Real mode sends sequential requests to explicit direct/proxy
endpoints, blocks redirects and ambient HTTP proxies, and stores hashes only for
live responses.  It is intentionally conservative: an uninduced model fixture is
INCONCLUSIVE, never PASS.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import ipaddress
import json
import socket
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

PASS = "PASS"
FAIL = "FAIL"
INCONCLUSIVE = "INCONCLUSIVE"
TERMINAL_FINISHES = {"stop", "tool_calls", "length", "content_filter"}
REQUIRED_NATIVE_ERROR = {
    "type": "model_output_error",
    "code": "unavailable_tool_call",
    "source": "tensorfold.tool_output_guard",
}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """Refuse all redirects instead of following a possibly external Location."""

    def redirect_request(self, req: Any, fp: Any, code: int, msg: str, headers: Any, newurl: str) -> None:
        return None


@dataclass(frozen=True)
class SseEvent:
    raw: bytes
    data: str | None
    complete_separator: bool


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat()


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def parse_sse(raw: bytes) -> tuple[list[SseEvent], bool]:
    """Return complete SSE events and whether bytes ended on an event separator."""
    events: list[SseEvent] = []
    start = 0
    index = 0
    while index < len(raw):
        line_end = raw.find(b"\n", index)
        if line_end < 0:
            break
        line = raw[index:line_end + 1]
        index = line_end + 1
        if line in (b"\n", b"\r\n"):
            event_raw = raw[start:index]
            lines = event_raw.replace(b"\r\n", b"\n").split(b"\n")
            data_lines = [part[5:].lstrip(b" ") for part in lines if part.startswith(b"data:")]
            data = b"\n".join(data_lines).decode("utf-8", errors="replace") if data_lines else None
            events.append(SseEvent(event_raw, data, True))
            start = index
    return events, start == len(raw)


def decoded_events(raw: bytes) -> tuple[list[SseEvent], list[dict[str, Any]], bool]:
    events, fully_separated = parse_sse(raw)
    json_events: list[dict[str, Any]] = []
    for event in events:
        if event.data and event.data != "[DONE]":
            try:
                parsed = json.loads(event.data)
            except json.JSONDecodeError:
                continue
            if isinstance(parsed, dict):
                json_events.append(parsed)
    return events, json_events, fully_separated


def choices_of(event: dict[str, Any]) -> Iterable[dict[str, Any]]:
    choices = event.get("choices", [])
    return choices if isinstance(choices, list) else []


def finish_count(events: Iterable[dict[str, Any]]) -> int:
    return sum(1 for event in events for choice in choices_of(event)
               if choice.get("finish_reason") in TERMINAL_FINISHES)


def content_parts(events: Iterable[dict[str, Any]]) -> list[str]:
    output: list[str] = []
    for event in events:
        for choice in choices_of(event):
            delta = choice.get("delta", {})
            if isinstance(delta, dict) and isinstance(delta.get("content"), str):
                output.append(delta["content"])
    return output


def calls_of(events: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    calls: list[dict[str, Any]] = []
    for event in events:
        for choice in choices_of(event):
            delta = choice.get("delta", {})
            if isinstance(delta, dict) and isinstance(delta.get("tool_calls"), list):
                calls.extend(item for item in delta["tool_calls"] if isinstance(item, dict))
    return calls


def native_errors(events: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    result = []
    for event in events:
        error = event.get("error")
        if isinstance(error, dict) and isinstance(error.get("type"), str):
            result.append(error)
    return result


def error_event_indexes(events: Iterable[SseEvent]) -> list[int]:
    indexes: list[int] = []
    for index, event in enumerate(events):
        if event.data and event.data != "[DONE]":
            try:
                parsed = json.loads(event.data)
            except json.JSONDecodeError:
                continue
            if isinstance(parsed, dict) and isinstance(parsed.get("error"), dict):
                indexes.append(index)
    return indexes


def classify_sse(raw: bytes, contract: dict[str, Any]) -> tuple[str, list[str], dict[str, Any]]:
    events, json_events, fully_separated = decoded_events(raw)
    done_indexes = [index for index, event in enumerate(events) if event.data == "[DONE]"]
    finishes = finish_count(json_events)
    content = content_parts(json_events)
    calls = calls_of(json_events)
    errors = native_errors(json_events)
    error_indexes = error_event_indexes(events)
    facts = {
        "event_count": len(events), "done_count": len(done_indexes), "finish_count": finishes,
        "content_event_count": len(content), "call_count": len(calls), "native_error_count": len(errors),
        "fully_separated": fully_separated, "has_trailing_usage": any("usage" in event for event in json_events),
    }
    reasons: list[str] = []
    if not fully_separated:
        reasons.append("stream ended without a complete blank-line SSE separator")
    if len(done_indexes) != 1:
        reasons.append("expected exactly one [DONE] event")
    elif done_indexes[0] != len(events) - 1:
        reasons.append("[DONE] was not the final complete event")
    kind = contract.get("kind")
    if kind == "complete_success":
        if finishes != 1:
            reasons.append("expected exactly one successful finish event")
        if contract.get("trailing_usage") and not facts["has_trailing_usage"]:
            reasons.append("expected a trailing usage event")
        if contract.get("incremental_prose") and len(content) < 2:
            reasons.append("expected multiple incremental prose content events")
        if errors:
            reasons.append("success stream contained native error")
    elif kind == "native_rejection":
        if len(errors) != 1:
            reasons.append("expected exactly one typed native error")
        elif any(errors[0].get(key) != value for key, value in REQUIRED_NATIVE_ERROR.items()):
            reasons.append("native error did not retain required type, code, and source")
        if error_indexes != [len(events) - 2]:
            reasons.append("native typed error must be immediately followed by [DONE]")
        if finishes:
            reasons.append("native rejection must not include a successful finish")
        if calls:
            reasons.append("native rejection must not include executable calls")
    elif kind == "truncated":
        # Any apparent completion would prove a malformed fixture, not a passing stream.
        if fully_separated and len(done_indexes) == 1 and finishes == 1:
            reasons.append("truncated fixture unexpectedly formed a complete successful stream")
        return INCONCLUSIVE, reasons or ["transport-truncated stream remains incomplete"], facts
    elif kind == "native_call":
        expected = contract.get("expected_calls", [])
        if finishes != 1:
            reasons.append("expected exactly one finish after native call")
        if calls != expected:
            reasons.append("native call changed or was not preserved exactly")
        if errors:
            reasons.append("native call stream contained native error")
    elif kind == "literal":
        expected_literal = contract.get("literal")
        if finishes != 1:
            reasons.append("expected exactly one finish after literal")
        if calls:
            reasons.append("literal was fabricated into executable tool calls")
        if expected_literal is not None and "".join(content) != expected_literal:
            reasons.append("literal content changed")
    else:
        return INCONCLUSIVE, ["unknown fixture contract kind"], facts
    return (PASS if not reasons else FAIL), reasons, facts


def is_loopback_url(base_url: str) -> bool:
    parsed = urllib.parse.urlparse(base_url)
    if parsed.scheme not in {"http", "https"} or not parsed.hostname:
        return False
    try:
        return ipaddress.ip_address(parsed.hostname).is_loopback
    except ValueError:
        try:
            return all(ipaddress.ip_address(address[4][0]).is_loopback
                       for address in socket.getaddrinfo(parsed.hostname, parsed.port or 80, type=socket.SOCK_STREAM))
        except OSError:
            return False


def validate_base_url(base_url: str, loopback_only: bool) -> str | None:
    parsed = urllib.parse.urlparse(base_url)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc or parsed.query or parsed.fragment:
        return "base URL must be an absolute http(s) origin without query or fragment"
    if loopback_only and not is_loopback_url(base_url):
        return "loopback-only mode rejected a non-loopback base URL"
    return None


def endpoint(base_url: str) -> str:
    return base_url.rstrip("/") + "/v1/chat/completions"


def read_bounded(response: Any, timeout_seconds: float, maximum_bytes: int = 4 * 1024 * 1024) -> tuple[bytes, bool]:
    """Read with an absolute deadline, even if a peer dribbles individual chunks."""
    deadline = time.monotonic() + timeout_seconds
    chunks: list[bytes] = []
    total = 0
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return b"".join(chunks), True
        try:
            response.fp.raw._sock.settimeout(remaining)
        except (AttributeError, OSError):
            pass
        try:
            chunk = response.read(min(8192, maximum_bytes + 1 - total))
        except (OSError, TimeoutError):
            return b"".join(chunks), True
        if not chunk:
            return b"".join(chunks), False
        chunks.append(chunk)
        total += len(chunk)
        if total > maximum_bytes:
            return b"".join(chunks), True


def open_request_detail(url: str, body: bytes, timeout_seconds: float) -> tuple[int, str, bytes, bool]:
    """Return response metadata for the runner's report-oriented real mode."""
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    request = urllib.request.Request(url, data=body, headers={"Content-Type": "application/json", "Accept": "text/event-stream"}, method="POST")
    try:
        with opener.open(request, timeout=timeout_seconds) as response:
            raw, truncated = read_bounded(response, timeout_seconds)
            return response.status, response.headers.get_content_type(), raw, truncated
    except urllib.error.HTTPError as error:
        raw, truncated = read_bounded(error, timeout_seconds)
        return error.code, error.headers.get_content_type(), raw, truncated


def open_request(url: str, body: bytes, timeout_seconds: float) -> tuple[int, bytes]:
    """Compatibility API for the canary harness.

    A bounded/deadline-truncated response is a transport fault, never a clean
    partial response.  Callers that need metadata can use open_request_detail.
    """
    status, _content_type, raw, truncated = open_request_detail(url, body, timeout_seconds)
    if truncated:
        raise OSError("response exceeded bounded total deadline or byte limit")
    return status, raw


def real_payload(case: dict[str, Any], model: str) -> dict[str, Any]:
    payload = case.get("request")
    if isinstance(payload, dict):
        return payload
    return {"model": model, "stream": True, "temperature": 0.4, "top_p": 1.0, "seed": 0, "max_tokens": 128,
            "messages": [{"role": "user", "content": "Write two plain-prose sentences about test isolation. Do not call a tool."}]}


def run_fixture(case: dict[str, Any]) -> dict[str, Any]:
    raw = case.get("raw_sse")
    if not isinstance(raw, str):
        return {"case": case.get("name", "unnamed"), "outcome": INCONCLUSIVE, "reasons": ["fixture has no raw_sse string"]}
    outcome, reasons, facts = classify_sse(raw.encode("utf-8"), case.get("contract", {}))
    return {"case": case.get("name", "unnamed"), "outcome": outcome, "reasons": reasons, "facts": facts,
            "synthetic": True, "raw_sse_sha256": sha256_bytes(raw.encode("utf-8"))}


def run_real(case: dict[str, Any], side: str, base_url: str, args: argparse.Namespace) -> dict[str, Any]:
    payload = real_payload(case, args.model)
    encoded = json.dumps(payload, sort_keys=True, separators=(",", ":")).encode("utf-8")
    try:
        http_status, content_type, body, transport_truncated = open_request_detail(endpoint(base_url), encoded, args.timeout_seconds)
    except (OSError, ValueError) as error:
        return {"case": case.get("name", "unnamed"), "side": side, "outcome": INCONCLUSIVE,
                "reasons": [f"request transport unavailable: {type(error).__name__}"], "request_sha256": sha256_bytes(encoded)}
    # Do not write live raw bodies. Only an absent fixture induction is inconclusive.
    contract = case.get("contract", {})
    if transport_truncated:
        outcome, reasons, facts = FAIL, ["response exceeded bounded total deadline or byte limit"], {}
    elif http_status != 200:
        outcome, reasons, facts = INCONCLUSIVE, [f"HTTP {http_status}"], {}
    elif content_type == "application/json":
        try:
            response = json.loads(body)
        except json.JSONDecodeError:
            outcome, reasons, facts = FAIL, ["200 JSON response was not valid JSON"], {}
        else:
            has_error = isinstance(response, dict) and isinstance(response.get("error"), dict)
            has_calls = bool(calls_of([{"choices": response.get("choices", [])}])) if isinstance(response, dict) else False
            outcome = PASS if contract.get("kind") == "native_rejection" and has_error and not has_calls else INCONCLUSIVE
            reasons, facts = ([] if outcome == PASS else ["model did not induce the required JSON fixture"], {"json_error": has_error, "call_count": int(has_calls)})
    else:
        outcome, reasons, facts = classify_sse(body, contract)
        structural = any("separator" in reason or "[DONE]" in reason or "finish" in reason for reason in reasons)
        literal_fabrication = contract.get("kind") == "literal" and any("fabricated" in reason for reason in reasons)
        altered_native_call = contract.get("kind") == "native_call" and facts.get("call_count", 0) > 0 and outcome == FAIL
        malformed_native_error = contract.get("kind") == "native_rejection" and facts.get("native_error_count", 0) > 0 and outcome == FAIL
        if outcome == FAIL and not (structural or literal_fabrication or altered_native_call or malformed_native_error):
            outcome = INCONCLUSIVE
            reasons = ["model did not induce the required deterministic fixture"] + reasons
    return {"case": case.get("name", "unnamed"), "side": side, "outcome": outcome, "reasons": reasons,
            "facts": facts, "http_status": http_status, "content_type": content_type, "request_sha256": sha256_bytes(encoded),
            "response_sha256": sha256_bytes(body), "transport_truncated": transport_truncated, "synthetic": False}


def load_cases(paths: list[Path]) -> list[dict[str, Any]]:
    cases: list[dict[str, Any]] = []
    for path in paths:
        loaded = json.loads(path.read_text(encoding="utf-8"))
        entries = loaded.get("cases") if isinstance(loaded, dict) else loaded
        if not isinstance(entries, list) or not all(isinstance(entry, dict) for entry in entries):
            raise ValueError(f"{path}: expected a JSON list or object with a cases list")
        cases.extend(entries)
    return cases


def write_report(output: Path, args: argparse.Namespace, records: list[dict[str, Any]], cases: list[dict[str, Any]]) -> None:
    output.mkdir(parents=True, exist_ok=False)
    summary = {state: sum(record.get("outcome") == state for record in records) for state in (PASS, FAIL, INCONCLUSIVE)}
    report = {"schema_version": 1, "started_utc": utc_now(), "mode": args.mode,
              "safety": {"generated_tools_executed": False, "sequential": True, "redirects_followed": False,
                         "live_raw_sse_saved": False, "loopback_only": args.loopback_only},
              "summary": summary, "records": records}
    (output / "results.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    if args.save_synthetic_raw and args.mode == "fixture":
        raw = {case.get("name", "unnamed"): case.get("raw_sse") for case in cases if isinstance(case.get("raw_sse"), str)}
        (output / "synthetic-raw-sse.json").write_text(json.dumps(raw, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("fixture", "real"), default="fixture")
    parser.add_argument("--fixture", type=Path, action="append", required=True, help="Synthetic or real-probe fixture JSON. Repeatable.")
    parser.add_argument("--output", type=Path, required=True, help="New output directory for request hashes and outcomes.")
    parser.add_argument("--direct-base-url")
    parser.add_argument("--proxy-base-url")
    parser.add_argument("--model", default="fixture")
    parser.add_argument("--timeout-seconds", type=float, default=30)
    parser.add_argument("--max-requests", type=int, default=16)
    parser.add_argument("--loopback-only", action="store_true", help="Require direct/proxy URLs to resolve to loopback.")
    parser.add_argument("--save-synthetic-raw", action="store_true", help="Save raw SSE only for fixture-mode synthetic evidence.")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        cases = load_cases(args.fixture)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"strict-contract: invalid fixture: {error}", file=sys.stderr)
        return 2
    if not cases or args.max_requests < 1 or args.timeout_seconds <= 0:
        print("strict-contract: cases must be non-empty and within positive --max-requests", file=sys.stderr)
        return 2
    if args.output.exists():
        print("strict-contract: --output must not already exist", file=sys.stderr)
        return 2
    records: list[dict[str, Any]] = []
    if args.mode == "fixture":
        records = [run_fixture(case) for case in cases]
    else:
        targets = [("direct", args.direct_base_url), ("proxy", args.proxy_base_url)]
        targets = [(side, url) for side, url in targets if url]
        if not targets:
            print("strict-contract: real mode requires --direct-base-url and/or --proxy-base-url", file=sys.stderr)
            return 2
        if len(cases) * len(targets) > args.max_requests:
            print("strict-contract: cases times targets exceeds --max-requests", file=sys.stderr)
            return 2
        for side, base_url in targets:
            error = validate_base_url(base_url, args.loopback_only)
            if error:
                print(f"strict-contract: {side}: {error}", file=sys.stderr)
                return 2
        for case in cases:
            for side, base_url in targets:
                records.append(run_real(case, side, base_url, args))
    if args.mode == "fixture" and len(cases) > args.max_requests:
        print("strict-contract: cases exceeds --max-requests", file=sys.stderr)
        return 2
    write_report(args.output, args, records, cases)
    summary = {state: sum(record.get("outcome") == state for record in records) for state in (PASS, FAIL, INCONCLUSIVE)}
    print("SUMMARY " + json.dumps(summary, sort_keys=True))
    return 0 if summary[FAIL] == 0 and summary[INCONCLUSIVE] == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
