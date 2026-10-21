#!/usr/bin/env bash
# Install the tracked release controls into a separate explicit destination.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
CONTROLS_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
DESTINATION=""
INSTALL=0
while (($#)); do
  case "$1" in
    --destination) DESTINATION="${2:?missing destination}"; shift ;;
    --install) INSTALL=1 ;;
    --help|-h) echo "Usage: install.sh --destination PATH --install"; exit 0 ;;
    *) echo "unsupported argument: $1" >&2; exit 2 ;;
  esac
  shift
done
[[ -n "$DESTINATION" && "$INSTALL" == 1 ]] || { echo "explicit --destination and --install are required" >&2; exit 2; }
[[ "$DESTINATION" != /tmp/* && "$DESTINATION" != /var/tmp/* && "$DESTINATION" != /dev/shm/* ]] || { echo "refusing RAM/temporary installation destination" >&2; exit 2; }
mkdir -p "$(dirname -- "$DESTINATION")"
STAGE="$(mktemp -d "$(dirname -- "$DESTINATION")/.mediallmproxy-controls.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
# Keep the relative controls layout intact: destination/effective_config.py and
# destination/mediallmproxy/*. This copies no deployed app config or binary.
cp -a "$CONTROLS_ROOT/." "$STAGE/"
find "$STAGE" -type d -name '__pycache__' -prune -exec rm -rf {} +
( cd "$STAGE" && find . -type f -print0 | sort -z | xargs -0 sha256sum > MANIFEST.sha256 )
if [[ -e "$DESTINATION" ]]; then
  PREVIOUS="${DESTINATION}.previous.$(date -u +%Y%m%dT%H%M%SZ)"
  [[ ! -e "$PREVIOUS" ]] || { echo "previous-controls backup already exists: $PREVIOUS" >&2; exit 2; }
  mv "$DESTINATION" "$PREVIOUS"
  echo "previous controls retained at $PREVIOUS"
fi
mv "$STAGE" "$DESTINATION"
trap - EXIT
echo "installed tracked controls at $DESTINATION; legacy installed copies were not modified"
