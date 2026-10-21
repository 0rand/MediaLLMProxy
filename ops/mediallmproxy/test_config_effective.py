#!/usr/bin/env python3
"""Offline standard-library coverage for ops/effective_config.py."""
from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VERIFY = ROOT / "ops" / "effective_config.py"
CONFIG_SHOW = ROOT / "ops" / "mediallmproxy" / "config-show.sh"

BASE = {
    "RoutingOptions": {"PrimaryBackend": {"BaseUrl": "http://secret-primary.invalid", "ToolOutputContract": "StrictNative", "CureRawToolCalls": False, "Temperature": 0.2, "TopP": 0.9}},
    "MultimodalOptions": {"Enabled": True, "VisionBackend": {"BaseUrl": "http://secret-vision.invalid"}, "AudioBackend": {"BaseUrl": "http://secret-audio.invalid"}, "VideoSupport": False, "DetourVision": True, "DetourAudio": True, "RehomeToolMedia": False, "RehomeAtEnd": False, "UrlPolicy": {"AllowDataUrls": True, "AllowHttpsHosts": ["media.example.invalid"], "MaxMediaBytes": 42}},
    "LoopGuardOptions": {"Enabled": True, "WedgeEnabled": False, "ReasoningTokenThreshold": 7},
    "UnavailableToolRetryOptions": {"Enabled": False, "MaxRetries": 1, "RetryRawToolCallAborts": True, "IncludeRequestContext": True, "MaxResponseBytes": 100},
}
SAFE_DELTA = {
    "RoutingOptions": {"PrimaryBackend": {"ToolOutputContract": "StrictNative", "CureRawToolCalls": False}},
    "LoopGuardOptions": {"WedgeEnabled": False},
    "UnavailableToolRetryOptions": {"Enabled": False},
}


def deep_merge(base, overlay):
    result = json.loads(json.dumps(base))
    for key, value in overlay.items():
        if isinstance(value, dict) and isinstance(result.get(key), dict):
            result[key] = deep_merge(result[key], value)
        else:
            result[key] = value
    return result


def set_dotted(value, path, leaf):
    current = value
    keys = path.split(".")
    for key in keys[:-1]:
        current = current.setdefault(key, {})
    current[keys[-1]] = leaf


def config_path(value, path):
    for key in path.split(":"):
        value = value[key]
    return value


def health_for(config):
    import importlib.util
    spec = importlib.util.spec_from_file_location("effective_config", VERIFY)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader
    spec.loader.exec_module(module)
    health = {}
    for health_key, config_key in module.CHECKS.items():
        set_dotted(health, health_key, module.expected_health_value(config, health_key, config_key))
    set_dotted(health, "primary.cureRawToolCalls", False)
    return health


class HealthServer:
    def __init__(self, payload):
        self.payload = payload
        outer = self
        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(json.dumps(outer.payload).encode())
            def log_message(self, *_):
                pass
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
    @property
    def url(self):
        return f"http://127.0.0.1:{self.server.server_port}/health"
    def close(self):
        self.server.shutdown()
        self.thread.join()


class EffectiveConfigTests(unittest.TestCase):
    def fixture(self, selected=SAFE_DELTA):
        directory = tempfile.TemporaryDirectory()
        root = Path(directory.name)
        (root / "appsettings.json").write_text(json.dumps(BASE))
        (root / "appsettings.Production.json").write_text(json.dumps(selected))
        return directory, root

    def run_verify(self, root, *extra):
        return subprocess.run([sys.executable, str(VERIFY), "--config-dir", str(root), "--environment", "Production", *extra], text=True, capture_output=True)

    def test_offline_safe_mode_nested_precedence_and_redaction(self):
        directory, root = self.fixture()
        self.addCleanup(directory.cleanup)
        result = self.run_verify(root, "--offline", "--require-safe-mode")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("<url sha256=", result.stdout)
        self.assertNotIn("secret-primary", result.stdout)
        self.assertIn("primary.sampling.temperature=0.2", result.stdout)

        wrapper = subprocess.run(["bash", str(CONFIG_SHOW), "--config-dir", str(root), "--environment", "Production", "--offline", "--require-safe-mode"], text=True, capture_output=True)
        self.assertEqual(wrapper.returncode, 0, wrapper.stdout + wrapper.stderr)

    def test_environment_shadow_precedes_json_and_fails_safe_mode(self):
        directory, root = self.fixture()
        self.addCleanup(directory.cleanup)
        result = self.run_verify(root, "--offline", "--require-safe-mode", "--env", "RoutingOptions__PrimaryBackend__CureRawToolCalls=true")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("StrictNative requires", result.stdout)

    def test_command_override_precedes_environment(self):
        directory, root = self.fixture()
        self.addCleanup(directory.cleanup)
        result = self.run_verify(root, "--offline", "--require-safe-mode", "--env", "LoopGuardOptions__WedgeEnabled=true", "--command-override", "LoopGuardOptions:WedgeEnabled=false")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_ambiguous_environment_selectors_fail_closed(self):
        directory, root = self.fixture()
        self.addCleanup(directory.cleanup)
        result = self.run_verify(root, "--offline", "--env", "ASPNETCORE_ENVIRONMENT=Production", "--env", "DOTNET_ENVIRONMENT=Development")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("ambiguous environment selector", result.stdout)

    def test_missing_or_invalid_required_environment_config_fails(self):
        directory, root = self.fixture()
        self.addCleanup(directory.cleanup)
        (root / "appsettings.Production.json").unlink()
        missing = self.run_verify(root, "--offline")
        self.assertNotEqual(missing.returncode, 0)
        self.assertIn("required config missing", missing.stdout)
        (root / "appsettings.Production.json").write_text("{")
        invalid = self.run_verify(root, "--offline")
        self.assertNotEqual(invalid.returncode, 0)
        self.assertIn("invalid config file", invalid.stdout)

    def test_live_stale_health_mismatch_and_unreachable_active_fail(self):
        directory, root = self.fixture()
        self.addCleanup(directory.cleanup)
        config = deep_merge(BASE, SAFE_DELTA)
        stale = health_for(config)
        stale["loopGuard"]["wedgeEnabled"] = True
        server = HealthServer(stale)
        self.addCleanup(server.close)
        result = self.run_verify(root, "--fixture-live", "--require-safe-mode", "--health-url", server.url)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("live mismatch: loopGuard.wedgeEnabled", result.stdout)
        unreachable = self.run_verify(root, "--fixture-live", "--require-safe-mode", "--health-url", "http://127.0.0.1:1/health")
        self.assertNotEqual(unreachable.returncode, 0)
        self.assertIn("health is unreachable", unreachable.stdout)

    def test_health_urls_are_sanitized_and_remote_target_is_rejected(self):
        directory, root = self.fixture()
        self.addCleanup(directory.cleanup)
        unsafe = deep_merge(BASE, SAFE_DELTA)
        unsafe["RoutingOptions"]["PrimaryBackend"]["BaseUrl"] = "http://user:token@127.0.0.1:8000/private?key=token#fragment"
        (root / "appsettings.json").write_text(json.dumps(unsafe))
        health = health_for(unsafe)
        self.assertEqual(health["primary"]["url"], "http://127.0.0.1:8000/private")
        self.assertNotIn("token", health["primary"]["url"])
        remote = self.run_verify(root, "--fixture-live", "--require-safe-mode", "--health-url", "http://198.51.100.10/health")
        self.assertNotEqual(remote.returncode, 0)
        self.assertIn("health is unreachable", remote.stdout)

    def test_retired_b_is_skipped_without_network_or_start(self):
        result = subprocess.run([sys.executable, str(VERIFY), "--instance", "b"], text=True, capture_output=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("RETIRED", result.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
