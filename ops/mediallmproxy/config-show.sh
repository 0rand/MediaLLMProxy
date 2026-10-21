#!/usr/bin/env bash
# Tracked source. Install/sync this file and effective_config.py together.
# Reads configuration and active health only. It never starts Proxy B.
set -euo pipefail
ROOT=$( (unset CDPATH; cd -- "$(dirname -- "$0")/.." && pwd) )
exec python3 "$ROOT/effective_config.py" --instance a "$@"
