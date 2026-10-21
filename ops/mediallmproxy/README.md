# MediaLLMProxy release controls

These are **tracked source controls** for release staging and an explicitly
approved Proxy A cutover. They do not edit legacy installed copies. Use
`install.sh --destination <disk-path> --install` only when an operator has
approved synchronizing this source to a separate controls directory.

## Supported now

- **Only Proxy A**: target and unit are hard-coded to `a` and
  `media-llm-proxy.service`. Proxy B is retired and cannot be awakened.
- A full 40-character immutable Git commit must equal a clean source checkout.
  Branch names, pulls, merges, and untracked source are refused.
- A full already-published artifact set is copied to a disk-backed immutable
  release directory with artifact, candidate-config, safe-config, SDK/runtime,
  and test-evidence hashes in `release-manifest.json`.
- `--stage` checks the tracked effective-config verifier before publication and
  has no service or live-directory action.
- **This staged source supports `--stage` only.** `--apply` intentionally
  refuses before touching a live directory or service. Package 4B does provide
  tested artifact promotion/rollback primitives, complete config-file snapshots,
  fixed canary and contract-gate wiring, but must not claim a safe live config
  switch until an approved content-root layout is available. `/health` alone is
  never accepted as a release result.

## Required configuration policy

The effective configuration must be validated from the candidate content root,
not from a copied `bin/appsettings.json`. JSON precedence is evaluated by the
tracked `ops/effective_config.py` verifier. Both candidate and fallback are
required to use safe mode: `StrictNative`, effective raw-tool curing off,
`LoopGuardOptions.WedgeEnabled=false`, and
`UnavailableToolRetryOptions.Enabled=false`.

The current default application settings show audio/STT configured toward a
loopback endpoint, but **STT availability is pending Package 5**. A successful
text release must not claim that STT is healthy or start an STT service.

## Explicit operator sequence

1. Produce a publish directory and independent, sanitized SDK/runtime and test
   evidence records. No credential, home path, or public address belongs in a
   manifest or record.
2. Run `update.sh --stage` with the exact approved ref, target config, vetted
   fallback config, and disk-backed releases directory. Review the manifest.
3. Obtain approvals for ref, target unit, restart window, rollback pair, and
   contract-gate target. Then, and only then, use `--apply`.
4. Save canary, promotion, and rollback records beneath the approved UTC run
   index outside this source tree. Never revise the historical audit records.

## Pending integration points

The tracked effective-config verifier, isolated canary runner, strict contract
gate, fixed contract fixtures, and restart guard are installed together for
review and offline evidence. Activating them into a production orchestration is
**pending** an approved full content-root promotion design. This is intentional:
release publication must fail closed rather than substitute a health check or
execute caller-provided hooks. The contract runner must be supplied with a
Proxy A endpoint and must never execute model-returned tools.
