#!/usr/bin/env python3
"""Immutable release staging and reversible directory promotion for Proxy A.

This tool deliberately has no build, git pull, service-manager, or network
operations.  ``update.sh`` is the explicit operator wrapper for the separately
approved restart and contract-gate steps.  Keeping filesystem promotion here
makes it practical to test the failure and rollback paths offline.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any


REQUIRED_ARTIFACTS = (
    "OAIPreRouter.Cli.dll",
    "OAIPreRouter.Cli.deps.json",
    "OAIPreRouter.Cli.runtimeconfig.json",
)
REF_RE = re.compile(r"^[0-9a-f]{40}$")


class ReleaseError(RuntimeError):
    """An operator-facing release safety violation."""


def fail(message: str) -> None:
    raise ReleaseError(message)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def hashes(root: Path) -> dict[str, str]:
    return {
        str(path.relative_to(root)): sha256(path)
        for path in sorted(root.rglob("*"))
        if path.is_file()
    }


def copy_tree(source: Path, destination: Path) -> None:
    if not source.is_dir():
        fail(f"required directory does not exist: {source}")
    shutil.copytree(source, destination, symlinks=True)


def config_names(environment: str) -> tuple[str, str]:
    if not re.fullmatch(r"[A-Za-z0-9._-]+", environment):
        fail("unsupported configuration environment selector")
    return ("appsettings.json", f"appsettings.{environment}.json")


def config_files(root: Path) -> list[Path]:
    if not root.is_dir():
        fail(f"required configuration root does not exist: {root}")
    result = [path for path in sorted(root.glob("appsettings*.json")) if path.is_file()]
    if any(path.is_symlink() for path in result):
        fail("configuration snapshot refuses symlinked appsettings files")
    return result


def copy_config_files(source: Path, destination: Path) -> None:
    files = config_files(source)
    if not files:
        fail("configuration snapshot has no appsettings JSON files")
    destination.mkdir(parents=True, exist_ok=False)
    for path in files:
        shutil.copy2(path, destination / path.name)


def must_not_be_ram_stage(path: Path) -> None:
    resolved = path.resolve()
    forbidden = (Path("/tmp"), Path("/var/tmp"), Path("/dev/shm"), Path("/run"))
    if any(resolved == root or root in resolved.parents for root in forbidden):
        fail(f"release storage must be disk-backed, not under {resolved}")


def run_git(source: Path, *args: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(source), *args],
        check=False,
        capture_output=True,
        text=True,
    )
    if result.returncode:
        detail = result.stderr.strip() or result.stdout.strip()
        fail(f"git {' '.join(args)} failed: {detail}")
    return result.stdout.strip()


def require_clean_approved_source(source: Path, approved_ref: str) -> str:
    if not REF_RE.fullmatch(approved_ref):
        fail("--approved-ref must be a full immutable 40-character lowercase commit id")
    resolved = run_git(source, "rev-parse", "--verify", f"{approved_ref}^{{commit}}")
    if resolved != approved_ref:
        fail("approved ref did not resolve to the exact requested immutable commit")
    head = run_git(source, "rev-parse", "HEAD")
    if head != approved_ref:
        fail("source HEAD does not equal the approved immutable commit")
    if run_git(source, "status", "--porcelain=v1", "--untracked-files=all"):
        fail("source tree is dirty or has untracked files")
    return head


def require_target_a(target: str, unit: str) -> None:
    if target != "a" or unit != "media-llm-proxy.service":
        fail("only explicit Proxy A target media-llm-proxy.service is supported; Proxy B is retired")


def require_files(root: Path, names: tuple[str, ...], label: str) -> None:
    missing = [name for name in names if not (root / name).is_file()]
    if missing:
        fail(f"missing required {label}: {', '.join(missing)}")


def validate_config_json(root: Path, names: tuple[str, ...], label: str) -> None:
    require_files(root, names, label)
    for name in names:
        try:
            with (root / name).open(encoding="utf-8") as stream:
                value = json.load(stream)
        except (OSError, json.JSONDecodeError) as error:
            fail(f"invalid required {label} JSON {name}: {error}")
        if not isinstance(value, dict):
            fail(f"invalid required {label} JSON root {name}: expected object")


def require_evidence(path: Path, label: str) -> dict[str, str]:
    if not path.is_file() or path.stat().st_size == 0:
        fail(f"missing or empty {label}: {path}")
    return {"path": path.name, "sha256": sha256(path)}


def read_manifest(release: Path) -> dict[str, Any]:
    manifest_path = release / "release-manifest.json"
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"invalid release manifest: {error}")
    if manifest.get("schema") != 1 or manifest.get("target") != "a":
        fail("release manifest is not a supported Proxy A release")
    return manifest


def verify_hashes(root: Path, expected: dict[str, str], label: str) -> None:
    actual = hashes(root)
    if actual != expected:
        fail(f"{label} hashes do not match its manifest")


def write_json_atomic(path: Path, value: Any) -> None:
    temporary = path.with_name(f".{path.name}.tmp")
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def timestamp() -> str:
    return dt.datetime.now(dt.UTC).strftime("%Y%m%dT%H%M%SZ")


def prepare(args: argparse.Namespace) -> int:
    source = Path(args.source).resolve()
    publish = Path(args.publish_dir).resolve()
    config = Path(args.config_dir).resolve()
    safe_config = Path(args.safe_config_dir).resolve()
    releases = Path(args.releases_dir).resolve()
    require_target_a(args.target, args.unit)
    must_not_be_ram_stage(releases)
    source_ref = require_clean_approved_source(source, args.approved_ref)
    required_config = config_names(args.environment)
    require_files(publish, REQUIRED_ARTIFACTS, "published artifacts")
    validate_config_json(config, required_config, "target configuration")
    validate_config_json(safe_config, required_config, "safe fallback configuration")

    sdk = require_evidence(Path(args.sdk_runtime_evidence).resolve(), "SDK/runtime evidence")
    tests = require_evidence(Path(args.test_evidence).resolve(), "test evidence")
    releases.mkdir(parents=True, exist_ok=True)
    release_name = f"{timestamp()}-{source_ref[:12]}"
    stage = releases / f".{release_name}.staging"
    final = releases / release_name
    if stage.exists() or final.exists():
        fail(f"release name already exists: {release_name}")

    try:
        (stage / "artifact").parent.mkdir(parents=True)
        copy_tree(publish, stage / "artifact")
        copy_config_files(config, stage / "config")
        copy_config_files(safe_config, stage / "safe-config")
        evidence_dir = stage / "evidence"
        evidence_dir.mkdir()
        shutil.copy2(Path(args.sdk_runtime_evidence), evidence_dir / "sdk-runtime.txt")
        shutil.copy2(Path(args.test_evidence), evidence_dir / "tests.txt")
        manifest = {
            "schema": 1,
            "created_utc": timestamp(),
            "target": "a",
            "unit": args.unit,
            "source": {"approved_ref": source_ref},
            "environment": args.environment,
            "artifacts": hashes(stage / "artifact"),
            "config": hashes(stage / "config"),
            "safe_config": hashes(stage / "safe-config"),
            "compatibility": {"sdk_runtime_evidence": sdk, "test_evidence": tests},
            "promotion": {"method": "atomic-directory-rename", "contract_gate_required": True},
        }
        write_json_atomic(stage / "release-manifest.json", manifest)
        os.replace(stage, final)
    except Exception:
        shutil.rmtree(stage, ignore_errors=True)
        raise
    print(final)
    return 0


def snapshot(path: Path, destination: Path) -> None:
    if path.exists():
        os.replace(path, destination)


def promote(args: argparse.Namespace) -> int:
    release = Path(args.release_dir).resolve()
    live_dir = Path(args.live_dir).resolve()
    config_dir = Path(args.config_dir).resolve()
    require_target_a(args.target, args.unit)
    manifest = read_manifest(release)
    artifact = release / "artifact"
    config = release / "config"
    safe_config = release / "safe-config"
    require_files(artifact, REQUIRED_ARTIFACTS, "release artifacts")
    verify_hashes(artifact, manifest["artifacts"], "artifact")
    verify_hashes(config, manifest["config"], "config")
    verify_hashes(safe_config, manifest["safe_config"], "safe fallback configuration")
    must_not_be_ram_stage(live_dir.parent)

    backups = live_dir.parent / "backups"
    backups.mkdir(parents=True, exist_ok=True)
    backup = backups / f"{timestamp()}-{release.name}"
    backup.mkdir()
    old_live = backup / "artifact"
    old_config = backup / "config"
    fallback_config = backup / "safe-config"
    metadata = {"schema": 1, "release": str(release), "target": "a", "unit": args.unit}
    try:
        pending_artifact = live_dir.parent / f".{live_dir.name}.{release.name}.pending"
        if pending_artifact.exists():
            fail("interrupted promotion residue exists; inspect before retrying")
        # Prepare both directories before moving either live directory. Config
        # and artifact swaps are individually atomic renames. A process sees a
        # coherent full directory at either path. Configuration roots often
        # contain the live bin directory, so only appsettings files are ever
        # snapshotted here. Config activation is deliberately handled by the
        # separately gated restart procedure, not by a recursive directory move.
        copy_tree(artifact, pending_artifact)
        if live_dir.exists():
            snapshot(live_dir, old_live)
        copy_config_files(config_dir, old_config)
        copy_config_files(safe_config, fallback_config)
        write_json_atomic(backup / "rollback.json", metadata)
        # An atomic rename switches all runtime files together. The staged
        # artifact remains immutable by copying it to a sibling first.
        os.replace(pending_artifact, live_dir)
    except Exception:
        pending_artifact = live_dir.parent / f".{live_dir.name}.{release.name}.pending"
        shutil.rmtree(pending_artifact, ignore_errors=True)
        if not live_dir.exists() and old_live.exists():
            os.replace(old_live, live_dir)
        raise
    print(backup)
    return 0


def rollback(args: argparse.Namespace) -> int:
    backup = Path(args.backup_dir).resolve()
    live_dir = Path(args.live_dir).resolve()
    config_dir = Path(args.config_dir).resolve()
    require_target_a(args.target, args.unit)
    try:
        metadata = json.loads((backup / "rollback.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"invalid rollback record: {error}")
    if metadata.get("target") != "a" or metadata.get("unit") != args.unit:
        fail("rollback record is not for explicit Proxy A")
    old_live = backup / "artifact"
    safe_config = backup / "safe-config"
    if not old_live.is_dir() or not safe_config.is_dir():
        fail("rollback record lacks complete prior artifacts or vetted safe configuration")
    recovery = backup / "replaced-during-rollback"
    if recovery.exists():
        fail("rollback recovery directory already exists; inspect before retrying")
    try:
        if live_dir.exists():
            os.replace(live_dir, recovery)
        os.replace(old_live, live_dir)
        for path in config_files(safe_config):
            pending = config_dir / f".{path.name}.rollback-pending"
            shutil.copy2(path, pending)
            os.replace(pending, config_dir / path.name)
    except Exception:
        if not live_dir.exists() and recovery.exists():
            os.replace(recovery, live_dir)
        raise
    print("rollback filesystem restore complete; restart and contract-gate are intentionally external")
    return 0


def parser() -> argparse.ArgumentParser:
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--target", required=True, choices=("a",))
    common.add_argument("--unit", required=True)
    root = argparse.ArgumentParser(description=__doc__)
    commands = root.add_subparsers(required=True)
    prepare_parser = commands.add_parser("prepare", parents=(common,))
    prepare_parser.add_argument("--source", required=True)
    prepare_parser.add_argument("--approved-ref", required=True)
    prepare_parser.add_argument("--publish-dir", required=True)
    prepare_parser.add_argument("--config-dir", required=True)
    prepare_parser.add_argument("--safe-config-dir", required=True)
    prepare_parser.add_argument("--environment", required=True)
    prepare_parser.add_argument("--releases-dir", required=True)
    prepare_parser.add_argument("--sdk-runtime-evidence", required=True)
    prepare_parser.add_argument("--test-evidence", required=True)
    prepare_parser.set_defaults(function=prepare)
    promote_parser = commands.add_parser("promote", parents=(common,))
    promote_parser.add_argument("--release-dir", required=True)
    promote_parser.add_argument("--live-dir", required=True)
    promote_parser.add_argument("--config-dir", required=True)
    promote_parser.set_defaults(function=promote)
    rollback_parser = commands.add_parser("rollback", parents=(common,))
    rollback_parser.add_argument("--backup-dir", required=True)
    rollback_parser.add_argument("--live-dir", required=True)
    rollback_parser.add_argument("--config-dir", required=True)
    rollback_parser.set_defaults(function=rollback)
    return root


def main() -> int:
    try:
        args = parser().parse_args()
        return args.function(args)
    except ReleaseError as error:
        print(f"release control refused: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
