# Debug Playbook — netPI ⇄ AiProxy ⇄ nInfer KV-cache correlation

Purpose: when a netPI session shows **slow turns / full prefill** (huge TTFT,
re-prefilling the whole context every turn), this playbook maps the three
diagnostic surfaces, the join keys that stitch them together, and the exact
root-cause signatures. It is written from the 2026-09-21 incident (session
idle for hours → resumed → FULL PREFILL for several turns) and the live
evidence captured during it.

> Verified live on this machine (2026-09-21): netPI diag `:5274` up since
> **06:57 UTC** (host restarted), AiProxy `:8090` up ~5.5 h, nInfer `:8080`
> healthy. The nInfer `/metrics` recent ring held a `124,562`-token prompt with
> `ttft_ms: 39,013` (a full prefill), followed by a turn that reused
> `124,719` prompt tokens (942 ms TTFT). That single capture is the whole
> diagnosis.

---

## 1. Topology (real, verified)

```
 netPI host (C:\AI\Projects\NetPI)
   └─ netpi.provider.aiproxy  →  AiProxy :8090  (single inference endpoint)
        baseUrl in ~/.netpi/config.json = http://127.0.0.1:8090
   └─ netpi.diagnostics  →  own Kestrel :5274  (default; per-plugin "port" override)
        /panel/diagnostics, /api/diag/*
 AiSwitcher/AiProxy (c:\ai\aiswitcher)
   └─ dashboard :8191 (also /aiswitcher/status, /aiswitcher/history on :8090)
   └─ routes /v1/chat/completions + /v1/responses by model field → backend
 nInfer serve (c:\ai\src\ninfer-windows)  :8080
   └─ OpenAI + Anthropic + local state; KV/context-cache engine
```

Ports: netPI diag **5274** · AiProxy **8090** · AiSwitcher dashboard/control
**8191** · nInfer **8080** · (llama local 1234, comfy 8188/8190 not relevant here).

nInfer is started by AiSwitcher from `C:\AI\AiSwitcher\publish\aiswitcher.json`
(`ninferProfiles`). **Profiles matter for forensics:** only `quasar-metrics`
passes `--request-log-jsonl C:\AI\ninfer\req.jsonl`. The other profiles
(`mtp3`, `dflash2`, `quasar2`, `quasar-v3`) run **without** the durable JSONL,
so if the incident happened under one of those, the only durable nInfer record
is the stderr operational log (wherever AiSwitcher pointed it) — there is no
per-request JSONL on disk.

---

## 2. Capability map (what each hop can tell you, and how long it keeps it)

### 2a. netPI (netpi.diagnostics, `:5274`) — read-only

| Endpoint | What | Durability / capacity |
|---|---|---|
| `GET /bootstrap` | health + `startedAt` (**the host/plugin process start — restart detector**) | live |
| `GET /panel/diagnostics` | self-refreshing HTML panel | live |
| `GET /api/diag/overview` | host pid/uptime/runtimeDir, agent `activeSessionId`, plugins, models (catalog `stale`/`lastRefreshedAt`), sessions, bg jobs | live |
| `GET /api/diag/logs?lines=&level=&plugin=&file=` | tail of `~/.netpi/logs/netpi-*.log` (per-day files) | **persistent** (disk) |
| `GET /api/diag/events?limit=&type=&session=` | host-bus `AgentEvent` ring, **filterable by `session`** | **in-memory ring, cap 500** |
| `GET /api/diag/model?limit=&session=` | `ModelRequestDiagnostics` ring (per model request) — **the wire-decision record** | **in-memory ring, cap 500** |
| `GET /api/diag/sessions`, `/sessions/{id}?count=` | session list / entries from the SQLite store | **persistent** (`~/.netpi/netpi.db`) |

`ModelRequestDiagnostics` fields (the ones you actually need for KV problems):
`sessionId, modelId, wireConfig, wireRequested, wireServed, chained, fallback,
failureReason, summary`. Key readings:

- `wireServed=responses, chained=true` → responses wire, sent
  `previous_response_id`, only the delta was sent → **KV chain active**.
- `wireServed=responses, chained=false` → responses wire but **no valid chain
  head** → netPI re-sent the **full transcript** as `input` (a "reset" run) →
  expect a **full prefill** for this request. *(This is the 2026-09-21
  signature.)*
- `wireServed=chat, fallback=true` → responses wire **failed before any
  content** and netPI transparently retried over chat completions, re-sending
  the **full transcript every request**, chain disabled for the run.
  `failureReason` carries the HTTP body (e.g. a 404 `response_not_found`).

**Where the KV chain lives in netPI** — `AiProxyProvider._chainHeads`
(`"sessionId|modelId" → {ResponseId, Covered[]}`), in-memory only. It is
wiped by **any host restart or AiProxy-plugin reload**, and the head resets
whenever the transcript prefix fingerprints no longer match (compaction
rebuilds message ids; new session; edited history). A clean `ModelCompleted`
advances the head; a failed/abandoned run leaves it in place. This is the
single most important fact for the full-prefill problem (see §5).

Persistent netPI record you can always reconstruct from: `~/.netpi/netpi.db`
SQLite — per-entry `CreatedAt` + monotonic `Sequence` + `EntryKind`
(`Message`/`Compaction`/`Metadata`). This is how you locate the **idle gap**
and whether a **Compaction** entry sits at the resume point.

### 2b. AiProxy (`:8090`) — the correlation hub

Per request it builds an `InferenceRequestState` (metadata only, no prompt
text) and stores it in `InferenceRequestStore`:

- **`RequestId`** — AiProxy's own long counter (per-process).
- **`UpstreamRequestId`** — captured from the nInfer **`x-request-id`**
  response header (`req_<32hex>`). **This is the join key into nInfer.**
- `prompt_fingerprint` (SHA of the body), `prompt_bytes`, `route_reason`,
  `alias`, `backend`, `started_at`, `ttft_ms`, `prefill.{percent,
  total_tokens, cached_tokens, processed_tokens}`, `usage.{prompt_tokens,
  completion_tokens, cached_tokens, reasoning_tokens}`, `decode.tps`,
  `finish_reason`, `error`.

Endpoints:

| Endpoint | What | Durability / capacity |
|---|---|---|
| `GET /aiswitcher/status` | backends (+ live nInfer `/metrics` lanes/recent), routes, `queued_requests`, `active_requests`, `last_completed`/`last_terminal` | in-memory; terminal requests retained **200** / **10 min** |
| `GET /aiswitcher/history` | the retained terminal request store (same rich fields) | in-memory, 200 / 10 min |
| dashboard `:8191` (Requests / Performance / System&events) | the above, rendered + WebSocket | live |

Note: the `/aiswitcher/*` recent/active views **drop** nInfer's numeric
`request_id` (they keep `upstream_id` only). To get the numeric id that the
nInfer operational log / JSONL use, hit **nInfer's own `/metrics`** (2c) — its
`recent[]` entries carry **both** `request_id` (numeric) and `upstream_id`
(string). That makes nInfer `/metrics` the string⇄numeric bridge.

### 2c. nInfer (`:8080`) — the KV ground truth

| Endpoint | What | Auth / durability |
|---|---|---|
| `GET /health` | engine readiness | open |
| `GET /metrics` | `lanes[]` (live: phase, prompt/completed/decoded tokens, t/s) + `recent[]` (cap **32**, in-memory: `request_id`, `upstream_id`, protocol, model, finish_reason, prompt/completion/reasoning tokens, total_ms, ttft_ms) + `stats` | in-memory |
| `GET /debug/context-cache?window=ms` | RuntimeStats delta snapshots over a window (default 600000 ms): `selections` (root / private_endpoint / turn_closure / response_replay / long_anchor / shared_stable_prefix / **reused_prompt_tokens**), state moves/forks/restores, KV/state **transfers** (d2h/h2d/d2d), **pressure** (spill, `private_owners_degraded/evicted`, `shared_owners_degraded/evicted`, checkpoints_dropped, searches, budget_exhaustions), **occupancy** (device/host state slots, device KV pages, host_kv_bytes) | in-memory window |
| **stderr operational log** | per request: `req#N done \| openai_responses \| … \| cache X (Y%, <path>) \| TTFT … \| total … \| prefill … tok/s \| decode … tok/s`; also `request_start`, `request_rejected`, failures (phase+class), interval `throughput` | **wherever AiSwitcher pointed stdout/stderr** — not a guaranteed file |
| `--request-log-jsonl FILE` (only `quasar-metrics` profile) | durable full-precision JSONL, schema v21: `request_start`, `request_rejected`, `request_done` (incl. **`prefix_cache_hit_tokens`**, **`prefix_reuse_path`**, computed_prefill, timings, speculative, materialization), `request_error`, `throughput` (same context_cache deltas as `/debug/context-cache`) | **persistent on disk** (only when the flag is set) |
| `GET /v1/responses/{id}`, `…/input_items` | inspect a still-stored local Response (the chain's stored object) | in-memory **LRU store, 1024 records / 256 MiB default** (`--response-store-max-records` / `--response-store-max-mib`); **lost on every process restart** (workload swaps!) |

Two **different** nInfer request ids — do not conflate:

- **`x-request-id`** = `req_<32hex>`, random, public header, =
  `LiveMetricsRegistry.upstream_id`, = AiProxy `UpstreamRequestId`.
- **`request_id`** = `req#N`, per-process monotonic counter (resets each
  process start). Used by the operational log and the JSONL.

`prefix_reuse_path` values you'll see in the `cache … (Y%, path)` field and the
JSONL: `root` (no reuse = full prefill), `private endpoint`, `turn closure`,
`response replay`, `long anchor`, `shared prefix`. A **full prefill** shows as
`cache 0 (0.0%)` or path `root`.

---

## 3. Correlation backbone (the join keys)

```
 netPI sessionId  ──(no HTTP header!)──►  AiProxy request
   join = time window + model + prompt_bytes/fingerprint
              (netPI does NOT tag requests with the session id)
 AiProxy UpstreamRequestId  ≡  nInfer x-request-id  ≡  /metrics upstream_id
 nInfer /metrics recent[i].upstream_id  ⇄  recent[i].request_id  (numeric)
 numeric request_id  ≡  operational log "req#N"  ≡  JSONL request_id
```

So the reliable chain is:

1. **netPI → AiProxy:** match by **wall-clock time** (both are local, ms
   precision), the **model id**, and **prompt size / fingerprint**. The
   `ModelRequestDiagnostics` ring is already `?session=`-filtered, so you get
   the candidate netPI request times; then find the AiProxy request whose
   `started_at` and `prompt_bytes`/`prompt_fingerprint` line up.
2. **AiProxy → nInfer:** exact via `UpstreamRequestId` ⇔ `x-request-id`.
3. **nInfer internal:** `/metrics` `recent[]` maps `upstream_id` ⇔ numeric
   `request_id` → operational log / JSONL. **If the request has left the 32-
   entry recent ring and there is no JSONL, the string⇄numeric link is gone**
   (this is part of what hurt the original incident).

Practical consequence: **the durable anchors are** (a) netPI SQLite +
`netpi-*.log`, and (b) nInfer JSONL **only when the active profile sets
`--request-log-jsonl`**. Everything else is a bounded in-memory ring that the
investigating agent's own model calls will evict.

---

## 4. The ring-buffer trap (why the 2026-09-21 correlation was lost)

Every live surface is a small bounded in-memory buffer:

- netPI `ModelRequestDiagnostics` ring: **500** (`ModelCap`), `AgentEvent` ring
  **500** (`EventCap`).
- AiProxy retained terminal requests: **200** / 10 min.
- nInfer `/metrics` recent ring: **32**.

When an agent spends many turns *investigating*, each turn is itself a model
request, so it continuously **fills and evicts** these rings. The diagnostic
records describing the original prefill incident get pushed out by the
investigation's own traffic before you can read them — exactly what happened.
The fix is procedural, not a bigger buffer:

**Capture evidence first, investigate second.** Before the investigating agent
burns turns, pull the durable + still-live artifacts (§6, "Live capture"
block). Once the durable stuff is on disk, the rings can churn freely.

---

## 5. Root-cause decision tree for "full prefill for N turns"

Start from netPI `GET /api/diag/model?session=<id>` (durable fallback: the
`netpi-*.log` + SQLite) and nInfer `/metrics` `recent[]`.

| # | Signature (what you actually see) | Cause | Fix / note |
|---|---|---|---|
| **A** | First 1–2 turns after resume: `wireServed=responses, chained=false`, nInfer `cache 0 (0.0%, root)` + 39 s TTFT; **then** later turns `chained=true`, nInfer `reused_prompt_tokens≈context`, 1-digit-s TTFT. netPI diag `/bootstrap` `startedAt` is recent (host restarted since the session was last used). | **Host/plugin restart wiped the in-memory responses-wire chain heads.** Resuming the old session had no chain head → one "reset" run re-sent the full transcript → full prefill → new head re-anchored → subsequent turns reuse. **This is the confirmed 2026-09-21 cause.** | Expected, self-healing after 1–2 turns. To avoid it: keep the host up across idle, or treat the one full prefill after any netPI restart as normal. (See §7 gaps for making the chain durable.) |
| **B** | (historical) **Every** turn: `wireServed=chat, fallback=true`, `failureReason` = `HTTP 404 … response_not_found`; nInfer stays on a high prompt with no `previous_response_id` reuse. The chain head points at a stored Response that nInfer **evicted or lost** (LRU eviction — store is 1024 records / 256 MiB, in-process — or a process restart / workload swap). | **Dead chain head → persistent chat fallback.** **FIXED (PLAN §14d, 2026-09-21):** on a pre-content 404 `response_not_found` netPI now drops the stale head and retries ONCE on the responses wire as a reset (no `previous_response_id`, full input, `store:true`) — re-anchoring on the same turn, no chat degradation. Log line: `session <id> chain head is stale (…) ; dropped, re-anchoring via reset on the responses wire`. A stale signature in old logs means the loop ran until that fix. | Nothing to do — the fix self-heals. (Old host builds: break the loop with a new session, a compaction, or an AiProxy plugin reload.) |
| **C** | Repeated full prefills with a **Compaction** entry in the session store at each reset point (`EntryKind=Compaction` in `~/.netpi/netpi.db`). | **AutoCompact** rebuilt the transcript with new message ids → fingerprints changed → chain reset by design (PLAN §14c). | Expected per compaction. Tune `netpi.autocompact` (`reserveTokens`/`keepRecentTokens`) if it's firing too often. |
| **D** | `chained=true` but nInfer `/debug/context-cache` shows `private_owners_evicted`/`shared_owners_evicted`/`checkpoints_dropped` and low `reused_prompt_tokens`, occupancy at host-KV ceiling; `shared_owners_degraded`/`private_owners_degraded` rising. | **Memory pressure evicted the retained KV** (device→host demotion then eviction) despite a valid chain. | Raise `--host-state-slots`/`--host-kv-mib` (see `quasar-v3` profile: `--host-state-slots 16 --host-kv-mib 10240`), or reduce `--max-concurrency`/`--kv-capacity`. |
| **E** | nInfer `recent[]` `prompt_tokens` grows every turn but `finish_reason=stop`, no fallback, `reused_prompt_tokens` healthy. | Not a cache bug — the transcript is genuinely growing (long tools output). | Check tool-result sizes / compaction. |

Distinguishing A from B in one look: **B (pre-fix hosts) keeps `protocol=openai_chat`
(fallback) on every turn and repeats the same 404; A is `openai_responses`, fails to
reuse once or twice, then `reused_prompt_tokens` jumps to ~context and stays.**

---

## 6. Runbook

### 6a. Live capture (do this *before* the investigating agent burns turns)

Run these the moment you suspect a KV/prefill problem; they are read-only and
cheap. Save the outputs to a file in the session (e.g. `~/.netpi/logs/kvcache-
<sessionid>-<date>/`).

```bash
# --- netPI (5274) -------------------------------------------------------
curl -s http://127.0.0.1:5274/bootstrap                 # startedAt = restart detector
curl -s "http://127.0.0.1:5274/api/diag/model?session=<SID>&limit=500"   # wire-decision ring
curl -s "http://127.0.0.1:5274/api/diag/events?session=<SID>&limit=500"  # bus events
curl -s "http://127.0.0.1:5274/api/diag/sessions/<SID>?count=200"        # durable entries
curl -s "http://127.0.0.1:5274/api/diag/logs?plugin=provider.aiproxy&lines=200"

# --- AiProxy (8090) -----------------------------------------------------
curl -s http://127.0.0.1:8090/aiswitcher/status    > status.json   # backends+metrics+active
curl -s http://127.0.0.1:8090/aiswitcher/history   > history.json  # retained terminal (200/10min)

# --- nInfer (8080) ------------------------------------------------------
curl -s http://127.0.0.1:8080/metrics               > metrics.json      # lanes + recent(32)
curl -s "http://127.0.0.1:8080/debug/context-cache?window=1800000" > ctxcache.json
# if the active profile set --request-log-jsonl, it's already durable:
#   C:\AI\ninfer\req.jsonl  (quasar-metrics profile)

# durable netPI side (copy, don't trust the ring)
cp ~/.netpi/logs/netpi-*.log   ~/.netpi/netpi.db   <evidence dir>/
```

Then, **before** you let the agent continue investigating, the durable + ring
snapshots are safe on disk. After that, investigate freely.

### 6b. Correlate

1. In `model?session=<SID>`, find the first turn after the idle gap. Note its
   `wireServed`/`chained`/`fallback`/`failureReason` and its timestamp.
2. Open nInfer `metrics.json` `recent[]`; find the entry whose time/model/
   prompt_tokens matches that turn → read `request_id`, `upstream_id`,
   `ttft_ms`, `prompt_tokens`.
3. Join to AiProxy via `upstream_id` = `UpstreamRequestId` in `history.json` /
   `status.json` active requests (for `prefill.cached_tokens`,
   `usage.cached_tokens`, route/alias, fingerprint).
4. Open `ctxcache.json` around that timestamp: is `reused_prompt_tokens` 0
   (full prefill) or ≈context (cache hit)? What `selections`/`pressure`/
   `occupancy` say.
5. Classify against §5.

### 6c. Forensic (rings already evicted — the post-incident path)

- **netPI SQLite** `~/.netpi/netpi.db`: the session's entries with
  `CreatedAt`/`Sequence` → find the idle gap (Δ between last entry before
  resume and first after) and any `Compaction` entry at the boundary.
- **`~/.netpi/logs/netpi-*.log`** (per-day, persistent): provider warnings like
  `responses wire failed before content … retrying via chat completions` and
  `AiProxy catalog: …` lines, with timestamps. Grep for `fallback`,
  `responses wire failed`, `HTTP 404`, `response_not_found`.
- **nInfer JSONL** `C:\AI\ninfer\req.jsonl` — **only exists if the incident
  ran under the `quasar-metrics` profile.** Grep `prefix_reuse_path` /
  `prefix_cache_hit_tokens` / `request_done`. If the incident ran under
  another profile, the nInfer per-request ground truth was only ever on
  stderr — if AiSwitcher didn't redirect it to a file, it is **gone** (a real
  coverage gap, see §7).
- Reconstruct the "when did the host restart" fact from netPI diag
  `/bootstrap` `startedAt` and/or the `netpi-*.log` first-line timestamp.

---

## 7. Gaps to close (so the next incident is clean)

1. **netPI: persist `ModelRequestDiagnostics` to disk** (append to
   `~/.netpi/logs/` or a small JSONL), not only the 500-entry bus ring. The
   wire-decision record (`chained`/`fallback`/`failureReason` per request) is
   the single most valuable netPI signal for KV problems and today is
   volatile.
2. **netPI: make the responses-wire chain head survive restart.** Persist
   `_chainHeads` (at least the latest `ResponseId` + `Covered` fingerprints per
   `sessionId|modelId`) so resuming a session after a host restart re-anchors
   instead of full-prefilling (root cause A).
3. ~~netPI: validate the chain head before chaining.~~ **CLOSED 2026-09-21 as
   lazy self-heal instead (PLAN §14d):** pre-validation is impossible through
   AiProxy (it 405s `GET /v1/responses/{id}` — only POST generation paths are
   forwarded), so the provider now detects the 404 `response_not_found`
   pre-content failure, drops the stale head, and retries once as a reset on
   the same wire (regression tests:
   `ResponsesSseTests.Chain_StaleHead_404ResponseNotFound_HealsWithResetOnSameWire`,
   `Chain_OtherPreContentFailure_StillFallsBackToChat`).
4. **netPI: tag requests with the session id** (a header) so the
   netPI→AiProxy hop has an exact join key instead of time+fingerprint.
5. **AiProxy: echo nInfer's numeric `request_id`** in `/aiswitcher/*` and the
   dashboard (today only `upstream_id` is kept), so the string⇄numeric bridge
   is available at the proxy without hitting nInfer.
6. **AiSwitcher: point every nInfer profile's stderr at a durable, rotated
   file** (and/or enable `--request-log-jsonl` on all profiles), so the KV
   ground truth survives even when the rings are evicted. Today only
   `quasar-metrics` writes JSONL.

---

## 8. One-screen cheat sheet

```
SLOW/TURN-REFILL PROBLEM
 1. netPI  GET :5274/api/diag/model?session=<SID>        ← wire/chained/fallback
 2. netPI  GET :5274/bootstrap                            ← restart? (startedAt)
 3. ninfer GET :8080/metrics  recent[]                     ← ttft_ms, prompt_tokens, request_id/upstream_id
 4. ninfer GET :8080/debug/context-cache?window=1800000    ← reused_prompt_tokens, pressure, occupancy
 5. aiproxy GET :8090/aiswitcher/history                   ← UpstreamRequestId join + cached_tokens

 REUSE GOOD  = nInfer reused_prompt_tokens ≈ context, ttft small, path private_endpoint/response_replay
 FULL PREFILL= cache 0 (0%, root) + 10s+ ttft  →  check netPI chained=false (A) or fallback+404 (B)
 DURABLE     = ~/.netpi/netpi.db + ~/.netpi/logs/netpi-*.log + C:\AI\ninfer\req.jsonl (quasar-metrics only)
 VOLATILE    = every ring (netPI 500, aiproxy 200/10min, ninfer recent 32) — CAPTURE BEFORE YOU INVESTIGATE
```
