# MediaLLMProxy — Quickstart & Configuration

This guide covers **how the proxy is configured**: which parameters live in the
JSON file, which live in environment variables, how they layer, and a full
current-configuration example (DeepSeek GA text + local vision model + guard
prompt injection) you can adapt.

---

## 1. Configuration layering

The proxy uses standard .NET configuration binding. Three layers, later wins:

```
1. appsettings.json        → checked-in defaults (repo)
2. appsettings.<ENV>.json  → per-environment overrides (rarely used)
3. Environment variables   → deployment config (keys, URLs, guards) — WINS
```

**Rule of thumb:**

| What | Where it belongs |
|---|---|
| Endpoint URLs, model ids, thresholds, cache, URL policy | `appsettings.json` (non-secret defaults) |
| API keys, real backend URLs, guard prompts, per-deployment tweaks | **environment variables** (never JSON) |
| Anything secret | environment variables only — never commit |

Env var naming: the JSON path becomes `__`-separated uppercase:
`RoutingOptions.PrimaryBackend.BaseUrl` → `RoutingOptions__PrimaryBackend__BaseUrl`.
`MultimodalOptions.Enabled` → `MultimodalOptions__Enabled`.

---

## 2. The JSON side (appsettings.json) — non-secret defaults

```json
{
  "RoutingOptions": {
    "ListenUrl": "http://0.0.0.0:7071",      // bind address
    "PrimaryBackend": { "BaseUrl": "http://localhost:8000" },
    "FastBackend":  { "BaseUrl": "http://localhost:8000", "MaxConcurrentConnections": 2 },
    "HeavyBackend": { "BaseUrl": "http://localhost:8000", "MaxConcurrentConnections": 2 },
    "SystemPromptThresholdBytes": 100000,     // lane routing thresholds (phase-1: unused)
    "FastLaneThresholdBytes": 65536,
    "LogDecisions": true,                      // log lane decisions
    "LogBodies": false,                        // deprecated (see VerboseRequests)
    "VerboseRequests": false                   // log incoming request bodies
  },
  "MultimodalOptions": {
    "Enabled": false,                          // master bridge switch — flip to true
    "VisionBackend": { "BaseUrl": "http://localhost:8000" },
    "VisionModel": "Qwen3.6-35B-A3B-MLX-VL-oQ8",
    "MaxObservationTokens": 512,               // observation length cap
    "TimeoutSeconds": 90,                      // vision detour timeout
    "CacheTtlHours": 24,                       // byte-keyed observation cache
    "CacheCapacity": 512,
    "UrlPolicy": { "AllowDataUrls": true, "AllowHttpsHosts": [], "MaxMediaBytes": 5000000 }
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
| `RoutingOptions__VerboseRequests` | log incoming bodies (debug) |
| `RoutingOptions__VerboseRewrites` | log the rewritten body actually sent to the text model |
| `MultimodalOptions__Enabled` | master bridge switch (true to bridge images) |
| `MultimodalOptions__VisionBackend__BaseUrl` | vision detour endpoint |
| `MultimodalOptions__VisionModel` | vision model id (JSON default usually fine) |

---

## 4. Full example — the current production configuration

This is the live configuration used by the authors (2026-08): text = DeepSeek GA
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
