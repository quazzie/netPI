# AiProxy — agent guide

## What AiProxy is

AiProxy is a persistent, OpenAI-compatible inference proxy that listens on
`http://127.0.0.1:8090`. It is the single endpoint for all local inference
clients (DSH, Pi, and any OpenAI-compatible client). You point your client at
this base URL and it transparently routes each request to the correct backend
based on the `model` field.

**Backends** (routed by model id):

| Backend | Model(s) | Notes |
|---|---|---|
| `llama` (remote) | remote llama.cpp router (`192.168.1.3:8080`) | always-on, remote box |
| `llama-local` (local) | local llama.cpp router (`127.0.0.1:1234`) | GPU-exclusive with ComfyUI/NInfer |
| `ninfer` | `qwen3.8-27b` via local `ninfer-serve.exe :8080` | GPU-exclusive with the above |

**Key behaviors an agent must know:**

1. **Model id selects the route.** Any model id listed by `GET /v1/models` is
   valid in `POST /v1/chat/completions` (and other OpenAI endpoints). The proxy
   forwards the body untouched to the owning backend, rewriting only the
   `model` field (alias resolution) and injecting telemetry.
2. **Unloaded models are loadable.** A llama router entry with
   `status.value: "unloaded"` is still routable — the upstream router
   auto-loads the preset model on the first request (first response is slower
   due to load/prefill). `status.value: "loaded"` means it is resident.
3. **Stopped backends hold requests.** If the owning backend is offline, the
   request is held for up to a 2-minute outage grace window (re-probed ~1 Hz)
   and then answered with `503 backend_unavailable`. Requests are cancellable
   during the hold.
4. **Aliases.** `aiproxy.json` can define static aliases (a public name that
   maps to a real model) and policy aliases (a public name that resolves to a
   candidate list, with conservative capability = the worst candidate). Alias
   ids appear as extra entries in `/v1/models`.
5. **Endpoints:**
   - `GET /v1/models` — model catalog (this document's focus).
   - `GET /v1/models/{id}` — upstream model lookup for one model; returns
     404 if unknown/unroutable.
   - `POST /v1/chat/completions`, `/v1/completions`, `/v1/responses`,
     `/v1/embeddings`, … — routed by `model`.
   - `GET /health` — proxy health only (`{"status":"ok","proxy":"aiproxy",
     "backends":{...}}`); backend availability is per-backend, not implied by
     a model's status.

## `GET /v1/models`

Standard OpenAI list envelope:

```json
{ "object": "list", "data": [ <model entry>, ... ] }
```

Each model entry:

| Field | Type | Meaning |
|---|---|---|
| `id` | string | The model id to put in the `model` field of requests. Stable; this is the routing key. |
| `object` | string | Always `"model"`. |
| `owned_by` | string | `"llamacpp"` or `"ninfer"` — which engine family serves it. |
| `created` | int | Unix seconds (engine start time, not meaningful — ignore). |
| `context_window` | int\|null | Usable context tokens (KV context, `n_ctx`). Runtime probe data wins; static capability table fills in while the model is unloaded. `null` = unknown. |
| `max_output_tokens` | int\|null | Max tokens a single completion should request. `null` = not constrained/unknown — do not assume. |
| `concurrency` | int\|null | Parallel slots the backend can serve for this model. `1` = one request at a time (queueing is fine — the backend queues); `null` = unknown. |
| `input_modalities` | string[] | e.g. `["text"]` or `["text","image"]`. Only send images if `"image"` is present. |
| `reasoning` | object\|null | `null` = model has no reasoning control (or unknown). Non-null: `supported` (bool), `efforts` (allowed reasoning-effort values, e.g. `["none","low","medium","xhigh"]`), `default` (the backend default if you don't specify). |
| `meta.n_ctx` | int\|null | Same value as `context_window`, exposed in engine dialect. |
| `status.value` | string | Live state: `"loaded"` (resident, fast to serve), `"unloaded"` (routable but will be loaded on first request — expect a slow first response), `"stopped"` (owning backend not running — request will be held up to 2 min, then 503), `"offline"` (remote backend unreachable). |

**Choosing a model, as an agent, in order:**
1. Filter `input_modalities` for what the task needs (`"image"` for vision).
2. Filter by `status.value` — prefer `"loaded"` for low latency; accept
   `"unloaded"` if you can tolerate a slow first response; avoid
   `"stopped"`/`"offline"` unless the user accepts a hold or a 503.
3. Check `context_window` covers the conversation + expected output.
4. Honor `max_output_tokens` if present; set your reasoning effort within
   `reasoning.efforts` if reasoning control matters (or omit to get `default`).

## Example response (live capture, 2026-07-21)

```json
{
  "object": "list",
  "data": [
    {
      "id": "gemma-4", "object": "model", "owned_by": "llamacpp", "created": 1790183814,
      "context_window": 65536, "max_output_tokens": null, "concurrency": 1,
      "input_modalities": ["text"],
      "reasoning": { "supported": true, "efforts": ["none", "max"], "default": "max" },
      "meta": { "n_ctx": 65536 }, "status": { "value": "unloaded" }
    },
    {
      "id": "ornith15-9b-mtp-128k", "object": "model", "owned_by": "llamacpp", "created": 1790183814,
      "context_window": 131072, "max_output_tokens": null, "concurrency": 1,
      "input_modalities": ["text", "image"],
      "reasoning": { "supported": true, "efforts": ["none", "max"], "default": "max" },
      "meta": { "n_ctx": 131072 }, "status": { "value": "unloaded" }
    },
    {
      "id": "qwen38-27b-iq3s", "object": "model", "owned_by": "llamacpp", "created": 1790183814,
      "context_window": 524288, "max_output_tokens": null, "concurrency": null,
      "input_modalities": ["text"], "reasoning": null,
      "meta": { "n_ctx": 524288 }, "status": { "value": "stopped" }
    },
    {
      "id": "qwen3.8-27b", "object": "model", "owned_by": "ninfer", "created": 1790183814,
      "context_window": 262144, "max_output_tokens": 16384, "concurrency": 2,
      "input_modalities": ["text", "image"],
      "reasoning": { "supported": true, "efforts": ["none", "low", "medium", "xhigh"], "default": "medium" },
      "meta": { "n_ctx": 262144 }, "status": { "value": "loaded" }
    }
  ]
}
```

## Minimal agent contract

```text
BASE = http://127.0.0.1:8090
GET  {BASE}/v1/models                      -> pick a model per the rules above
POST {BASE}/v1/chat/completions            -> { "model": "<id from catalog>",
                                              "messages": [...],
                                              "max_tokens": <= max_output_tokens,
                                              "reasoning_effort": in reasoning.efforts,
                                              "stream": true|false }
Errors: 503 {"error":{"type":"backend_unavailable"}} if the backend stays
         offline; 404 if the model id is not in the catalog (re-fetch
         /v1/models).
```
