# Tool-Call Recovery

MediaLLMProxy has two complementary tool-output features:

- `RoutingOptions.PrimaryBackend.CureRawToolCalls` converts supported raw DSML/XML
  assistant content into standard OpenAI `tool_calls`.
- `UnavailableToolRetryOptions.Enabled` retries a rejected model response with a
  corrective user message, once per incoming request.

Both are disabled in the checked-in defaults. The opt-in example is
[`examples/appsettings.tool-recovery.json`](../examples/appsettings.tool-recovery.json).
It uses loopback port 7071; it does not bind production port 8000.

## Configuration

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

The stack's `model_output_error` / `unavailable_tool_call` error triggers recovery
in HTTP JSON or an SSE error frame. With curing enabled, proxy raw-tool validation
failures also trigger recovery when `RetryRawToolCallAborts` is true. These include
malformed/incomplete markup and raw calls that name an undeclared tool.

The retry starts from the final forwarded request after sampling, media rewrites
and request-side LoopGuard evaluation. It appends exactly one new `role: user`
message. Failed assistant output and generated arguments are not included.

`InjectionMarker` prefixes the corrective message. `Banner` controls the message
for an unavailable tool; `RawToolCallBanner` controls the message for a raw-tool
rejection. The defaults ask the model to use only offered tools and, for raw-tool
failures, emit complete valid calls without repeating rejected protocol markup.

Thinking mode, sampling, tool definitions and `tool_choice` are preserved. The
proxy neither executes calls nor invents arguments or tool names. The banner is
not emitted to the client or added to its stored conversation history.

Both rejection types share one retry budget. A change of error type on the
second attempt does not reset the budget. `MaxRetries` defaults to 1 and is capped
at 1; setting it to 0 disables re-issues while keeping buffered validation active.
The guard does not retry requests without a non-empty `tools` array, unrelated
backend errors, network failures or authentication failures. Client cancellation
is propagated rather than starting another attempt.

## Final Errors

If the retry is rejected by the stack again, its error passes through. A second
proxy raw-tool rejection produces:

```json
{
  "error": {
    "type": "model_output_error",
    "code": "invalid_raw_tool_call",
    "message": "The model response contained an invalid or incomplete raw tool call."
  }
}
```

Non-streaming raw-tool rejection uses HTTP 502. Streaming raw-tool rejection uses
HTTP 200 with one SSE error frame and `data: [DONE]`, not a successful terminal
chunk or a connection reset. No rejected call arguments are returned.

With recovery disabled, the existing unbuffered cure-only path can still abort
the connection on rejected raw markup. Enable both features to use corrective
retry and structured final errors instead.

## Streaming Tradeoffs

Each eligible attempt is fully buffered and validated before reaching the client.
This prevents reasoning or executable calls from a rejected attempt from leaking
into the client conversation. `MaxResponseBytes` caps the buffer at 16 MiB per
attempt by default; exceeding the cap fails without a model retry.

The first response bytes are delayed until validation. Allow client timeouts to
cover up to two generations, including prefill. Responses without tools retain
their normal streaming path. Request-side LoopGuard still runs, but the mid-turn
wedge is bypassed for buffered recovery requests to avoid an extra unvalidated
generation. Media observations are appended to successful responses only.

Raw-markup recognition remains a compatibility heuristic, not a security boundary
or complete tool-argument schema validation. Literal protocol examples may still
be treated as attempted calls. Recovery helps the model correct the response; it
does not replace authoritative tool validation in the serving stack or client.

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
    "cureRawToolCalls": true
  },
  "unavailableToolRetry": {
    "enabled": true,
    "maxRetries": 1,
    "retryRawToolCallAborts": true,
    "maxResponseBytes": 16777216
  }
}
```

`unavailableToolRetry` is a **top-level** object, not a field inside `primary` or
`loopGuard`. Its `metrics` object contains `unavailable_tool_retries`,
`unavailable_tool_retry_success` and `unavailable_tool_retry_fail`. These counters
cover both rejection types and reset when the process restarts. A zero retry
count means no corrective re-issue has happened since restart, not that the
feature is disabled.

Startup logs include `UnavailableToolRetry: enabled=True maxRetries=1 rawToolAborts=True`.
Actual retries are logged as `TOOL_RETRY reason=unavailable_tool_call` or
`TOOL_RETRY reason=invalid_raw_tool_call`, without rejected arguments.
`X-PreRouter-Tool-Retries` reports re-issues for eligible requests;
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
