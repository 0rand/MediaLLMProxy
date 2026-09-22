# MiMo Structured Output Compatibility

## Purpose

MiMo-family models served through vLLM may return a schema-valid final answer in
the OpenAI `reasoning_content` field while leaving `message.content` empty. This
breaks strict JSON consumers even though the model generated the requested JSON.

MediaLLMProxy provides an opt-in, model-scoped request policy:

```text
ordinary MiMo request                 → thinking behavior unchanged
request with `response_format`        → `enable_thinking: false`
```

The policy is deliberately request-layer logic, not a global parser heuristic.
It is intended for a dedicated MiMo proxy instance only.

## Root Cause

With thinking enabled, the vLLM `mimo` reasoning parser uses generic Qwen3
reasoning semantics. It initially classifies generated tokens as reasoning until
it observes the parser's recognized reasoning-end boundary.

On some post-tool strict-schema turns, MiMo emits the final JSON directly,
without that boundary. The generic parser therefore routes the JSON to
`reasoning_content`, and OpenAI `content` is empty.

A global rule such as "a JSON object ends reasoning" is unsafe: ordinary model
reasoning can itself contain JSON. The proxy has request context (`response_format`)
before generation, so it can select MiMo's no-think template path safely.

## Scope and Non-Goals

This policy fires only when the request includes a top-level `response_format`.
It preserves all existing `chat_template_kwargs` and overwrites only
`enable_thinking`.

It does not:

- disable thinking for ordinary tool use, agent work, or chat;
- inspect, promote, or expose `reasoning_content` as user content;
- repair model instruction-following when no `response_format` is supplied;
- add structured-output enforcement to a backend that does not support it.

In particular, a model can still fail an adversarial prompt that asks for JSON
but supplies no server-side `response_format`. That is a model behavior failure,
not a parser-routing failure.

## Dedicated MiMo Instance

Use a separate proxy process so other models retain their own routing and
sampling policies. Example topology:

```text
client → MiMo proxy :8020 → MiMo vLLM :8100
```

The example configuration is at
[`../deploy/appsettings.proxy-mimo.json`](../deploy/appsettings.proxy-mimo.json).
Set the backend URL, model identifier, listen port, and any authentication to
match your installation.

Required section:

```json
{
  "StructuredOutputOptions": {
    "DisableThinkingForResponseFormat": true
  }
}
```

The proxy exposes the active policy at `/health`:

```json
{
  "structuredOutput": {
    "disableThinkingForResponseFormat": true
  }
}
```

Affected responses include:

```text
X-PreRouter-Structured-Output: thinking-off
```

## Boot-Persistent systemd Deployment

1. Publish the binary and create an isolated runtime directory:

```bash
dotnet publish OAIPreRouter.Cli/OAIPreRouter.Cli.csproj -c Release \
  -o "$HOME/.local/share/mediallmproxy-mimo/bin"
mkdir -p "$HOME/.config/mediallmproxy-mimo"
cp OAIPreRouter.Cli/appsettings.json "$HOME/.config/mediallmproxy-mimo/"
cp deploy/appsettings.proxy-mimo.json \
  "$HOME/.config/mediallmproxy-mimo/appsettings.proxy-mimo.json"
```

2. Edit `appsettings.proxy-mimo.json` for your backend. Do not commit a
machine-specific copy or API key.

3. Copy `deploy/media-llm-proxy-mimo.service` to
`/etc/systemd/system/`, replacing `YOUR_USER` with the account that owns the
runtime directory.

4. Enable it for every boot and start it now:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now media-llm-proxy-mimo.service
systemctl is-enabled media-llm-proxy-mimo.service
systemctl is-active media-llm-proxy-mimo.service
curl --fail http://127.0.0.1:8020/health
```

Use `journalctl -u media-llm-proxy-mimo.service -f` for startup and request
logs. Keep this unit separate from any existing proxy units.

## Verification Ladder

1. Confirm the dedicated proxy advertises only the expected model:

```bash
curl http://127.0.0.1:8020/v1/models
```

2. Send an exact post-tool `response_format` request. Require both:

```text
- JSON in choices[0].message.content
- X-PreRouter-Structured-Output: thinking-off
```

3. Run the full tool benchmark through the proxy, using the same benchmark
build, seed, scenario count, parallelism, and timeout as the raw-server
baseline. Compare raw points, not only rounded scores.

One verified MiMo-V2.6-Flash deployment, on tool-eval-bench
`v2.6.1.dev65+g6be685f0e`, moved from 159/176 (90/100) direct to 163/176
(93/100) through the dedicated proxy. Structured Output improved from 6/12 to
10/12: the four post-tool schema cases recovered, while a distinct no-enforcement
model-formatting case remained.

## Updating This Project

This is an independent project, not an external GitHub fork. Keep local changes
on `main`, then push the tested commit. If you later adopt an external upstream,
add it explicitly and merge deliberately:

```bash
git remote add upstream https://github.com/OWNER/REPOSITORY.git
git fetch upstream
git checkout main
git merge upstream/main
./build.sh
```

Do not merge an upstream change directly into a deployed binary. Build and run
the full test suite first.
