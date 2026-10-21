#!/usr/bin/env bash
# Explicit, A-only promotion wrapper. It performs nothing unless --apply is supplied.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
CONTROLS_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
REPO_DIR="$(cd -- "$SCRIPT_DIR/../.." && pwd)"
PYTHON="${PYTHON:-python3}"

usage() {
  cat <<'EOF'
Usage: update.sh --stage [inputs]

Only Proxy A (media-llm-proxy.service) is supported. This script never targets,
queries, starts, or restarts retired Proxy B. --stage publishes a verified
disk-backed release only. --apply is deliberately refused in this staged
Package 4B source until an approved full content-root promotion design exists.
EOF
}

MODE=""
APPROVED_REF=""
SOURCE="$REPO_DIR"
PUBLISH_DIR=""
CONFIG_DIR=""
SAFE_CONFIG_DIR=""
RELEASES_DIR=""
SDK_EVIDENCE=""
TEST_EVIDENCE=""
ENVIRONMENT="Production"

while (($#)); do
  case "$1" in
    --stage|--apply) [[ -z "$MODE" ]] || { usage >&2; exit 2; }; MODE="$1" ;;
    --approved-ref|--source|--publish-dir|--config-dir|--safe-config-dir|--releases-dir|--sdk-runtime-evidence|--test-evidence|--environment)
      [[ $# -ge 2 ]] || { echo "missing value for $1" >&2; exit 2; }
      case "$1" in
        --approved-ref) APPROVED_REF="$2" ;; --source) SOURCE="$2" ;; --publish-dir) PUBLISH_DIR="$2" ;;
        --config-dir) CONFIG_DIR="$2" ;; --safe-config-dir) SAFE_CONFIG_DIR="$2" ;; --releases-dir) RELEASES_DIR="$2" ;;
        --sdk-runtime-evidence) SDK_EVIDENCE="$2" ;; --test-evidence) TEST_EVIDENCE="$2" ;;
        --environment) ENVIRONMENT="$2" ;;
      esac
      shift ;;
    --help|-h) usage; exit 0 ;;
    *) echo "unsupported argument: $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

[[ -n "$MODE" && -n "$APPROVED_REF" && -n "$PUBLISH_DIR" && -n "$CONFIG_DIR" && -n "$SAFE_CONFIG_DIR" && -n "$RELEASES_DIR" && -n "$SDK_EVIDENCE" && -n "$TEST_EVIDENCE" ]] || { usage >&2; exit 2; }

# This is a fixed, tracked verifier, not an arbitrary caller-supplied hook.
CONFIG_VERIFIER="$CONTROLS_ROOT/effective_config.py"
[[ -f "$CONFIG_VERIFIER" ]] || { echo "required effective-config verifier is not installed" >&2; exit 2; }
"$PYTHON" "$CONFIG_VERIFIER" --offline --instance a --config-dir "$CONFIG_DIR" --environment "$ENVIRONMENT" --require-safe-mode
"$PYTHON" "$CONFIG_VERIFIER" --offline --instance a --config-dir "$SAFE_CONFIG_DIR" --environment "$ENVIRONMENT" --require-safe-mode

RELEASE="$($PYTHON "$SCRIPT_DIR/release.py" prepare --target a --unit media-llm-proxy.service --source "$SOURCE" --approved-ref "$APPROVED_REF" --publish-dir "$PUBLISH_DIR" --config-dir "$CONFIG_DIR" --safe-config-dir "$SAFE_CONFIG_DIR" --environment "$ENVIRONMENT" --releases-dir "$RELEASES_DIR" --sdk-runtime-evidence "$SDK_EVIDENCE" --test-evidence "$TEST_EVIDENCE")"
echo "staged immutable release: $RELEASE"

if [[ "$MODE" == "--stage" ]]; then
  echo "stage-only: no files were promoted and no service was contacted"
  exit 0
fi

# Package 4B is intentionally staged only. The deployment root commonly
# combines config, logs, and the live bin directory. An approved content-root
# switch design is required before this wrapper can safely activate candidate
# config alongside artifacts. Refuse rather than pretending a restart is safe.
echo "--apply is not enabled in this staged Package 4B source; no production change was made" >&2
exit 2
