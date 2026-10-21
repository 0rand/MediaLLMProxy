# Proxy configuration contract

This is the tracked source for the operator guide. It does not change a running
service. Use [README.md](README.md) for the explicit control installation and
release procedure. Do not maintain a second, divergent guide in the deployment
folder.

## Source and runtime state

The source checkout, deployed artifact directory and config content root are
separate paths. A source edit or successful build does not update production.
The active text proxy is `media-llm-proxy.service` (Proxy A). Its existing listener
is port 8000. The model listener is port 8100. Proxy B is retired. A check must
not start it.

The app loads config at startup. The startup content root supplies
`appsettings.json` and the selected `appsettings.<environment>.json`.
Nested keys override individual keys, not complete sections. Applicable
process environment values and command-line arguments override JSON. An
ambiguous content root, environment selector or unsupported override must stop
verification. Do not choose an environment by alphabetic order.

The running app's `/health` values are the authority for resolved options.
Compare them with the required config files and process context. Editing JSON
without restarting the proxy does not apply the edit. The verifier must fail on
that mismatch. A required missing health field is an unverified gate, not a pass.
Do not publish the process environment, config contents or backend credentials.

## Strict ownership and recovery

Keep `RoutingOptions.PrimaryBackend.ToolOutputContract=StrictNative`.
Keep `CureRawToolCalls=false` explicit. StrictNative forces the effective cure
value off even if the requested flag is true. Check requested and effective
values separately. The backend validates executable output. The proxy must not
convert literal examples into calls.

Calls under omitted, null or `auto` tool choice remain optional. A corrective
message must follow the original task. `parallel_tool_calls=false` means at most
one call, not exactly one optional call. `none` forbids executable calls, not
requested inert examples. The backend does not enforce the proxy's former
wire-level `max_calls` banner field. See
[the retry contract](../../docs/TOOL-CALL-RECOVERY.md).

The banner fix does not change retry authorization. The existing strict retry
can override `x-should-retry:false` for an eligible non-streaming typed guard
error. The streaming retry stops at the first ordinary frame, including a
role-only frame. Do not enable broader retry without a separate decision.
A retry-success metric is not proof that the original task succeeded.

## Temporary safe profile

[safe-mode.delta.json](safe-mode.delta.json) is a delta, not a replacement for
all config. Review it before use. It disables the mid-turn wedge and corrective
retry. It leaves the request-side loop guard enabled at its existing value.
Apply both gates together after a successful isolated canary and explicit
restart approval. Never disable retry first while the old wedge is armed.
`MaxRetries=0` is not the same path-selection gate as `Enabled=false`.

This profile trades automatic loop and strict-error recovery for predictable
pass-through. Long reasoning can still reach the current generation limits.
Retain the safe profile as the preferred rollback profile. Restoring a known
broken original profile needs explicit operator approval.

## Sampling, media and audio

The last observed production sampling pins were temperature 0.4 and top_p 1.0.
Do not treat a past observation as current state. Inspect the resolved health
fields. Preserve model aliases, bindings, media gates and sampling during a
proxy-only release unless a separate change approves them.

The last audit found audio detour enabled with no STT listener at port 8085.
Text-model health does not prove that audio works. A cached transcript can hide
this gap. Preserve 502/504 errors for unavailable transcription. Do not forward
raw audio as a substitute without proof of native support. Do not start a local
GPU STT model on the serving pair. Endpoint choice, placement, memory headroom
and test-audio consent remain approval gates.

## Serving safety

Do not restart the model for a proxy change. Do not enter serving ranks with
`docker exec`. Do not stop the display manager. Do not update drivers or the
kernel. Inspect a current rank image from outside its container:

```bash
docker inspect --format '{{.Config.Image}} {{.Image}} {{.State.StartedAt}}' tfv41-r0
```

Use `tfv41-r1` on the worker. Record new release and rollback evidence under the
UTC date. Keep old audit and run records unchanged.
