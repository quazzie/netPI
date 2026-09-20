# §14b. Responses API wire mode

Status: **planned** — supersedes "chat completions only" from PLAN-v1 §13/§14.
The legacy plan lives at [docs/archive/PLAN-v1.md](../archive/PLAN-v1.md).

`netPI.Provider.AiProxy` gains a second wire protocol — the OpenAI **Responses
API** (`POST /v1/responses`) — in addition to Chat Completions. Both protocols
are supported concurrently; nothing about chat mode changes.

## 1. Decision record

- **Both protocols coexist.** Chat Completions remains the stable baseline and
  the fallback. Responses mode is additive, provider-internal.
- **Default is `auto`, which PREFERS `/v1/responses`** when the model supports
  it, with **per-model fallback** to chat completions otherwise.
- **Stateless first (v1).** Always send the full typed `input`; `store: false`.
  No `previous_response_id` chaining in v1 (it conflicts with steering §12,
  AutoCompact §32, and the retry plugin). The KV-cache win still materializes
  because the engine prefix-caches the stable prefix — verified locally.
  Server-side chaining is a documented future phase (§7).

## 2. Verified environment facts (probed through AiProxy)

Probed against local AiProxy (`127.0.0.1:8090`) proxying nInfer
(`qwen3.8-27b`). The provider always talks to AiProxy, never directly to a
backend.

| Probe | Result |
|---|---|
| `POST /v1/responses` non-streaming | 200, `object:"response"`, typed `output[]` (`reasoning`, `message`) |
| Streaming events | `response.created` → `response.in_progress` → `response.output_item.added` → `response.content_part.added` → `response.reasoning_text.delta` / `response.reasoning_text.done` → `response.content_part.done` → `response.output_item.done` → (function call item: `response.function_call_arguments.delta` / `.done`) → `response.completed` (carries `usage`). No `[DONE]` sentinel. |
| `previous_response_id` chain | Works; chained turn had **127/150 input tokens cached** (`usage.input_tokens_details.cached_tokens`) |
| Tool round-trip | `function_call` + `function_call_output` input items accepted; model answered from the result without re-calling |
| Usage | `input_tokens`, `output_tokens`, `input_tokens_details.cached_tokens`, `output_tokens_details.reasoning_tokens`, `total_tokens` |
| Reasoning items | Plaintext `reasoning_text` content parts; `reasoning.effort` request field accepted |
| Local event-name divergence | nInfer emits `reasoning_text.delta` (OpenAI: `reasoning_summary_text.delta`) — parser must key on item `type`, not exact event name |

Note: `~/.netpi/config.json` `netpi.provider.aiproxy.baseUrl` now points at the
AiProxy port (8090). A config pointing directly at a backend port (8080)
bypasses AiProxy catalog enrichment and routing and must be avoided.

## 3. Wire shape (delta vs chat completions)

Request:

```jsonc
POST /v1/responses
{
  "model": "...",
  "stream": true,
  "store": false,
  "instructions": "system prompt text",          // top-level; system role
  "input": [
    { "type":"message", "role":"user",
      "content":[{ "type":"input_text", "text":"..." }] },
    { "type":"message", "role":"assistant",
      "content":[{ "type":"output_text", "text":"..." }] },
    { "type":"function_call", "call_id":"call_1", "name":"f", "arguments":"{...}" },
    { "type":"function_call_output", "call_id":"call_1", "output":"..." },
    { "type":"reasoning", "id":"rs_...", "content":[{"type":"reasoning_text","text":"..."}] }
  ],
  "tools": [ { "type":"function", "name":"...", "description":"...", "parameters":{...} } ],
  "reasoning": { "effort": "low|medium|high" },
  "max_output_tokens": 4096,
  "temperature": 0.7
}
```

Response (non-streaming): `response` object with `id`, `output[]`
(`message` / `reasoning` / `function_call` items), `status`, `usage`.

## 4. netPI mapping

### Request build (`BuildResponsesPayload`)

| netPI source | Responses item |
|---|---|
| System message (first) | top-level `instructions` |
| `User` / `Assistant` `TextPart` | `message` item, `input_text` / `output_text` |
| `Assistant` `ThinkingPart` | `reasoning` item (`reasoning_text`); if the model 400s on reasoning input, retry once without it (log) |
| `Assistant` `ToolCallPart` | `function_call` (`call_id` = part id) |
| `Tool` `ToolResultPart` | `function_call_output` (`call_id` = `ToolCallId`) |
| `Tools` | same JSON shape as chat (verified accepted) |
| `ReasoningLevel` | `reasoning.effort` (skip when empty/"off") |
| `MaxTokens` / `Temperature` | `max_output_tokens` / `temperature` |
| `SessionId`, `Seed` | not sent (seed: skip in v1 — responses schema differs; revisit) |

### Stream parse (`ParseResponseEvent`)

Branch on event `type`; unknown types ignored (matches catalog philosophy).

| nInfer event | ModelEvent |
|---|---|
| `response.created` / `response.in_progress` | `ModelStarted` (once) |
| `output_item.added` (reasoning) → `reasoning_text.delta` | `ThinkingStarted` / `ThinkingDelta` |
| `output_item.added` (message) → `output_text.delta` | `TextStarted` / `TextDelta` |
| `output_item.added` (function_call) → `function_call_arguments.delta` | `ToolCallStarted` / `ToolCallArgumentsDelta` (accumulate per `call_id`) |
| `reasoning_text.done` / `content_part.done` | (part closure; no event) |
| `output_item.done` (reasoning/message) | `ThinkingCompleted` / `TextCompleted` |
| `response.completed` | `UsageUpdated` (with `cached_tokens`), then accumulated `ModelCompleted` |
| `response.failed` / HTTP error | `ModelFailed` |

The emitted sequence is **identical in kind and order** to chat mode for the
same transcript — that parity is a test, not an assumption.

### Completion assembly

Same as chat mode: `ThinkingPart` + `TextPart` + `ToolCallPart`(parsed JSON
args, `{}` on parse failure) → `AgentMessage` → `ModelCompleted`.

## 5. Wire selection

Config (`netpi.provider.aiproxy` section):

```jsonc
"wire": "auto"      // default: prefer responses, per-model fallback
      | "chat"      // force chat completions for all models (rollback switch)
      | "responses" // force responses; unprobed models still fall back on probe failure
```

- **Probe (only in `auto`/`responses`):** during `RefreshAsync`, one tiny
  non-streaming request per model (`input:"hi"`, 10 s timeout, errors
  non-fatal). Success → `ModelInfo.SupportsResponses = true`.
- **Per run:** `SupportsResponses && wire != "chat"` → responses; else chat.
- **Mid-run failure:** a non-2xx from `/v1/responses` triggers one automatic
  retry of the same request via chat completions for that model run (logged);
  subsequent failures go through the normal retry plugin.
- Probe cost is bounded: refresh happens at startup and on model-picker open
  only (§13), off the hot path.

## 6. File-by-file changes

| # | File | Change |
|---|---|---|
| 1 | `src/NetPI.Abstractions/Model.cs` | `ModelInfo` + `bool SupportsResponses = false` (append-only ctor param) |
| 2 | `src/NetPI.Abstractions/ModelEvents.cs` | `UsageUpdated` + optional `int CachedTokens = 0` |
| 3 | `plugins/NetPI.Provider.AiProxy/AiProxyProvider.cs` | catalog probe; `SelectWire`; `BuildResponsesPayload`; `ParseResponseEvent`; fallback retry; chat path untouched |
| 4 | `plugins/NetPI.Provider.AiProxy/AiProxyPlugin.cs` | read `wire` config (default `"auto"`), pass to provider |
| 5 | `tests/NetPI.Host.Tests/ResponsesSseTests.cs` (new) | SSE fixtures replaying the exact observed event sequence (reasoning+text, tool-call args deltas, usage tail, EOF-without-completed, non-200); **parity test** chat-vs-responses → identical `ModelEvent` kinds/order; wire-selection tests (auto+ok→responses URL; auto+fail→chat URL; forced chat→chat URL) |
| 6 | `docs/plans/responses-wire.md` | this document (living spec) |

No changes to `AgentRuntime`, storage, retry, AutoCompact, Web, or UI: they
already consume `ModelEvent`.

## 7. Future phase (out of scope for this implementation)

- `prompt_cache_breakpoint` explicit breakpoints (if nInfer ever implements
  them).
- `cached_tokens` surfaced in the status line / session stats.
- `previous_response_id` + `conversation` objects if nInfer grows stateful
  support.

**`previous_response_id` chaining is now IMPLEMENTED (PLAN §14c, commit
b02935d).** nInfer keys its KV-cache session on `previous_response_id`
(LiveSession retention for a chained responses run vs RecentPrivate for chat
completions), so the provider keeps a per-`sessionId|modelId` chain head and
continuation runs send `previous_response_id` plus only the transcript DELTA
beyond the covered prefix (the server appends the stored chain on top of the
input, so resending covered items would duplicate tokens). `store:true` on
every run. Transcript fingerprints detect edits/compaction (prefix mismatch
→ reset to full input, fresh head); a failed run never advances the head
(the transcript was not mutated). Instructions (system) are always resent;
assistant items keep the server-required reasoning → message → function
calls order. One `function_call_output` item per `ToolResultPart` (and one
tool message per `tool_call_id` on the chat wire). No `AgentRuntime`
changes: the provider owns the head, keyed off the request `SessionId`.

## 8. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Probe cost on slow local models | 10 s timeout, one probe per model, failures non-fatal (flag stays false) |
| nInfer emits non-OpenAI event names / shapes | parser keys on item `type`, ignores unknown events (observed `reasoning_text.delta` divergence already handled) |
| Model rejects `reasoning` input items | one retry without reasoning items, logged once per model |
| `response.completed` missing (stream cut) | treat as the existing partial-completion behavior (chat mode already handles EOF-without-usage) |
| Regression of chat mode | chat code path untouched; `wire:"chat"` is the rollback switch; parity test guards both |
| Config pointing at backend port instead of AiProxy | documented in §2 note; AiProxy owns enrichment/routing by design (§13) |
