# Proxy operator instructions

Use [CONFIG.md](CONFIG.md) for config precedence and effective safety gates.
Use [README.md](README.md) for the tracked controls and release procedure.
These files replace the old environment-only and both-proxy instructions only
after an explicit operator-approved install.

## Before a change

1. Identify the source checkout, immutable commit and deployed artifact hashes.
2. Confirm Proxy A is active and Proxy B is retired.
3. Record the startup content root and selected environment without publishing
   secrets. Do not assume that JSON next to the DLL is active.
4. Run the offline unit, config, contract and release-control tests.
5. Build serially into a disk-backed staging folder. Do not publish into the
   active artifact folder.
6. Test the staged DLL on a loopback-only port. Use the existing model at port
   8100 for bounded sequential inference. Do not start another GPU model.
7. Inspect complete SSE events and exactly one complete DONE event. Check
   native calls, literal examples, typed errors and incomplete transports.
8. Confirm the real client and original agent scenario with a recording or stub
   executor. Do not execute generated calls in the acceptance suite.

## Production approval

Staging is not approval to promote. Obtain approval for the exact immutable
source ref, target unit, restart window and rollback artifact/config pair.
Obtain a separate decision before enabling corrective recovery. A fixed banner
does not prove that recovery fulfilled the original task.

The preferred rollback config disables both the wedge and corrective retry.
Check that profile with the prior artifact before promotion. Do not restore a
known broken profile by default. A Proxy A restart can interrupt clients. Do
not claim zero downtime.

After an approved promotion, run the production contract gate. Health alone is
not acceptance. Verify that serving-rank identities did not change. On failure,
restore the complete prior artifact set and vetted safe config. Restart only
Proxy A and repeat the same gate. Never stop the model as part of this rollback.

## Audio

Do not infer STT availability from healthy text inference. The intended STT
endpoint and approved placement must be established first. Obtain consent before
processing test audio. Check uncached and cached requests, timeout, backend
failure, non-speech and URL policy. Keep 502/504 failure behavior until the
independent audio change passes. Do not use a new local GPU model to fill a
missing port on the serving pair.

Keep all prior audit and run records unchanged. Add a new dated record for each
canary, cutover and rollback. Do not publish keys, raw process environments or
user-specific paths.
