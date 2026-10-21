"""Hermetic checks for actual-DLL canary fixture contracts and launch settings."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import canary_check as canary


class CanaryTests(unittest.TestCase):
    def test_success_fixtures_have_complete_exact_terminal_events(self):
        for fixture in canary.fixtures():
            if not fixture["stream"] or fixture["name"] in {"native-rejection", "done-only", "truncated-event", "transport-fault"}:
                continue
            with self.subTest(case=fixture["name"]):
                state, reasons, _facts = canary.contract.classify_sse(fixture["body"], {"kind": "complete_success"})
                self.assertEqual(state, "PASS", reasons)

    def test_rejection_fixture_is_typed_and_contains_no_executable_call(self):
        fixture = next(case for case in canary.fixtures() if case["name"] == "native-rejection")
        state, reasons, _facts = canary.contract.classify_sse(fixture["body"], {"kind": "native_rejection"})
        self.assertEqual(state, "PASS", reasons)

    def test_incomplete_fixture_is_not_repaired(self):
        fixture = next(case for case in canary.fixtures() if case["name"] == "truncated-event")
        state, _reasons, _facts = canary.contract.classify_sse(fixture["body"], {"kind": "complete_success"})
        self.assertEqual(state, "FAIL")
        self.assertNotIn(b"[DONE]", fixture["body"])

    def test_launch_is_loopback_isolated_and_cleanup_always_terminates_child(self):
        resolved = {"primary": {"toolOutputContract": "StrictNative", "cureRawToolCalls": False}}
        for profile in ("wedge", "retry", "safe"):
            with self.subTest(profile=profile), tempfile.TemporaryDirectory() as root:
                seen = {}

                def spawn(argv, **kwargs):
                    seen["argv"] = argv
                    seen["settings"] = json.loads((Path(kwargs["cwd"]) / "appsettings.json").read_text())
                    seen["env"] = kwargs["env"]
                    return process

                from unittest.mock import Mock
                process = Mock()
                process.poll.return_value = None
                with patch.object(canary.subprocess, "Popen", side_effect=spawn), patch.object(canary, "health", return_value=resolved), patch.dict(canary.os.environ, {"RoutingOptions__PrimaryBackend__CureRawToolCalls": "true", "ASPNETCORE_ENVIRONMENT": "unsafe"}):
                    with self.assertRaisesRegex(RuntimeError, "probe failed"):
                        with canary.staged_proxy(Path("/fixture/proxy.dll"), Path(root), "http://127.0.0.1:8100", profile, "fixture") as (url, _health):
                            self.assertTrue(url.startswith("http://127.0.0.1:"))
                            raise RuntimeError("probe failed")
                process.terminate.assert_called_once()
                process.wait.assert_called_once_with(timeout=5)
                self.assertNotIn("ASPNETCORE_ENVIRONMENT", seen["env"])
                self.assertNotIn("RoutingOptions__PrimaryBackend__CureRawToolCalls", seen["env"])
                self.assertEqual(seen["settings"]["UnavailableToolRetryOptions"]["Enabled"], profile == "retry")
                self.assertEqual(seen["settings"]["LoopGuardOptions"]["WedgeEnabled"], profile != "safe")


if __name__ == "__main__":
    unittest.main()
