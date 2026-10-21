# MediaLLMProxy — Quickstart & Configuration

This guide covers **how the proxy is configured**: which parameters live in the
JSON file, which live in environment variables, how they layer, and a full
current-configuration example (DeepSeek GA text + local vision model + guard
prompt injection) you can adapt.

---

## 1. Configuration layering

The proxy uses standard .NET configuration binding. Later layers win:

```
1. appsettings.json        → base configuration in the process working directory
2. appsettings.<ENV>.json  → selected by ASPNETCORE_ENVIRONMENT
3. Environment variables   → override JSON configuration
4. Command-line options    → override the preceding layers
```

**Rule of thumb:**

| What | Where it belongs |
|---|---|
| Endpoint URLs, model ids, thresholds, cache, URL policy | `appsettings.json` (non-secret defaults) |
| Non-secret endpoint URLs, guard settings, per-deployment tweaks | per-environment JSON or environment variables |
| Anything secret | environment variables only — never commit |

Env var naming: the JSON path becomes `__`-separated uppercase:
`RoutingOptions.PrimaryBackend.BaseUrl` → `RoutingOptions__PrimaryBackend__BaseUrl`.
`MultimodalOptions.Enabled` → `MultimodalOptions__Enabled`.

For systemd, the unit's `WorkingDirectory` is the configuration root. A file in
the binary's `bin/` directory is not read unless that is also the working
directory. Options are read once at startup; edits require a restart. Features
added in code also require deploying the updated binary before restarting.

---

## 2. The JSON side (appsettings.json) — non-secret defaults

```json
{
  "RoutingOptions": {
    "ListenUrl": "http://0.0.0.0:7071",
    "PrimaryBackend": {
      "BaseUrl": "http://localhost:8000",
      "CureRawToolCalls": false
    },
    "FastBackend":  { "BaseUrl": "http://localhost:8000", "MaxConcurrentConnections": 2 },
    "HeavyBackend": { "BaseUrl": "http://localhost:8000", "MaxConcurrentConnections": 2 },
    "SystemPromptThresholdBytes": 100000,
    "FastLaneThresholdBytes": 65536,
    "LogDecisions": true,
    "LogBodies": false,
    "VerboseRequests": false
  },
  "MultimodalOptions": {
    "Enabled": false,
    "VisionBackend": { "BaseUrl": "http://localhost:8000" },
    "VisionModel": "Qwen3.6-35B-A3B-MLX-VL-oQ8",
    "MaxObservationTokens": 2048,
    "TimeoutSeconds": 90,
    "CacheTtlHours": 24,
    "CacheCapacity": 512,
    "UrlPolicy": { "AllowDataUrls": true, "AllowHttpsHosts": [], "MaxMediaBytes": 5000000 }
  },
  "UnavailableToolRetryOptions": {
    "Enabled": false,
    "MaxRetries": 1,
    "RetryRawToolCallAborts": true,
    "MaxResponseBytes": 16777216
  }
}
```

You can leave the JSON untouched and do everything from env vars — but the JSON
file is the right home for stable, non-secret defaults (e.g. vision model id).

---

## 3. The env-var side — everything deployment-specific

| Env var | Meaning |
|---|---|
| `RoutingOptions__ListenUrl` | bind address (default `http://0.0.0.0:7071`) |
| `RoutingOptions__PrimaryBackend__BaseUrl` | text backend root — **proxy appends `/v1/chat/completions`** (see Gotchas) |
| `RoutingOptions__PrimaryBackend__ApiKey` | text backend key (overrides any client key) |
| `RoutingOptions__PrimaryBackend__RewriteModel` | the ONE model the proxy executes (enforced) |
| `RoutingOptions__PrimaryBackend__ModelAlias` | name advertised via `/v1/models` (clients ask for this) |
| `RoutingOptions__PrimaryBackend__InjectedSystemPrompt` | guard prompt prepended to every request (inline) |
| `RoutingOptions__PrimaryBackend__InjectedSystemPromptPath` | guard prompt from a file (wins over inline; missing file aborts startup) |
| `RoutingOptions__PrimaryBackend__Temperature` | text sampling override — forced on every request, beats client values (null = leave client's) |
| `RoutingOptions__PrimaryBackend__TopP` | text sampling override — forced on every request, beats client values (null = leave client's) |
| `RoutingOptions__PrimaryBackend__ToolOutputContract` | `LegacyRaw` (default) or `StrictNative`. `StrictNative` means the backend owns tool parsing and emits native `tool_calls`. The proxy does not scan or rewrite content and retries one typed guard rejection. |
| `RoutingOptions__PrimaryBackend__CureRawToolCalls` | false by default; **LegacyRaw only**. Convert supported raw DSML/XML content to standard `tool_calls`. Ignored when `ToolOutputContract=StrictNative`. |
| `UnavailableToolRetryOptions__Enabled` | false by default; recover once from a rejected tool call with a corrective user message |
| `UnavailableToolRetryOptions__MaxRetries` | shared retry budget; defaults to 1 and is capped at 1 |
| `UnavailableToolRetryOptions__RetryRawToolCallAborts` | true by default; **LegacyRaw only**. Include proxy raw-tool rejections when recovery and curing are enabled. |
| `UnavailableToolRetryOptions__IncludeRequestContext` | true by default; strict retry messages include normalized offered-tool policy. `max_calls` is not included because the pinned backend ignores that wire field. |
| `UnavailableToolRetryOptions__MaxResponseBytes` | response buffer limit per non-streaming attempt, default 16777216 bytes (16 MiB) |
| `UnavailableToolRetryOptions__Banner` / `RawToolCallBanner` | legacy corrective text for unavailable tools / rejected raw markup |
| `RoutingOptions__VerboseRequests` | log incoming bodies (debug) |
| `RoutingOptions__VerboseRewrites` | log the rewritten body actually sent to the text model |
| `MultimodalOptions__Enabled` | master bridge switch (true to bridge images) |
| `MultimodalOptions__VideoSupport` | true = also bridge video parts (input_video/video_url/video) to the vision backend — backend must accept video natively (e.g. mlx-vlm qwen3_5/qwen3_5_moe); the terse-observer system prompt is omitted for video payloads (mlx-vlm rejects system+video) |
| `MultimodalOptions__DetourVision` | true (default) = images/video are DETOURED to the vision backend, text backend receives an observation. false = PASSTHROUGH: media parts stay in the request RAW and the primary backend must be natively multimodal (e.g. GLM-5.3-Flash, Qwen3.8-Omni). Passthrough skips the vision detour, observation injection and media rewrite — but `PrimaryBackend.Temperature/TopP` overrides still apply to the forwarded body |
| `MultimodalOptions__DetourAudio` | true (default) = audio is DETOURED to the STT backend, text backend receives a transcript. false = PASSTHROUGH: audio parts stay RAW in the request (primary must accept input_audio natively, e.g. an Omni model). Same semantics as DetourVision |
| `MultimodalOptions__VisionBackend__BaseUrl` | primary vision detour endpoint |
| `MultimodalOptions__VisionBackend__ApiKey` | primary vision token; omit for local M5/oMLX/vLLM |
| `MultimodalOptions__VisionModel` | primary vision model id |
| `MultimodalOptions__VisionFallbackBackend__BaseUrl` | optional failure-only fallback vision endpoint |
| `MultimodalOptions__VisionFallbackBackend__ApiKey` | fallback vision token; omit for local Qwen llama.cpp |
| `MultimodalOptions__VisionFallbackModel` | failure-only fallback model id |
| `MultimodalOptions__VisionBackend__Temperature` | vision sampling override — included in every observation request (null = backend default) |
| `MultimodalOptions__VisionBackend__TopP` | vision sampling override — included in every observation request (null = backend default) |
| `MultimodalOptions__VisionFallbackBackend__Temperature` | fallback vision sampling override (null = backend default) |
| `MultimodalOptions__VisionFallbackBackend__TopP` | fallback vision sampling override (null = backend default) |

---

## 4. Full example — cloud text and local vision

This is a historical production profile (2026-08): text = DeepSeek GA
(cloud, text-only), vision = local oMLX serving Qwen-VL (images never leave the
machine for the *seeing*), guard prompt injected from a file. Adapt the URLs.

```bash
# launch.sh — the launcher IS the config
export RoutingOptions__ListenUrl="http://0.0.0.0:7071"

# ── text backend: DeepSeek GA ───────────────────────────────────────────────
export RoutingOptions__PrimaryBackend__BaseUrl="https://api.deepseek.com"   # NO /v1 suffix!
export RoutingOptions__PrimaryBackend__ApiKey="$DEEPSEEK_API_KEY"           # from your env
export RoutingOptions__PrimaryBackend__RewriteModel="deepseek-v4-flash"     # the real model
export RoutingOptions__PrimaryBackend__ModelAlias="main"                    # clients ask for "main"

# ── guard prompt (adversarial-hardening; see README) ────────────────────────
export RoutingOptions__PrimaryBackend__InjectedSystemPromptPath="/etc/mediabridge/guard.txt"

# ── vision bridge: local oMLX 35B-VL ────────────────────────────────────────
export MultimodalOptions__Enabled="true"
export MultimodalOptions__VisionBackend__BaseUrl="http://localhost:8000"    # oMLX / vLLM / ollama
export MultimodalOptions__VisionModel="Qwen3.6-35B-A3B-MLX-VL-oQ8"

# ── observability ───────────────────────────────────────────────────────────
export RoutingOptions__VerboseRequests="false"
export RoutingOptions__VerboseRewrites="true"   # see exactly what the text model receives

dotnet run --project OAIPreRouter.Cli -c Release --no-build
```

Client side (any OpenAI-compatible client):

```bash
curl http://localhost:7071/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "main",                                  # the ModelAlias
    "messages": [{
      "role": "user",
      "content": [
        {"type": "text", "text": "What color is this image?"},
        {"type": "image_url", "image_url": {"url": "data:image/png;base64,..."}}
      ]
    }]
  }'
```

---

## 5. Minimal smoke test after any config change

```bash
# 1. is the proxy up and pointing where you think?
curl -s http://localhost:7071/health | python3 -m json.tool
#    → "primary": {"url": "..."}  must match your text backend

# 2. what does the model surface look like?
curl -s http://localhost:7071/v1/models
#    → data[].id must be your ModelAlias ("main")

# 3. text path
curl -s http://localhost:7071/v1/chat/completions -H "Content-Type: application/json" \
  -d '{"model":"main","messages":[{"role":"user","content":"Reply OK"}]}'

# 4. image path (with VerboseRewrites=true watch the log: BRIDGE → FORWARD)
#    the response header X-PreRouter-Media: image confirms the bridge fired
```

For DeepSeek DSML/XML tool recovery, enable both
`RoutingOptions.PrimaryBackend.CureRawToolCalls` and
`UnavailableToolRetryOptions.Enabled`. The complete opt-in profile is
[`examples/appsettings.tool-recovery.json`](examples/appsettings.tool-recovery.json).
It listens on loopback port 7071 and forwards to a backend on port 8100; neither
the sample nor a test instance needs to bind production port 8000.

After deployment/restart, `/health` should include:

```json
{
  "primary": { "cureRawToolCalls": true },
  "unavailableToolRetry": {
    "enabled": true,
    "maxRetries": 1,
    "retryRawToolCallAborts": true,
    "maxResponseBytes": 16777216
  }
}
```

The retry object is **top-level**, not nested inside `primary` or `loopGuard`.
If absent, check the deployed build and endpoint. If present but disabled,
check config precedence and whether the process restarted. Full behavior,
buffering costs, banner settings, error contracts and observability are in
[Tool-Call Recovery](docs/TOOL-CALL-RECOVERY.md).

---

## 6. Gotchas

1. **BaseUrl must NOT include `/v1`** — the proxy appends `/v1/chat/completions`.
   DeepSeek GA: `https://api.deepseek.com` (adding `/v1` → `/v1/v1/...` → 404).
   Local servers (oMLX/vLLM/Ollama): `http://host:port`.
2. **Keys live in env, never JSON** — `ApiKey` is a config binding; the
   launcher script should source it from your secret store / .env at launch.
3. **The guard file aborts startup if missing** — a guard that silently
   disappears is a security hole; the proxy refuses to run without it.
4. **`VerboseRewrites` shows the truth** — enable it once to confirm exactly
   what the text model receives (guard + observations + stripped media).
5. **Restart after config changes** — nothing hot-reloads; the guard file is
   read once at startup.
6. **`/health` counters** — scans/detours/cache hits/rewrites tell you the
   bridge actually worked on a request.
