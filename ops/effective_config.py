#!/usr/bin/env python3
"""Fail-closed effective-config verifier for MediaLLMProxy Proxy A.

This source is intentionally portable. The installer must supply a content root or
let the verifier read the target unit's WorkingDirectory. It never starts units,
prints secrets, or writes configuration.
"""
from __future__ import annotations

import argparse
import hashlib
import ipaddress
import json
import re
import shlex
import subprocess
import urllib.error
import urllib.request
from urllib.parse import urlsplit
from copy import deepcopy
from pathlib import Path
from typing import Any

UNIT_A = "media-llm-proxy"
HEALTH_A = "http://127.0.0.1:8000/health"
ENV_NAMES = ("ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT")
CONFIG_PREFIXES = (
    "RoutingOptions", "MultimodalOptions", "LoopGuardOptions", "UnavailableToolRetryOptions",
)
# Health key, configuration key. These intentionally contain only behavioral fields.
CHECKS = {
    "primary.url": "RoutingOptions:PrimaryBackend:BaseUrl",
    "primary.toolOutputContract": "RoutingOptions:PrimaryBackend:ToolOutputContract",
    "primary.cureRawToolCallsRequested": "RoutingOptions:PrimaryBackend:CureRawToolCalls",
    "primary.sampling.temperature": "RoutingOptions:PrimaryBackend:Temperature",
    "primary.sampling.top_p": "RoutingOptions:PrimaryBackend:TopP",
    "bridge.enabled": "MultimodalOptions:Enabled",
    "bridge.vision": "MultimodalOptions:VisionBackend:BaseUrl",
    "bridge.audio": "MultimodalOptions:AudioBackend:BaseUrl",
    "bridge.videoSupport": "MultimodalOptions:VideoSupport",
    "bridge.detourVision": "MultimodalOptions:DetourVision",
    "bridge.detourAudio": "MultimodalOptions:DetourAudio",
    "bridge.rehomeToolMedia": "MultimodalOptions:RehomeToolMedia",
    "bridge.rehomeAtEnd": "MultimodalOptions:RehomeAtEnd",
    "bridge.urlPolicy.allowDataUrls": "MultimodalOptions:UrlPolicy:AllowDataUrls",
    "bridge.urlPolicy.allowHttpsHostsSha256": "MultimodalOptions:UrlPolicy:AllowHttpsHosts",
    "bridge.urlPolicy.maxMediaBytes": "MultimodalOptions:UrlPolicy:MaxMediaBytes",
    "loopGuard.enabled": "LoopGuardOptions:Enabled",
    "loopGuard.wedgeEnabled": "LoopGuardOptions:WedgeEnabled",
    "loopGuard.threshold": "LoopGuardOptions:ReasoningTokenThreshold",
    "unavailableToolRetry.enabled": "UnavailableToolRetryOptions:Enabled",
    "unavailableToolRetry.policy": "UnavailableToolRetryOptions:Enabled",
    "unavailableToolRetry.shouldRetryOverridePolicy": "UnavailableToolRetryOptions:Enabled",
    "unavailableToolRetry.maxRetries": "UnavailableToolRetryOptions:MaxRetries",
    "unavailableToolRetry.retryRawToolCallAborts": "UnavailableToolRetryOptions:RetryRawToolCallAborts",
    "unavailableToolRetry.includeRequestContext": "UnavailableToolRetryOptions:IncludeRequestContext",
    "unavailableToolRetry.maxResponseBytes": "UnavailableToolRetryOptions:MaxResponseBytes",
}


def fail(problems: list[str], message: str) -> None:
    problems.append(message)


def redact(value: Any) -> str:
    """Return useful but non-sensitive diagnostics, never a raw URL/string/list."""
    if value is None:
        return "<null>"
    if isinstance(value, bool) or isinstance(value, int) or isinstance(value, float):
        return str(value).lower()
    if isinstance(value, list):
        return f"<list count={len(value)}>"
    if isinstance(value, str):
        kind = "url" if "://" in value else "string"
        digest = hashlib.sha256(value.encode("utf-8")).hexdigest()[:12]
        return f"<{kind} sha256={digest}>"
    return f"<{type(value).__name__}>"


def safe_health_url(value: Any) -> Any:
    if not isinstance(value, str) or not value:
        return value
    try:
        parsed = urlsplit(value)
        if not parsed.scheme or not parsed.hostname:
            return "<invalid-url>"
        host = f"[{parsed.hostname}]" if ":" in parsed.hostname else parsed.hostname
        port = f":{parsed.port}" if parsed.port is not None and parsed.port not in (80, 443) else ""
        return f"{parsed.scheme}://{host}{port}{parsed.path or '/'}"
    except ValueError:
        return "<invalid-url>"


def hash_health_hosts(value: Any) -> str:
    if not isinstance(value, list) or not all(isinstance(item, str) for item in value):
        return "<invalid-host-list>"
    return hashlib.sha256("\n".join(sorted(value)).encode("utf-8")).hexdigest()


def expected_health_value(config: dict[str, Any], health_key: str, config_key: str) -> Any:
    value = get_path(config, config_key)
    if health_key in ("primary.url", "bridge.vision", "bridge.audio"):
        return safe_health_url(value)
    if health_key == "bridge.urlPolicy.allowHttpsHostsSha256":
        return hash_health_hosts(value)
    if health_key == "unavailableToolRetry.policy":
        return "strict-native-guard-retry-once" if value else "disabled"
    if health_key == "unavailableToolRetry.shouldRetryOverridePolicy":
        return "no-request-header-override"
    return value


def deep_merge(base: dict[str, Any], overlay: dict[str, Any]) -> dict[str, Any]:
    result = deepcopy(base)
    for key, value in overlay.items():
        if isinstance(value, dict) and isinstance(result.get(key), dict):
            result[key] = deep_merge(result[key], value)
        else:
            result[key] = deepcopy(value)
    return result


def get_path(value: Any, path: str) -> Any:
    current = value
    for key in path.split(":"):
        if not isinstance(current, dict) or key not in current:
            return None
        current = current[key]
    return current


def set_path(value: dict[str, Any], path: str, raw: str) -> None:
    keys = path.split(":")
    target = value
    for key in keys[:-1]:
        child = target.get(key)
        if child is None:
            child = {}
            target[key] = child
        if not isinstance(child, dict):
            raise ValueError(f"override conflicts with scalar section {key}")
        target = child
    old = target.get(keys[-1])
    target[keys[-1]] = parse_value(raw, old)


def parse_value(raw: str, old: Any) -> Any:
    if raw.lower() == "null":
        return None
    if isinstance(old, bool):
        if raw.lower() not in ("true", "false"):
            raise ValueError("boolean override must be true or false")
        return raw.lower() == "true"
    if isinstance(old, int) and not isinstance(old, bool):
        return int(raw)
    if isinstance(old, float):
        return float(raw)
    if isinstance(old, list):
        value = json.loads(raw)
        if not isinstance(value, list):
            raise ValueError("list override must be JSON array")
        return value
    return raw


def load_json(path: Path, required: bool, problems: list[str]) -> dict[str, Any]:
    if not path.is_file():
        if required:
            fail(problems, f"required config missing: {path.name}")
        return {}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        fail(problems, f"invalid config file {path.name}: {type(error).__name__}")
        return {}
    if not isinstance(data, dict):
        fail(problems, f"invalid config file {path.name}: root must be an object")
        return {}
    return data


def parse_assignments(items: list[str], source: str, problems: list[str]) -> dict[str, str]:
    result: dict[str, str] = {}
    for item in items:
        if "=" not in item:
            fail(problems, f"invalid {source} assignment (expected KEY=VALUE)")
            continue
        key, value = item.split("=", 1)
        if not key:
            fail(problems, f"invalid {source} assignment (empty key)")
            continue
        result[key] = value
    return result


def selector(environment: dict[str, str], requested: str | None, problems: list[str]) -> str:
    values = {environment[name] for name in ENV_NAMES if environment.get(name)}
    if requested:
        values.add(requested)
    if len(values) > 1:
        fail(problems, "ambiguous environment selector: ASPNETCORE_ENVIRONMENT, DOTNET_ENVIRONMENT, and --environment must agree")
        return "Production"
    selected = next(iter(values), "Production")
    if not re.fullmatch(r"[A-Za-z0-9._-]+", selected):
        fail(problems, "unsupported environment selector")
    return selected


def relevant_path(key: str, delimiter: str) -> str | None:
    path = key.replace(delimiter, ":")
    if not any(path == prefix or path.startswith(prefix + ":") for prefix in CONFIG_PREFIXES):
        return None
    # Config binding supports arbitrary keys, but the verifier cannot prove unknown behavior.
    if path not in CHECKS.values() and path not in ("RoutingOptions:PrimaryBackend:CureRawToolCalls",):
        return None
    return path


def apply_overrides(config: dict[str, Any], environment: dict[str, str], command: dict[str, str], problems: list[str]) -> set[str]:
    applied: set[str] = set()
    for key, raw in environment.items():
        path = relevant_path(key, "__")
        if path is None:
            if any(key.startswith(prefix + "__") for prefix in CONFIG_PREFIXES):
                fail(problems, f"unsupported relevant environment override: {key.split('__', 1)[0]}")
            continue
        try:
            set_path(config, path, raw)
            applied.add(path)
        except (TypeError, ValueError, json.JSONDecodeError):
            fail(problems, f"invalid environment override for {path}")
    for key, raw in command.items():
        path = relevant_path(key.lstrip("-"), ":")
        if path is None:
            stripped = key.lstrip("-")
            if any(stripped.startswith(prefix + ":") for prefix in CONFIG_PREFIXES):
                fail(problems, f"unsupported relevant command override: {stripped.split(':', 1)[0]}")
            continue
        try:
            set_path(config, path, raw)
            applied.add(path)
        except (TypeError, ValueError, json.JSONDecodeError):
            fail(problems, f"invalid command override for {path}")
    return applied


def systemd_context(unit: str, problems: list[str]) -> tuple[Path | None, dict[str, str], dict[str, str]]:
    def show(prop: str) -> str:
        try:
            return subprocess.run(["systemctl", "show", unit, "--property", prop, "--value"], capture_output=True, text=True, check=True, timeout=5).stdout.strip()
        except (OSError, subprocess.SubprocessError):
            fail(problems, f"cannot inspect unit {unit} for {prop}")
            return ""

    content_root = show("WorkingDirectory")
    env_text = show("Environment")
    exec_text = show("ExecStart")
    environment = parse_assignments(shlex.split(env_text), "unit environment", problems)
    command: dict[str, str] = {}
    for token in shlex.split(exec_text):
        if token.startswith("--") and "=" in token:
            key, value = token[2:].split("=", 1)
            command[key] = value
    return (Path(content_root) if content_root else None, environment, command)


def health_value(health: dict[str, Any], dotted: str) -> Any:
    current: Any = health
    for key in dotted.split("."):
        if not isinstance(current, dict) or key not in current:
            return None
        current = current[key]
    return current


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        raise urllib.error.HTTPError(request.full_url, code, "redirect rejected", headers, fp)


def fetch_health(url: str, problems: list[str]) -> dict[str, Any] | None:
    try:
        parsed = urlsplit(url)
        if parsed.scheme not in ("http", "https") or parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise ValueError("health URL must be a plain local HTTP(S) URL")
        if parsed.hostname is None or not ipaddress.ip_address(parsed.hostname).is_loopback:
            raise ValueError("health URL must target a literal loopback address")
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        with opener.open(url, timeout=5) as response:
            value = json.load(response)
        if not isinstance(value, dict):
            raise ValueError("health root was not an object")
        return value
    except (OSError, ValueError, urllib.error.URLError, json.JSONDecodeError):
        fail(problems, "active Proxy A health is unreachable or invalid")
        return None


def validate_config(config: dict[str, Any], origins: set[str], require_safe_mode: bool, problems: list[str]) -> None:
    for path in CHECKS.values():
        if get_path(config, path) is None and path not in ("RoutingOptions:PrimaryBackend:Temperature", "RoutingOptions:PrimaryBackend:TopP"):
            fail(problems, f"unverified required gate: {path}")
    contract = get_path(config, "RoutingOptions:PrimaryBackend:ToolOutputContract")
    requested_cure = get_path(config, "RoutingOptions:PrimaryBackend:CureRawToolCalls")
    if contract not in ("StrictNative", "LegacyRaw"):
        fail(problems, "unsupported tool-output contract")
    if contract == "StrictNative" and requested_cure is not False:
        # StrictNative makes it ineffective, but explicit false documents the safe request.
        fail(problems, "StrictNative requires CureRawToolCalls=false as the requested configuration")
    retry = get_path(config, "UnavailableToolRetryOptions:MaxRetries")
    if not isinstance(retry, int) or retry < 0 or retry > 1:
        fail(problems, "UnavailableToolRetryOptions:MaxRetries must be 0 or 1")
    hosts = get_path(config, "MultimodalOptions:UrlPolicy:AllowHttpsHosts")
    if not isinstance(hosts, list) or not all(isinstance(item, str) for item in hosts):
        fail(problems, "UrlPolicy:AllowHttpsHosts must be a string list")
    if require_safe_mode:
        required = {
            "LoopGuardOptions:WedgeEnabled": False,
            "UnavailableToolRetryOptions:Enabled": False,
            "RoutingOptions:PrimaryBackend:ToolOutputContract": "StrictNative",
            "RoutingOptions:PrimaryBackend:CureRawToolCalls": False,
        }
        for path, expected in required.items():
            if get_path(config, path) != expected:
                fail(problems, f"safe-mode requirement failed: {path}")
            if path not in origins:
                fail(problems, f"safe-mode requirement is not explicit: {path}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Verify effective MediaLLMProxy configuration without starting services.")
    parser.add_argument("--instance", default="a", choices=("a", "b"))
    parser.add_argument("--config-dir", type=Path)
    parser.add_argument("--environment")
    parser.add_argument("--env", action="append", default=[], metavar="KEY=VALUE")
    parser.add_argument("--command-override", action="append", default=[], metavar="KEY=VALUE")
    parser.add_argument("--unit", default=UNIT_A)
    parser.add_argument("--health-url")
    parser.add_argument("--offline", action="store_true")
    parser.add_argument("--fixture-live", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--require-safe-mode", action="store_true")
    args = parser.parse_args(argv)
    if args.offline and args.fixture_live:
        parser.error("--offline and --fixture-live cannot be combined")
    if args.instance == "b":
        print("RETIRED: Proxy B is deliberately skipped; it was not inspected or started.")
        return 0

    problems: list[str] = []
    environment = parse_assignments(args.env, "--env", problems)
    command = parse_assignments(args.command_override, "--command-override", problems)
    config_dir = args.config_dir
    # A live verification must model the target unit's selectors and overrides.
    # The fixture escape hatch exists only for the standard-library test server.
    if config_dir is None or (not args.offline and not args.fixture_live):
        unit_root, unit_env, unit_command = systemd_context(args.unit, problems)
        if config_dir is None:
            config_dir = unit_root
        environment = {**unit_env, **environment}
        command = {**unit_command, **command}
    if config_dir is None or not config_dir.is_dir():
        fail(problems, "content root is missing or not a directory")
        config_dir = Path(".")
    selected = selector(environment, args.environment, problems)
    base = load_json(config_dir / "appsettings.json", True, problems)
    selected_file = config_dir / f"appsettings.{selected}.json"
    overlay = load_json(selected_file, True, problems)
    effective = deep_merge(base, overlay)
    origins = {path for path in CHECKS.values() if get_path(base, path) is not None}
    origins.update(path for path in CHECKS.values() if get_path(overlay, path) is not None)
    origins.update(apply_overrides(effective, environment, command, problems))
    validate_config(effective, origins, args.require_safe_mode, problems)

    print(f"instance=a environment={selected} config=verified")
    print("effective requested gates (redacted):")
    for health_key, config_key in CHECKS.items():
        value = expected_health_value(effective, health_key, config_key)
        print(f"  {health_key}={redact(value)}")
    contract = get_path(effective, "RoutingOptions:PrimaryBackend:ToolOutputContract")
    requested = get_path(effective, "RoutingOptions:PrimaryBackend:CureRawToolCalls")
    effective_cure = bool(requested) and contract != "StrictNative"
    print(f"  primary.cureRawToolCalls={redact(effective_cure)}")

    if not args.offline:
        health = fetch_health(args.health_url or HEALTH_A, problems)
        if health is not None:
            for health_key, config_key in CHECKS.items():
                expected = expected_health_value(effective, health_key, config_key)
                actual = health_value(health, health_key)
                if actual != expected:
                    fail(problems, f"live mismatch: {health_key} expected={redact(expected)} actual={redact(actual)}")
            actual_cure = health_value(health, "primary.cureRawToolCalls")
            if actual_cure != effective_cure:
                fail(problems, f"live mismatch: primary.cureRawToolCalls expected={redact(effective_cure)} actual={redact(actual_cure)}")
    if problems:
        print("FAILED:")
        for problem in problems:
            print(f"  - {problem}")
        return 1
    print("OK: effective configuration is verified" + (" offline" if args.offline else " against active health"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
