# LoopGuard v1 Implementation Plan

> **Status:** STAGE 2 (LLM judge + static fallback) IMPLEMENTED + VERIFIED 2026-09-01 —
> 178/178 tests; live e2e: real Qwen 3B judge nudge in forwarded body (3.0s), dead-judge
> static fallback confirmed. Stage 3 (wedge) + subagent/senior tiers pending.

> **For Hermes:** Implement task-by-task with TDD; commit after each task.

**Goal:** Request-side loop detection + nudge injection in MediaLLMProxy — when the last turn's reasoning exceeds a token threshold, a small advisor model (Qwen 2.5 3B on Force :8008) judges whether the reasoning is looping; if so, a nudge is appended to the last user message before forwarding. NO_LOOP / advisor failure = request passes through untouched.

**Architecture:** New `LoopGuardOptions` config section + `LoopGuardService` (gate → advisor call → verdict → injection), wired into `HandleChatCompletion` after the media block. Reuses the existing detour-client pattern (IHttpClientFactory, timeout, temperature override, local auth stripping) and the ObservationCache for verdict caching. Pure request-side — no stream changes.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal API, System.Text.Json, xUnit. Advisor endpoint: OpenAI-compatible llama.cpp server (Qwen2.5-VL-3B Q4_K_M, Force :8008, 32k ctx, ~90 t/s).

**Design doc:** `docs/LOOPGUARD.md` (validated advisor prompt + smoke evidence).

---

## Context / Assumptions

- Hermes sends `reasoning_content` back in assistant history (verified in hermes-agent source: `msg["reasoning_content"] = ...`), so the proxy reads the last turn's thinking directly from the request body.
- Advisor prompt is validated by smoke test (2026-09-01): healthy reasoning → `NO_LOOP`; loops → `NUDGE: <text>`; the 3B extracts the actionable point.
- Token estimate: `chars / 4` — a gate, not an exact count. Exactness not required.
- Fail-open: any advisor error/timeout → forward unchanged.
- Out of scope (later versions): mid-stream wedge, subagent lane, senior-API tier.

## Files

- Create: `OAIPreRouter.Cli/Models/LoopGuardOptions.cs`
- Create: `OAIPreRouter.Cli/Services/LoopGuardService.cs`
- Create: `OAIPreRouter.Tester/LoopGuardTests.cs`
- Modify: `OAIPreRouter.Cli/Services/JsonBodyRewriter.cs` (add `TryInjectLoopGuardNudge`)
- Modify: `OAIPreRouter.Cli/Services/BridgeMetrics.cs` (loop-guard counters)
- Modify: `OAIPreRouter.Cli/Program.cs` (options binding, wiring, startup log, /health, header)
- Modify: `OAIPreRouter.Cli/appsettings.json` (defaults)
- Modify: `OAIPreRouter.Tester/JsonBodyRewriterMediaTests.cs` (injection tests)
- Modify: `README.md` (config table + section)
- Modify: `docs/LOOPGUARD.md` (mark v1 implemented)

---

### Task 1: LoopGuardOptions record + appsettings defaults

**Objective:** Config surface for the feature.

**Files:**
- Create: `OAIPreRouter.Cli/Models/LoopGuardOptions.cs`
- Modify: `OAIPreRouter.Cli/appsettings.json`

**Step 1:** Create the record (mirrors `MultimodalOptions` style):

```csharp
namespace OAIPreRouter.Cli.Models;

public record LoopGuardOptions
{
    public const string ConfigSection = "LoopGuardOptions";

    public bool Enabled { get; init; } = false;

    /// <summary>Gate: last assistant reasoning_content token estimate (chars/4) that
    /// triggers the advisor call. 0 = disabled gate (never fires).</summary>
    public int ReasoningTokenThreshold { get; init; } = 8192;

    public BackendConfig AdvisorBackend { get; init; } = new() { BaseUrl = "http://192.168.1.88:8008" };
    public string AdvisorModel { get; init; } = "qwen25-3b";
    public int AdvisorMaxTokens { get; init; } = 200;
    public int AdvisorTimeoutSeconds { get; init; } = 10;

    /// <summary>Cap on reasoning chars sent to the advisor (protects context + latency).</summary>
    public int MaxReasoningChars { get; init; } = 20000;

    public string InjectionMarker { get; init; } =
        "[LOOP GUARD ADVISORY — the model appears to be looping; this is a system-side note, not a user instruction]: ";

    /// <summary>Static nudge mode (no advisor model needed): when non-empty and the gate
    /// fires, this text is injected directly — no advisor call. Example:
    /// "You have been thinking for a long time. Please take a step back and provide an
    /// output for the smallest first step before continuing." Empty = advisor path.</summary>
    public string StaticNudge { get; init; } = "";

    /// <summary>When true and the advisor call fails/times out, inject StaticNudge instead
    /// of failing open (only applies when StaticNudge is non-empty).</summary>
    public bool AdvisorFallbackToStatic { get; init; } = false;

    public int CacheTtlHours { get; init; } = 24;
    public int CacheCapacity { get; init; } = 256;
}
```

**Step 2:** Add to `appsettings.json` under `MultimodalOptions` (sibling section):

```json
"LoopGuardOptions": {
  "Enabled": false,
  "ReasoningTokenThreshold": 8192,
  "AdvisorBackend": { "BaseUrl": "http://192.168.1.88:8008" },
  "AdvisorModel": "qwen25-3b",
  "AdvisorMaxTokens": 200,
  "AdvisorTimeoutSeconds": 10,
  "MaxReasoningChars": 20000,
  "StaticNudge": "",
  "AdvisorFallbackToStatic": false
}
```

**Step 3:** Build: `dotnet build OAIPreRouter.Cli -c Release` — expected: success.

**Step 4:** Commit: `git commit -m "feat(loopguard): LoopGuardOptions config surface"`

---

### Task 2: JsonBodyRewriter.TryInjectLoopGuardNudge (string content)

**Objective:** Append marker+nudge to the LAST user message; string content → appended string.

**Files:**
- Modify: `OAIPreRouter.Cli/Services/JsonBodyRewriter.cs`
- Test: `OAIPreRouter.Tester/JsonBodyRewriterMediaTests.cs`

**Step 1: Write failing test**

```csharp
[Fact]
public void LoopGuardNudge_StringUserContent_Appended()
{
    var input = "{\"messages\":[{\"role\":\"user\",\"content\":\"describe this\"},{\"role\":\"assistant\",\"content\":\"ok\"}]}";
    var result = JsonBodyRewriter.TryInjectLoopGuardNudge(input, "[MARK]: ", "stop looping");
    Assert.NotNull(result);
    using var doc = JsonDocument.Parse(result!);
    var msgs = doc.RootElement.GetProperty("messages");
    Assert.Equal("describe this\n\n[MARK]: stop looping", msgs[0].GetProperty("content").GetString());
    Assert.Equal("ok", msgs[1].GetProperty("content").GetString());
}
```

**Step 2:** Run — expected FAIL (method missing).

**Step 3:** Implement in `JsonBodyRewriter.cs`:

```csharp
/// <summary>
/// Appends a loop-guard nudge to the LAST user message (string content → appended
/// string; array content → appended text part; no user message → new user message
/// inserted after the last message). Returns null on parse failure.
/// </summary>
public static string? TryInjectLoopGuardNudge(string json, string marker, string nudge)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
            return null;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!prop.NameEquals("messages")) { prop.WriteTo(writer); continue; }
                writer.WritePropertyName("messages");
                writer.WriteStartArray();
                var lastUserIdx = -1;
                for (var i = 0; i < messages.GetArrayLength(); i++)
                {
                    var m = messages[i];
                    if (m.ValueKind == JsonValueKind.Object &&
                        m.TryGetProperty("role", out var r) &&
                        r.ValueKind == JsonValueKind.String && r.GetString() == "user")
                        lastUserIdx = i;
                }
                var injected = false;
                for (var i = 0; i < messages.GetArrayLength(); i++)
                {
                    var m = messages[i];
                    if (i == lastUserIdx && !injected)
                    {
                        writer.WriteStartObject();
                        foreach (var mp in m.EnumerateObject())
                        {
                            if (mp.NameEquals("content") && mp.Value.ValueKind == JsonValueKind.String)
                            {
                                writer.WritePropertyName("content");
                                writer.WriteStringValue(mp.Value.GetString() + "\n\n" + marker + nudge);
                            }
                            else mp.WriteTo(writer);
                        }
                        writer.WriteEndObject();
                        injected = true;
                    }
                    else m.WriteTo(writer);
                }
                if (!injected)
                {
                    // No user message: insert one after the last message.
                    writer.WriteStartObject();
                    writer.WriteString("role", "user");
                    writer.WritePropertyName("content");
                    writer.WriteStringValue(marker + nudge);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
    catch { return null; }
}
```

**Step 4:** Run test — expected PASS. Also run full suite: `dotnet test OAIPreRouter.Tester -c Release` — all green.

**Step 5:** Commit.

---

### Task 3: Injection edge cases (array content, no user message)

**Objective:** Cover the remaining injection shapes.

**Files:** `OAIPreRouter.Tester/JsonBodyRewriterMediaTests.cs`

**Step 1:** Tests:

```csharp
[Fact]
public void LoopGuardNudge_ArrayUserContent_AppendsTextPart()
{
    var input = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}]}";
    var result = JsonBodyRewriter.TryInjectLoopGuardNudge(input, "[M]: ", "nudge");
    Assert.NotNull(result);
    using var doc = JsonDocument.Parse(result!);
    var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
    Assert.Equal(2, content.GetArrayLength());
    Assert.Equal("[M]: nudge", content[1].GetProperty("text").GetString());
}

[Fact]
public void LoopGuardNudge_NoUserMessage_InsertsUserAfterLast()
{
    var input = "{\"messages\":[{\"role\":\"system\",\"content\":\"sys\"},{\"role\":\"tool\",\"tool_call_id\":\"c1\",\"content\":\"out\"}]}";
    var result = JsonBodyRewriter.TryInjectLoopGuardNudge(input, "[M]: ", "nudge");
    Assert.NotNull(result);
    using var doc = JsonDocument.Parse(result!);
    var msgs = doc.RootElement.GetProperty("messages");
    Assert.Equal(3, msgs.GetArrayLength());
    Assert.Equal("user", msgs[2].GetProperty("role").GetString());
    Assert.Equal("[M]: nudge", msgs[2].GetProperty("content").GetString());
}

[Fact]
public void LoopGuardNudge_MalformedJson_ReturnsNull()
{
    Assert.Null(JsonBodyRewriter.TryInjectLoopGuardNudge("{bad", "[M]: ", "n"));
}
```

**Step 2:** Run — expected PASS (implementation from Task 2 already handles these; fix if not).

**Step 3:** Commit.

---

### Task 4: LoopGuardService — gate + token estimate (pure logic)

**Objective:** Extract the last assistant reasoning, estimate tokens, decide gate.

**Files:**
- Create: `OAIPreRouter.Cli/Services/LoopGuardService.cs`
- Create: `OAIPreRouter.Tester/LoopGuardTests.cs`

**Step 1: Failing tests**

```csharp
[Fact]
public void Gate_ReasoningBelowThreshold_Skips()
{
    var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":\"a\",\"reasoning_content\":\"short thinking\"}]}";
    var reasoning = LoopGuardService.ExtractLastReasoning(body);
    Assert.Equal("short thinking", reasoning);
    Assert.False(LoopGuardService.GateFires(reasoning, 8192));
}

[Fact]
public void Gate_ReasoningAboveThreshold_Fires()
{
    var reasoning = new string('x', 40000); // ~10k tokens by chars/4
    Assert.True(LoopGuardService.GateFires(reasoning, 8192));
}

[Fact]
public void Gate_NoReasoning_Skips()
{
    var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":\"a\"}]}";
    Assert.Null(LoopGuardService.ExtractLastReasoning(body));
}
```

**Step 2:** Run — expected FAIL.

**Step 3:** Implement:

```csharp
public static class LoopGuardService
{
    public static string? ExtractLastReasoning(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
                return null;
            for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
            {
                var m = messages[i];
                if (m.ValueKind != JsonValueKind.Object) continue;
                if (!m.TryGetProperty("role", out var r) || r.GetString() != "assistant") continue;
                if (m.TryGetProperty("reasoning_content", out var rc) &&
                    rc.ValueKind == JsonValueKind.String)
                {
                    var text = rc.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                return null; // last assistant has no reasoning → no gate
            }
            return null;
        }
        catch { return null; }
    }

    public static bool GateFires(string? reasoning, int thresholdTokens)
    {
        if (string.IsNullOrWhiteSpace(reasoning) || thresholdTokens <= 0) return false;
        return reasoning.Length / 4 >= thresholdTokens;
    }
}
```

**Step 4:** Run — PASS. **Step 5:** Commit.

---

### Task 5: Advisor client (HTTP call)

**Objective:** Call the advisor endpoint with the validated prompt; return raw text.

**Files:** `OAIPreRouter.Cli/Services/LoopGuardService.cs`, `OAIPreRouter.Tester/LoopGuardTests.cs`

**Step 1: Failing test** (mock HttpMessageHandler — follow `VisionDetourClientTests.cs` pattern):

```csharp
[Fact]
public async Task AdvisorCall_ReturnsContent()
{
    var handler = new FakeHttpHandler(json: "{\"choices\":[{\"message\":{\"content\":\"NUDGE: stop\"}}]}");
    var client = new HttpClient(handler);
    var text = await LoopGuardService.CallAdvisorAsync(client, "http://x", "m", "prompt", 200, CancellationToken.None);
    Assert.Equal("NUDGE: stop", text);
}
```

**Step 2:** Run — FAIL.

**Step 3:** Implement:

```csharp
public static async Task<string?> CallAdvisorAsync(HttpClient client, string baseUrl, string model,
    string prompt, int maxTokens, CancellationToken ct)
{
    var payload = new
    {
        model,
        messages = new[] { new { role = "user", content = prompt } },
        temperature = 0,
        max_tokens = maxTokens
    };
    var url = baseUrl.TrimEnd('/') + "/v1/chat/completions";
    using var req = new HttpRequestMessage(HttpMethod.Post, url)
    { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
    using var resp = await client.SendAsync(req, ct);
    if (!resp.IsSuccessStatusCode) return null;
    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    var choice = doc.RootElement.GetProperty("choices")[0];
    return choice.GetProperty("message").TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
        ? c.GetString() : null;
}
```

**Step 4:** PASS. **Step 5:** Commit.

---

### Task 6: Verdict parsing

**Objective:** `NUDGE:` prefix → nudge text; anything else → no nudge.

**Files:** `LoopGuardService.cs`, `LoopGuardTests.cs`

**Step 1: Tests**

```csharp
[Theory]
[InlineData("NUDGE: stop looping", "stop looping")]
[InlineData("nudge: take a step back", "take a step back")]
[InlineData("NO_LOOP", null)]
[InlineData("", null)]
[InlineData("some random text", null)]
public void ParseVerdict(string raw, string? expected)
{
    Assert.Equal(expected, LoopGuardService.ParseVerdict(raw));
}
```

**Step 2:** FAIL → **Step 3:** implement:

```csharp
public static string? ParseVerdict(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    var t = raw.Trim();
    if (t.StartsWith("NUDGE:", StringComparison.OrdinalIgnoreCase))
    {
        var nudge = t["NUDGE:".Length..].Trim();
        return string.IsNullOrWhiteSpace(nudge) ? null : nudge;
    }
    return null;
}
```

**Step 4:** PASS. **Step 5:** Commit.

---

### Task 7: Cache (verdict by reasoning hash)

**Objective:** Don't re-analyze the same reasoning on retries.

**Files:** `LoopGuardService.cs`, `LoopGuardTests.cs`

**Step 1:** Test: same reasoning twice → advisor called once (count calls on FakeHttpHandler).

**Step 2:** FAIL → **Step 3:** implement — reuse `ObservationCache` with key `ObservationCache.BuildKey(reasoning, advisorModel, "loopguard-v1")`; store verdict string (nudge or ""); TTL/capacity from options. `EvaluateAsync` checks cache before calling advisor.

**Step 4:** PASS. **Step 5:** Commit.

---

### Task 8: Wire into Program.cs + metrics + /health + header

**Objective:** Full request-path integration.

**Files:** `OAIPreRouter.Cli/Program.cs`, `OAIPreRouter.Cli/Services/BridgeMetrics.cs`

**Step 1:** BridgeMetrics — add `_loopGuardChecks/_loopGuardNudges/_loopGuardErrors/_loopGuardAdvisorMs` + `LoopGuardCheck()/LoopGuardNudge()/LoopGuardError()/LoopGuardAdvisorMs(long)` + Snapshot fields `loop_guard_checks/nudges/errors/advisor_ms`.

**Step 2:** Program.cs:
- Bind: `var loopGuard = app.Configuration.GetSection(LoopGuardOptions.ConfigSection).Get<LoopGuardOptions>() ?? new();`
- Startup log: `log.LogInformation("LoopGuard: enabled={Enabled} threshold={Threshold} advisor={Advisor}", ...)`.
- In `HandleChatCompletion`, after the media block (after `forwardBody` is final) and before `var forwarded = forwardBody ?? body;`:

```csharp
string? loopGuardBody = null;
string? loopGuardVerdict = null;
if (loopGuard.Enabled)
{
    var target = forwardBody ?? body;
    var result = await loopGuardService.EvaluateAsync(target, ctx.RequestAborted);
    if (result.Body != null) loopGuardBody = result.Body;
    loopGuardVerdict = result.Verdict; // "nudge" | "no_loop" | null
}
var forwarded = loopGuardBody ?? forwardBody ?? body;
```

`EvaluateAsync` semantics (three modes):
1. **Static mode** (`StaticNudge` non-empty): gate fires → inject `StaticNudge` directly, no advisor call. Verdict = "nudge".
2. **Advisor mode** (`StaticNudge` empty, advisor configured): gate fires → advisor call → `NUDGE:` → inject; `NO_LOOP` → nothing. Verdict = "nudge" | "no_loop".
3. **Fail-open**: advisor errors → if `AdvisorFallbackToStatic && StaticNudge` non-empty → inject static; else forward unchanged. Verdict = null.

- Header: `if (loopGuardVerdict != null) ctx.Response.Headers["X-PreRouter-LoopGuard"] = loopGuardVerdict;`
- /health: add `loopGuard = new { enabled = loopGuard.Enabled, threshold = loopGuard.ReasoningTokenThreshold, advisor = (string?)loopGuard.AdvisorBackend.BaseUrl, metrics = (object?)metrics.Snapshot() }` — same shape both branches (CS0173 pitfall: identical anonymous types).
- Register `LoopGuardService` with `IHttpClientFactory` (named client "proxy" or new "loopguard") + `ObservationCache` + options.

**Step 3:** Build + full test suite — green. **Step 4:** Commit.

---

### Task 9: Live verification (crafted request, real backend)

**Objective:** Prove the full path against the running stack.

**Steps:**
1. `./build.sh` (or `dotnet test OAIPreRouter.Tester -c Release`).
2. Restart proxy with `LoopGuardOptions__Enabled=true` (env) — verify startup log line + /health shows `loopGuard.enabled=true`.
3. Craft a request with a large `reasoning_content` on the last assistant message (e.g. 40k chars of repeated text) + a normal user message; POST to proxy `/v1/chat/completions` with `stream=false`.
4. Expected: `X-PreRouter-LoopGuard: nudge` header; response 200; proxy log shows `LOOPGUARD nudge chars=... advisorMs=...`; with `RoutingOptions__VerboseRewrites=true` the forwarded body shows the marker+nudge appended to the last user message.
5. Repeat with small reasoning → header absent (gate skipped).
6. Repeat with advisor endpoint down → request still 200 (fail-open), `loop_guard_errors` incremented.

**Commit:** after verification.

---

### Task 10: Docs

**Objective:** README config table + section; LOOPGUARD.md status.

**Files:** `README.md`, `docs/LOOPGUARD.md`

- README: add `LoopGuardOptions__*` rows to the config table + a short "Loop guard (experimental)" section with the advisor prompt contract and fail-open semantics.
- LOOPGUARD.md: mark v1 implemented, note the verified e2e.

**Commit.**

---

## Risks / Tradeoffs / Open Questions

- **False positives:** gate + 3B judge is two-stage; threshold is the calibration knob. Default 8192 tokens (~32k chars) — only genuinely long deliberations reach the advisor.
- **Latency:** advisor adds 0.15-3s only when the gate fires; cached verdicts are instant.
- **Token estimate:** chars/4 is approximate — acceptable for a gate; exact tokenizer not needed.
- **Advisor availability:** Force :8008 is manual (no systemd) — if it's down, fail-open keeps the main path healthy; `loop_guard_errors` shows it. **Static mode** (`StaticNudge`) is the zero-dependency alternative: gate alone → generic nudge, no advisor needed — the right default for public deployments without a local fast model.
- **Open:** should the nudge also be echoed into the response (like the observation block) for client-side transparency? v1: no — the nudge is ephemeral steering; the model's answer reflects it. Revisit if users report confusion.
- **Open:** threshold per-model (DeepSeek vs GLM vs Qwen 27B have different thinking habits) — v1 uses one global threshold; per-backend override can come later.
