#!/usr/bin/env bash
# Tracked source. Proxy A only. It validates before a requested restart and
# independently verifies active health after it. It never starts retired Proxy B.
set -euo pipefail
ROOT=$( (unset CDPATH; cd -- "$(dirname -- "$0")/.." && pwd) )
VERIFY=(python3 "$ROOT/effective_config.py" --instance a --require-safe-mode)

usage() {
  cat <<'USAGE'
Usage: restart.sh [--apply]

Without --apply this performs only the fail-before offline verification and exits.
--apply restarts only media-llm-proxy after that verification, then checks live
/health. Target units, config roots and environment overrides are not accepted:
the script must verify the real Proxy A process context. Proxy B is retired and
is never started.
USAGE
}

apply=false
for arg in "$@"; do
  case "$arg" in
    --apply) apply=true ;;
    --help|-h) usage; exit 0 ;;
    *) echo "unsupported argument: $arg" >&2; usage >&2; exit 2 ;;
  esac
done

# Offline first detects malformed/missing configs, ambiguity, unsafe explicit gates,
# and unsupported relevant overrides before systemctl is touched.
"${VERIFY[@]}" --offline
if [ "$apply" != true ]; then
  echo "NOT RESTARTED: pass --apply after reviewing the verified configuration."
  exit 0
fi

sudo systemctl restart media-llm-proxy
"${VERIFY[@]}"
echo "RESTART VERIFIED: Proxy A only."
