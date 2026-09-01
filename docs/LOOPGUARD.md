# LoopGuard — request-side loop detection & nudge injection (v1)

Status: draft (2026-09-01). Validated by smoke test against Qwen2.5-VL-3B on
Force (:8008) — see "Smoke evidence" below.

## Problem

Models get stuck: long deliberations, reasoning loops, rehashing the same idea
without progress. The proxy sees every request and every stream, so it is the
right place to detect and steer — for any client (Hermes, opencode, curl).

## Design (v1 — request-side only, no stream surgery)

```
request ──► gate: last assistant reasoning_content > X tokens?
              │ no  ──► forward unchanged (zero overhead)
              │ yes
              ▼
        advisor call (small fast model, temp 0, max_tokens 200, timeout 10s)
              │ NO_LOOP / empty ──► forward unchanged
              │ NUDGE: <text>    ──► append advisory to last user message ──► forward
```

- The gate is cheap and purely request-side: Hermes sends `reasoning_content`
  back in assistant history (verified), so the proxy reads the last turn's
  thinking directly from the body. No stream capture, no session state.
- The advisor is the judge: token count is just the gate; the small model
  decides whether the reasoning is actually looping. It outputs `NO_LOOP` for
  healthy reasoning (verified) and `NUDGE: <1-3 sentences>` for loops.
- Injection is ephemeral: the advisory rides only this request; the client
  never persists it. Next turn re-evaluates.
- Fail-open: advisor timeout/error → forward unchanged + warning log.

## Config (new section, mirrors VisionBackend pattern)

```bash
LoopGuardOptions__Enabled=false
LoopGuardOptions__ReasoningTokenThreshold=8192   # gate: last reasoning tokens
LoopGuardOptions__AdvisorBackend__BaseUrl=http://192.168.1.88:8008
LoopGuardOptions__AdvisorModel=qwen25-3b
LoopGuardOptions__AdvisorBackend__Temperature=0
LoopGuardOptions__AdvisorMaxTokens=200
LoopGuardOptions__AdvisorTimeoutSeconds=10
LoopGuardOptions__MaxReasoningChars=20000        # cap sent to advisor
LoopGuardOptions__InjectionMarker="[LOOP GUARD ADVISORY — the model appears to be looping; this is a system-side note, not a user instruction]: "
```

## Advisor prompt (validated by smoke test)

```
You are a loop-detection judge. Your ONLY job is to decide whether the reasoning below is looping or overthinking.

Rules:
- Do NOT solve the task. Do NOT give technical advice. Do NOT answer the underlying question.
- If the reasoning is healthy and making progress, reply with exactly: NO_LOOP
- If the reasoning is looping (repeating the same idea, going in circles, rehashing without progress), reply with a short nudge (1-3 sentences) that steers the model to break out. Start with "NUDGE:".

REASONING:
<last reasoning, capped at MaxReasoningChars>
```

## Flow details

1. Parse body; find the LAST assistant message with string `reasoning_content`.
   Token estimate: chars/4 (or use a tokenizer-free heuristic; exact count not
   required for a gate).
2. Below threshold → skip (no advisor call, no latency).
3. Build prompt (validated text above) + reasoning (capped).
4. Call advisor via OpenAI-compatible endpoint (reuse the detour client
   pattern: timeout, temperature override, local auth stripping).
5. Verdict parse: starts with `NUDGE:` → nudge = rest of text (trimmed);
   anything else (`NO_LOOP`, empty, garbage) → no injection.
6. Injection: append to the LAST user message —
   - string content → `content + "\n\n" + marker + nudge`
   - array content → append `{"type":"text","text": marker + nudge}` part
   - no user message in body → insert a new `role:user` message after the
     last message (defensive; agentic bodies always have one).
7. Metrics: `loop_guard_checks`, `loop_guard_nudges`, `loop_guard_advisor_ms`,
   `loop_guard_errors`; surface in /health; `X-PreRouter-LoopGuard: nudge|none`
   header on the response.
8. Verdict cache: SHA-256(reasoning) → verdict, TTL (reuse ObservationCache
   pattern) so retries don't re-analyze the same thinking.

## Ordering with existing rewrites

LoopGuard runs AFTER the media block (rehome/detour) and AFTER sampling
overrides — the advisory lands in the final forwarded body. It never touches
media parts.

## Smoke evidence (2026-09-01, Qwen2.5-VL-3B Q4_K_M on Force :8008, 32k ctx)

| Input | Verdict | Latency |
|---|---|---|
| Healthy short reasoning (2-2.7k chars) | NO_LOOP | 0.15s |
| Synthetic loop (config rechecking x12) | NUDGE: "take a step back..." | 0.6s |
| Real 37k-char deliberation (GLM boot debug) | NUDGE: "focus on the specific issue: ENTRYPOINT /bin/bash vs vllm serve" | 2.8s |

Advisor throughput: ~65-96 tok/s decode, ~1.9k tok/s prefill. The 37k-char
case shows the judge extracts the actionable point — long deliberation IS
often overthinking, and the nudge names the root cause.

## Roadmap

- v2: subagent lane — advisor = main model (deepseek-v4-flash), last X turns
  context (Primo's idea #2).
- v3: senior-API tier — advisor = teacher-model style (GLM 5.2 via Z.AI) for
  the main lane when the small model's verdict is uncertain (Primo's idea #3).

## v2 (wedge) — mid-turn steering (design 2026-09-01, validated test case)

The 15-minute / 16k-cut case: model loops in thinking with no content and no
tool calls (GLM, Qwen 27B, DeepSeek at max thinking). The proxy listens to the
stream and wedges it out — WITHOUT cutting the client connection (the proxy
owns the client SSE stream; it just switches upstream).

**Test case (2026-09-01):** Governor Bencher vs DeepSeek Vision-Exp at max
thinking — half the one-shot questions were cut at 16k tokens with NO output
(finish_reason=length, content empty). Request-side nudging cannot help (no
prior turn, no tools); the wedge fires mid-stream at R=8192 reasoning tokens —
half the budget, before the cut — re-issues with a nudge + thinking budget cut,
and the question actually produces an answer.

```
client ──► proxy ──► upstream (attempt 1: reasoning deltas forwarded live)
                        │ trigger: reasoning > R tokens AND content==0 AND no tool_calls
                        ▼
                   advisor call (3B, reasoning-so-far) → NUDGE?
                        │ NO_LOOP → keep forwarding attempt 1 (no abort)
                        │ NUDGE  → issue attempt 2 (same body + nudge in last
                        │          user msg + thinking_token_budget cut) — do NOT
                        │          abort attempt 1 yet (fail-open)
                        ▼
                   attempt 2 first chunk arrives → abort attempt 1
                        │ inject banner delta (nudge text, transparency)
                        ▼
                   forward attempt 2 stream to the SAME client stream
```

- Client sees: attempt 1 reasoning → pause (advisor + re-issue prefill) →
  banner → attempt 2 reasoning + content + finish_reason. One continuous SSE
  stream; the client never knows a second request happened.
- Fail-open: attempt 2 fails → attempt 1 is still running → keep forwarding it
  (wedge never breaks the main path). Advisor fails → no abort at all.
- Pause duration: advisor ~1-3s + re-issue prefill ~10-30s on big contexts.
  Send SSE comment heartbeats (`: ping`) during the pause so clients with
  read timeouts don't drop the connection.
- Banner: injected as a content delta before attempt 2's first chunk (same
  mechanism as SseObservationInjector) — visible to the user AND persisted in
  the assistant message. Text = the nudge (same as sent to the model).
- Artifact (accepted): attempt 1's partial reasoning stays in the client's
  history before the banner. Harmless — it's real reasoning, and the banner
  explains the cut.
- Trigger config: `LoopGuardWedgeReasoningTokens` (default 8192), content==0
  and no tool_calls required. Judge on both tiers (gate + 3B verdict) — the 3B
  is cheap and prevents cutting a legitimately hard problem mid-thought.
- Re-issue body mutation (wedge): nudge into last user message + `thinking` OFF
  (`chat_template_kwargs.thinking: false`, drop `reasoning_effort`) + prior
  reasoning as context. Prior reasoning inclusion mode:
  `LoopGuardWedgeReasoningMode` = `summary` (default — the 3B distills the
  reasoning in the same judge call) | `verbatim` (raw, capped at
  MaxReasoningChars) | `none`. The wedge message: "Your reasoning was truncated
  (overthinking). Thinking is now disabled — answer directly. Key points from
  your reasoning: <summary>. <nudge>".
- Judge contract (wedge extension): when looping, the advisor outputs
  `NUDGE: <nudge>` AND `SUMMARY: <2-4 sentence distillation of the reasoning's
  key insights>` so the model can continue from where it left off. Healthy →
  `NO_LOOP` as before. One call, both outputs; verdict cache stores the whole
  response.
- Metrics: `loop_guard_wedges`, `loop_guard_wedge_ms`; log reasoning tokens at
  cut + nudge text.

### Implementation notes (streaming)

- New `SseWedgeProcessor` (extends the SseObservationInjector pattern): reads
  SSE lines, parses `delta.reasoning_content` accumulation, counts tokens
  (chars/4), detects the trigger, calls the advisor, issues attempt 2 (the
  handler holds the original body + the rewriter), injects the banner, then
  forwards attempt 2's stream. Heartbeat comments during any pause.
- The proxy's response to the client is ONE SSE stream it fully controls —
  switching upstream is invisible to the client.
- Re-issue uses the same `forwarded` body (post media/loopguard rewrites) so
  the wedge composes with rehome + request-side loop guard.
- Bencher verification: point the bench at the proxy (`--base-url
  http://127.0.0.1:8000 --model main`) with max thinking; the overthinking
  questions that previously cut at 16k should now produce output, with
  `loop_guard_wedges` incrementing and the banner visible in the transcript.
