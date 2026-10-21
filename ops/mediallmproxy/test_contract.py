#!/usr/bin/env python3
"""Deterministic tests for the offline strict-contract runner."""
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RUNNER_PATH = ROOT / "ops/mediallmproxy/strict_contract.py"
PASS_FIXTURE = ROOT / "ops/mediallmproxy/fixtures/strict_contract_pass.json"
BAD_FIXTURE = ROOT / "ops/mediallmproxy/fixtures/strict_contract_incomplete_or_fail.json"

spec = importlib.util.spec_from_file_location("strict_contract", RUNNER_PATH)
strict_contract = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = strict_contract
spec.loader.exec_module(strict_contract)


class StrictContractTests(unittest.TestCase):
    def test_passing_synthetic_fixtures_are_exact_passes(self):
        cases = json.loads(PASS_FIXTURE.read_text())["cases"]
        outcomes = [strict_contract.classify_sse(case["raw_sse"].encode(), case["contract"])[0] for case in cases]
        self.assertEqual(outcomes, [strict_contract.PASS] * len(cases))

    def test_truncated_and_fabricated_literal_never_pass(self):
        cases = json.loads(BAD_FIXTURE.read_text())["cases"]
        outcomes = [strict_contract.classify_sse(case["raw_sse"].encode(), case["contract"])[0] for case in cases]
        self.assertEqual(outcomes, [strict_contract.INCONCLUSIVE, strict_contract.FAIL])

    def test_native_rejection_requires_exact_typed_error_then_done(self):
        raw = (
            b'data: {"error":{"type":"model_output_error","code":"unavailable_tool_call",'
            b'"source":"wrong.source"}}\n\ndata: [DONE]\n\n'
        )
        outcome, reasons, _ = strict_contract.classify_sse(raw, {"kind": "native_rejection"})
        self.assertEqual(outcome, strict_contract.FAIL)
        self.assertTrue(any("type, code, and source" in reason for reason in reasons))

    def test_done_must_have_final_blank_separator(self):
        raw = b'data: {"choices":[{"delta":{},"finish_reason":"stop"}]}\n\ndata: [DONE]\n'
        outcome, _, facts = strict_contract.classify_sse(raw, {"kind": "complete_success"})
        self.assertEqual(outcome, strict_contract.FAIL)
        self.assertFalse(facts["fully_separated"])

    def test_cli_nonzero_for_incomplete_mandatory_case_and_saves_no_raw_by_default(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "result"
            completed = subprocess.run(
                [sys.executable, str(RUNNER_PATH), "--fixture", str(BAD_FIXTURE), "--output", str(output)],
                check=False, capture_output=True, text=True,
            )
            self.assertEqual(completed.returncode, 1, completed.stderr)
            report = json.loads((output / "results.json").read_text())
            self.assertEqual(report["summary"], {"PASS": 0, "FAIL": 1, "INCONCLUSIVE": 1})
            self.assertFalse((output / "synthetic-raw-sse.json").exists())

    def test_loopback_only_rejects_nonloopback_target(self):
        self.assertIsNone(strict_contract.validate_base_url("http://127.0.0.1:8000", True))
        self.assertIsNotNone(strict_contract.validate_base_url("http://192.0.2.10:8000", True))


if __name__ == "__main__":
    unittest.main()
