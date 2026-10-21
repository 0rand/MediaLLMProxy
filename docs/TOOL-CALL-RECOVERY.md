# Tool-Call Recovery

MediaLLMProxy supports two tool-output contracts:

- `ToolOutputContract=StrictNative` is for a backend that validates tool output
  itself. The backend emits native `tool_calls` and returns a typed terminal
  error for rejected output. The proxy does not scan or rewrite answer content.
- `ToolOutputContract=LegacyRaw` keeps the compatibility path that converts
  supported raw DSML/XML assistant content into standard OpenAI `tool_calls`.

Both contracts can retry a rejected model response once with a corrective user
message. The strict path retries only the backend's typed guard rejection. The
legacy path also covers proxy raw-tool validation failures when curing is
enabled.

The strict contract is the correct choice for the local DeepSeek TensorFold
stack at `:8100`. The legacy raw-cure path is for older backends that do not
produce native calls.

## Strict-native contract

Set `RoutingOptions.PrimaryBackend.ToolOutputContract` to `StrictNative`:

```json
{
  "RoutingOptions": {
    "PrimaryBackend": {
      "BaseUrl": "http://127.0.0.1:8100",
      "ToolOutputContract": "StrictNative",
      "CureRawToolCalls": false
    }
  },
  "UnavailableToolRetryOptions": {
    "Enabled": true,
    "MaxRetries": 1,
    "IncludeRequestContext": true,
    "MaxResponseBytes": 16777216
  }
}
```

In strict-native mode:

- `CureRawToolCalls` is ignored. A startup warning records the conflict.
  The proxy never calls `SseToolCallCure` or `RawToolCallCure`.
- Native `tool_calls`, prose and reasoning pass through unchanged. The proxy
  does not buffer answer content for validation.
- A retry is allowed only for a terminal `model_output_error` with
  `code=unavailable_tool_call` and
  `source=tensorfold.tool_output_guard`. Other errors pass through.
- The retry appends one `role: user` message. It preserves the original task,
  offered schemas, thinking settings and sampling. Its policy guidance is
  normalized from `tool_choice` and `parallel_tool_calls`: omitted, `null` and
  `auto` keep calls optional; `none` forbids executable calls but preserves
  requested inert examples as text; `required` asks for at least one offered,
  schema-valid call; and a named choice requires that exact offered function.
  `parallel_tool_calls:false` means at most one call, not exactly one under
  `auto`.
- The pinned backend ignores a wire-level `max_calls` field. The proxy does not
  describe or enforce it in retry guidance. `tool_choice:"none"` is the
  supported no-call policy. A negotiated call-limit API would require separate
  request validation and integration coverage.
- Invalid or incompatible tool policy is left to the backend. The proxy does
  not retry or rewrite its error into a different policy.
- The message never includes rejected arguments, rejected prose or raw model
  output. The backend's payload-free `rejection_id` is logged, not sent to the
  model.
- Tools, `tool_choice`, thinking settings and sampling are preserved. The
  marker message is sent upstream only and is not persisted client-side.

Streaming limit: the proxy inspects only the initial SSE preamble. It retries
when the guard error arrives before any client-visible frame. The strict stack
normally rejects at the end of generation, after reasoning or prose has
streamed. The proxy then forwards the error and does not replay. Use
non-streaming tool turns when a corrective retry is required.

## Configuration

The legacy raw-cure example is below. It is not for a strict-native backend.

```json
{
  "RoutingOptions": {
    "PrimaryBackend": {
      "CureRawToolCalls": true
    }
  },
  "UnavailableToolRetryOptions": {
    "Enabled": true,
    "MaxRetries": 1,
    "RetryRawToolCallAborts": true,
    "MaxResponseBytes": 16777216
  }
}
```

Equivalent environment variables:

```bash
export RoutingOptions__PrimaryBackend__CureRawToolCalls=true
export UnavailableToolRetryOptions__Enabled=true
export UnavailableToolRetryOptions__MaxRetries=1
export UnavailableToolRetryOptions__RetryRawToolCallAborts=true
export UnavailableToolRetryOptions__MaxResponseBytes=16777216
```

The JSON files are resolved from the process working directory. A per-environment
file overrides the base file; environment variables override both. Options are
read at startup. Configuration changes require a restart, and new functionality
requires deploying an updated binary before restarting. See
[`QUICKSTART.md`](../QUICKSTART.md) for configuration layering.

## Recovery Contract

In strict-native mode, recovery is allowed only for a terminal
`model_output_error` with `code=unavailable_tool_call`,
`source=tensorfold.tool_output_guard` and a model-correctable reason. The proxy
re-issues the request once. Transport failures, authentication failures,
unrelated 5xx responses, cancellation and `stream_prefix_changed` are not
retried.

In legacy mode, the stack's `model_output_error` / `unavailable_tool_call` error
also triggers recovery for HTTP JSON and for an SSE error frame received before
any SSE frame is forwarded. With curing enabled, non-streaming proxy raw-tool
validation failures also trigger recovery when `RetryRawToolCallAborts` is true.

The retry starts from the final forwarded request after sampling, media rewrites
and request-side LoopGuard evaluation. It appends exactly one new `role: user`
message. Failed assistant output and generated arguments are not included.

In strict-native mode, `StrictGuardBannerIntro` opens the message. The default
intro is neutral, but a custom intro is operator-supplied text and can conflict
with normalized policy guidance. The proxy appends a category-specific sentence
when the backend supplies a safe `category`, then the normalized tool policy.
Set `IncludeRequestContext=false` to omit the offered-function context, while
the policy-safe corrective guidance remains. The message never contains
rejected arguments or output.

In legacy mode, `InjectionMarker` prefixes the corrective message. `Banner`
controls the message for an unavailable tool; `RawToolCallBanner` controls the
message for a raw-tool rejection.

Thinking mode, sampling, tool definitions and `tool_choice` are preserved. The
proxy neither executes calls nor invents arguments or tool names. The banner is
not emitted to the client or added to its stored conversation history.

Both rejection types share one retry budget. A change of error type on the
second attempt does not reset the budget. `MaxRetries` defaults to 1 and is capped
at 1; setting it to 0 disables re-issues while keeping buffered validation active.
The guard does not retry requests without a non-empty `tools` array, unrelated
backend errors, network failures or authentication failures. Client cancellation
is propagated rather than starting another attempt.

The current retry authorization is unchanged by this guidance. For eligible
non-streaming strict guard errors, the configured single retry still overrides a
backend `x-should-retry:false` response hint. That behavior requires a separate,
explicit policy decision before changing it. Streaming remains conservative: the
proxy retries only an initial preamble guard error before any client-visible
frame. It does not broaden recovery past role-only, reasoning, content, or other
visible frames.

## Final Errors

If the retry is rejected by the stack again, its error passes through. A second
proxy raw-tool rejection produces:

```json
{
  "error": {
    "type": "model_output_error",
    "code": "invalid_raw_tool_call",
    "source": "mediallmproxy.raw_tool_guard",
    "reason": "invalid_raw_call",
    "message": "The model response contained an invalid raw tool call (reason: invalid_raw_call)."
  }
}
```

Non-streaming raw-tool rejection uses HTTP 502. Streaming raw-tool rejection uses
HTTP 200 with one SSE error frame and `data: [DONE]`, not a successful terminal
chunk or a connection reset. No rejected call arguments are returned.

The proxy's payload-free `reason` is one of `buffer_limit`,
`mixed_native_raw_calls`, `incomplete_raw_call`, `invalid_raw_call`, or
`non_tool_finish`. It is also repeated in the human message for clients that
retain only that field.

With recovery disabled, curing still returns the structured raw-tool error; the
retry feature only controls whether an eligible non-streaming response is reissued.

## Streaming Tradeoffs

In strict-native mode the proxy does not buffer answer content. It reads only
the initial SSE preamble to decide whether a retry is safe. If the first
client-visible frame arrives before a guard error, the proxy forwards the
stream unchanged and does not replay it. This prevents duplicate reasoning or
prose. A guard error that arrives before any visible frame, including after
comments or heartbeats, can be retried. `MaxResponseBytes` caps only
non-streaming attempts.

In legacy mode, non-streaming eligible attempts are fully buffered and
validated. Streaming attempts inspect only their first SSE line: an upfront
unavailable-tool error may be retried, but after a comment, heartbeat, reasoning,
`reasoning_content`, or other SSE frame is forwarded the proxy never replays the
request. This prevents duplicate or divergent visible reasoning.
`MaxResponseBytes` caps only buffered non-streaming attempts.

The legacy streaming raw-call filter forwards SSE comments, heartbeats, errors,
`reasoning`, and `reasoning_content` immediately. It buffers answer `content` per
choice until its terminal event so raw calls remain atomic and literal context can
span arbitrary chunks. A malformed actual raw call after visible output ends with
one `invalid_raw_tool_call` SSE error frame and `[DONE]`; it is not retried.
Because the proxy does not retain a live stream to completion, a streaming retry
is not finalized as a retry-success metric after its first forwarded frame.
Request-side LoopGuard still runs, but the mid-turn wedge is bypassed for recovery
requests. Media observations are appended to successful responses only.

Raw-markup recognition in legacy mode remains a compatibility heuristic, not a
security boundary or complete tool-argument schema validation. Recovery helps
the model correct the response; it does not replace authoritative tool validation
in the serving stack or client. The strict-native contract exists because a
second parser can disagree with the backend's literal and envelope rules.

## Health And Logs

Check the actual proxy endpoint, not the backend's port:

```bash
curl -fsS http://127.0.0.1:7071/health | python3 -m json.tool
```

For a deployment listening on port 8000, use that port instead. Expected fields
are in two separate places:

```json
{
  "primary": {
    "toolOutputContract": "StrictNative",
    "cureRawToolCalls": false
  },
  "unavailableToolRetry": {
    "enabled": true,
    "maxRetries": 1,
    "retryRawToolCallAborts": false,
    "strictGuardOnly": true,
    "includeRequestContext": true,
    "maxResponseBytes": 16777216
  }
}
```

`unavailableToolRetry` is a **top-level** object, not a field inside `primary` or
`loopGuard`. Its `metrics` object contains `unavailable_tool_retries`,
`unavailable_tool_retry_success`, `unavailable_tool_retry_fail`,
`tool_retry_skipped_visible`, `tool_retry_skipped_not_guard` and
`tool_retry_fail_by_category`. These counters reset when the process restarts.
A zero retry count means no corrective re-issue has happened since restart, not
that the feature is disabled.

Startup logs include `UnavailableToolRetry: enabled=True maxRetries=1 rawToolAborts=False strictGuardOnly=True`.
Strict retries are logged as
`TOOL_RETRY reason=strict_guard_rejection category=... rejection_id=...`.
Legacy retries are logged as `TOOL_RETRY reason=unavailable_tool_call` or
`TOOL_RETRY reason=invalid_raw_tool_call`. Rejected arguments are not logged.
`X-PreRouter-Tool-Contract` reports `strict-native` or `legacy-raw`.
`X-PreRouter-Tool-Retries` reports re-issues for eligible requests.
`X-PreRouter-Tool-Rejected` identifies a final rejection.

If the health object is missing, verify the deployed build and endpoint first.
Adding JSON settings to an older binary does not add the feature. On a systemd
deployment, check `ExecStart`, `WorkingDirectory`, `MainPID` and the start time:

```bash
systemctl show media-llm-proxy.service \
  -p ExecStart -p WorkingDirectory -p MainPID -p ExecMainStartTimestamp
journalctl -u media-llm-proxy.service -n 50 --no-pager
```

Compare the deployed DLL hash with the published artifact if necessary. If the
object exists but `enabled` is false, inspect configuration precedence, selected
environment and whether the process restarted after the edit.

## Verification

```bash
dotnet test OAIPreRouter.Tester/OAIPreRouter.Tester.csproj -c Release
```

`UnavailableToolRetryTests` covers error detection and banner construction.
`ToolRetryRecoveryTests` covers JSON/SSE recovery, cross-error retry budgets,
repeated rejections, preserved tool definitions/thinking settings, cancellation,
response limits and failed-attempt isolation.

Model-backed checks against the DeepSeek stack also verified recovery from each
rejection type in JSON and SSE mode using isolated non-production proxy ports.
They inspected generated calls without executing them.
