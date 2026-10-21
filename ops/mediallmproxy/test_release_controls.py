#!/usr/bin/env python3
"""Offline tests for Package 4B release controls.

No test invokes update.sh --apply, systemctl, a real endpoint, or a model. The
fixtures use a fresh local Git repository and ordinary files only.
"""

from __future__ import annotations

import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
RELEASE = REPO_ROOT / "ops" / "mediallmproxy" / "release.py"


class ReleaseControlTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(dir=REPO_ROOT)
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        self.publish = self.root / "publish"
        self.config = self.root / "config"
        self.safe = self.root / "safe"
        self.releases = self.root / "releases"
        self.evidence = self.root / "evidence"
        self._git("init", str(self.source))
        self._git("-C", str(self.source), "config", "user.email", "offline@example.invalid")
        self._git("-C", str(self.source), "config", "user.name", "Offline Test")
        (self.source / "tracked.txt").write_text("source\n", encoding="utf-8")
        self._git("-C", str(self.source), "add", "tracked.txt")
        self._git("-C", str(self.source), "commit", "-m", "fixture")
        self.ref = self._git("-C", str(self.source), "rev-parse", "HEAD").stdout.strip()
        self.publish.mkdir()
        for name in ("OAIPreRouter.Cli.dll", "OAIPreRouter.Cli.deps.json", "OAIPreRouter.Cli.runtimeconfig.json", "extra.dat"):
            (self.publish / name).write_text(f"new-{name}\n", encoding="utf-8")
        for directory in (self.config, self.safe):
            directory.mkdir()
            (directory / "appsettings.json").write_text("{}\n", encoding="utf-8")
            (directory / "appsettings.Production.json").write_text("{}\n", encoding="utf-8")
        self.evidence.mkdir()
        (self.evidence / "sdk.txt").write_text("SDK 10 runtime 10\n", encoding="utf-8")
        (self.evidence / "tests.txt").write_text("offline tests passed\n", encoding="utf-8")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def _git(self, *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(["git", *args], text=True, capture_output=True, check=True)

    def invoke(self, *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(["python3", str(RELEASE), *args], text=True, capture_output=True)

    def prepare_args(self, ref: str | None = None) -> list[str]:
        return [
            "prepare", "--target", "a", "--unit", "media-llm-proxy.service",
            "--source", str(self.source), "--approved-ref", ref or self.ref,
            "--publish-dir", str(self.publish), "--config-dir", str(self.config),
            "--safe-config-dir", str(self.safe), "--environment", "Production", "--releases-dir", str(self.releases),
            "--sdk-runtime-evidence", str(self.evidence / "sdk.txt"),
            "--test-evidence", str(self.evidence / "tests.txt"),
        ]

    def stage(self) -> Path:
        result = self.invoke(*self.prepare_args())
        self.assertEqual(result.returncode, 0, result.stderr)
        return Path(result.stdout.strip())

    def test_prepare_writes_sanitized_complete_manifest(self) -> None:
        release = self.stage()
        manifest = json.loads((release / "release-manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["source"]["approved_ref"], self.ref)
        self.assertEqual(manifest["target"], "a")
        self.assertIn("OAIPreRouter.Cli.dll", manifest["artifacts"])
        self.assertNotIn(str(Path.home()), (release / "release-manifest.json").read_text(encoding="utf-8"))

    def test_bad_config_fails_before_release_directory_is_created(self) -> None:
        (self.config / "appsettings.Production.json").write_text("{ not-json", encoding="utf-8")
        result = self.invoke(*self.prepare_args())
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("invalid required target configuration JSON", result.stderr)
        self.assertFalse(self.releases.exists())

    def test_wrong_ref_and_dirty_source_fail_closed(self) -> None:
        wrong = self.invoke(*self.prepare_args("0" * 40))
        self.assertNotEqual(wrong.returncode, 0)
        self.assertFalse(self.releases.exists())
        (self.source / "untracked.txt").write_text("dirty\n", encoding="utf-8")
        dirty = self.invoke(*self.prepare_args())
        self.assertNotEqual(dirty.returncode, 0)
        self.assertIn("dirty", dirty.stderr)

    def test_missing_publish_file_fails_closed(self) -> None:
        (self.publish / "OAIPreRouter.Cli.dll").unlink()
        result = self.invoke(*self.prepare_args())
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("missing required published artifacts", result.stderr)

    def test_tampered_release_cannot_promote_and_old_release_survives(self) -> None:
        release = self.stage()
        (release / "artifact" / "OAIPreRouter.Cli.dll").write_text("tampered\n", encoding="utf-8")
        live = self.root / "live" / "bin"
        live.mkdir(parents=True)
        (live / "OAIPreRouter.Cli.dll").write_text("old\n", encoding="utf-8")
        current = self.root / "current-config"
        shutil.copytree(self.config, current)
        result = self.invoke(
            "promote", "--target", "a", "--unit", "media-llm-proxy.service",
            "--release-dir", str(release), "--live-dir", str(live), "--config-dir", str(current),
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual((live / "OAIPreRouter.Cli.dll").read_text(encoding="utf-8"), "old\n")

    def test_promotion_backup_and_rollback_restore_old_artifacts_with_safe_config(self) -> None:
        release = self.stage()
        live = self.root / "live" / "bin"
        live.mkdir(parents=True)
        (live / "OAIPreRouter.Cli.dll").write_text("old\n", encoding="utf-8")
        (live / "OAIPreRouter.Cli.deps.json").write_text("old\n", encoding="utf-8")
        (live / "OAIPreRouter.Cli.runtimeconfig.json").write_text("old\n", encoding="utf-8")
        current = self.root / "current-config"
        shutil.copytree(self.config, current)
        (self.safe / "appsettings.json").write_text('{"safe":true}\n', encoding="utf-8")
        # Restage after making the vetted fallback distinct.
        shutil.rmtree(release)
        release = self.stage()
        promoted = self.invoke("promote", "--target", "a", "--unit", "media-llm-proxy.service", "--release-dir", str(release), "--live-dir", str(live), "--config-dir", str(current))
        self.assertEqual(promoted.returncode, 0, promoted.stderr)
        backup = Path(promoted.stdout.strip())
        self.assertTrue((backup / "artifact" / "OAIPreRouter.Cli.dll").is_file())
        self.assertEqual((live / "OAIPreRouter.Cli.dll").read_text(encoding="utf-8"), "new-OAIPreRouter.Cli.dll\n")
        rolled = self.invoke("rollback", "--target", "a", "--unit", "media-llm-proxy.service", "--backup-dir", str(backup), "--live-dir", str(live), "--config-dir", str(current))
        self.assertEqual(rolled.returncode, 0, rolled.stderr)
        self.assertEqual((live / "OAIPreRouter.Cli.dll").read_text(encoding="utf-8"), "old\n")
        self.assertEqual((current / "appsettings.json").read_text(encoding="utf-8"), '{"safe":true}\n')

    def test_config_root_can_be_parent_of_live_bin_without_recursive_backup(self) -> None:
        release = self.stage()
        content_root = self.root / "production-root"
        live = content_root / "bin"
        live.mkdir(parents=True)
        for name in ("OAIPreRouter.Cli.dll", "OAIPreRouter.Cli.deps.json", "OAIPreRouter.Cli.runtimeconfig.json"):
            (live / name).write_text("old\n", encoding="utf-8")
        shutil.copy2(self.config / "appsettings.json", content_root / "appsettings.json")
        shutil.copy2(self.config / "appsettings.Production.json", content_root / "appsettings.Production.json")
        result = self.invoke("promote", "--target", "a", "--unit", "media-llm-proxy.service", "--release-dir", str(release), "--live-dir", str(live), "--config-dir", str(content_root))
        self.assertEqual(result.returncode, 0, result.stderr)
        backup = Path(result.stdout.strip())
        self.assertTrue((backup / "config" / "appsettings.json").is_file())
        self.assertFalse((backup / "config" / "bin").exists())
        self.assertEqual((live / "OAIPreRouter.Cli.dll").read_text(encoding="utf-8"), "new-OAIPreRouter.Cli.dll\n")


if __name__ == "__main__":
    unittest.main()
