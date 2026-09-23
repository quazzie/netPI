# Compaction, tool history, and Responses recovery

Status: **P0 + P1 implemented** (2026-09-23) — §2/§3 (P0) and §4/§5/§6 (P1) are in
place, tested, and the plugins are republished. §3.3 (recover a boundary call from
durable history) is implemented and verified against the REAL incident session: the
read-only §7.8 reconstruction now carries the recovered call (seq 215) immediately
before its retained result (seq 216), so every tool exchange is whole and the input
is valid — no DB edit, no lost neighboring results, no tools run. §4.3 (usage
baseline) and §4.4 (summary-failure safety) are verified/covered (see P1 status).
The plan is complete; no open items.
Investigation completed 2026-09-23.

**P0 status.** §2: `ChooseRetainedFrom` now treats a complete tool exchange as an
indivisible retention unit — the token candidate is snapped backward to the start
of any exchange it would split (new shared definition in
`src/NetPI.Abstractions/TranscriptExchanges.cs`). §3: `TranscriptSanitizer` is
now `Normalize` — a batch-local ordered scan that matches each tool result only
against its immediately-preceding exchange (no global id set), preserves all valid
messages/identities, repairs dangling calls with synthetic interrupted results
inserted directly after the assistant turn, and turns irrecoverable orphan/
duplicate/misplaced results into labeled historical-recovery notes outside the tool
batch. The one shared policy is applied in `AgentRunner.BuildTranscriptAsync`
(direct-store fallback), `AutoCompactService.BuildActiveContextAsync` (checkpoint
reconstruction) and `AutoCompactService.CompactAsync` (automatic compaction return);
an irreparable transcript raises an actionable validation failure instead of
shipping to a provider. §3.3: before that policy degrades a leading orphan result
to a recovery note, `AutoCompactService.RecoverBoundaryCallsAsync` first recovers the
owning boundary call from durable history — a bounded look-back before the retained
boundary, re-including only the call parts that own the leading orphans (original
order, no manufactured calls). A legacy checkpoint that split an exchange therefore
reconstructs whole (call + result together) instead of degrading to a note; clean
checkpoints (no leading orphan) are untouched. Regression coverage:
`TranscriptExchangesTests`, extended `TranscriptSanitizerTests`, the 215/216 +
multi-call incident cases in `AutoCompactTriggerTests`, and the boundary-recovery
cases (`…RecoverTheBoundaryCallFromDurableHistory`, `…IsNotTouchedByBoundaryRecovery`).
Abstractions contract API id changed; plugins republished
(a host running an older contract assembly must be restarted at an idle boundary to
pick them up — §7.7).

**P1 status.**
- §4: `ReadAfterAsync` is an ascending LIMIT, so a bounded call drops the NEWEST
  active-range entries; compaction now pages the COMPLETE active range
  (`ReadFullActiveRangeAsync`). In-run compaction (AgentRuntime) reconstructs the
  durable project snapshot from its persisted `ProjectContext` entry — the retained
  tail (Message entries only) never carried it, so it was silently dropped. Repeated
  compaction no longer concatenates summaries (unbounded); a prior summary is FOLDED
  into the material and re-summarized into one bounded replacement. §4.3: the usage
  baseline is already correct by construction — the runtime resets
  `LastUsageMessageCount`/`LastPromptTokens` at the top of EVERY model request (which
  always precedes the same iteration's compaction check), so a post-rebuild baseline
  is never read by an estimate, and `TokenEstimator.Estimate` counts tool output
  (`ToolResultPart`). §4.4: the summary-failure safety is locked in by the
  `SummaryFailure_LeavesNoCheckpoint_AndOriginalTranscriptIntact` regression — a
  failed summarization throws before the checkpoint is appended, so no broken
  checkpoint is ever persisted and the original transcript stays intact.
- §5: `ResolveChainMode` computes the ACTUAL request mode (chained vs reset) ONCE —
  a head existing is not enough; the payload chains only when it covers the prefix.
  The mode is used for serialization, diagnostics AND recovery eligibility. The
  one-shot stale-chain retry now requires the request to have actually referenced a
  prior response (was chained); `IsStaleChainFailure` classifies on the structured
  code and explicitly excludes `invalid_tool_history`. `Fingerprint` covers a tool
  call's NAME + ARGUMENTS (a same-id content correction flips the fingerprint).
  `ModelRequestDiagnostics` carries the truthful `Mode` + bounded `Reason`. Tests:
  `ResponsesRecoveryTests` (stale-404 one-reset-retry, generic-404 / invalid-history /
  failure-after-content NOT retried, truthful diagnostics) + the fingerprint case in
  `ResponsesIdentityTests`.
- §6: the Diagnostics panel no longer opens the cross-origin host WebSocket. It
  drives same-origin POST control endpoints on the Diagnostics port
  (`/api/diag/reload`, `/reload-all`, `/scan`, `/models/refresh`) — each
  loopback-Host + same-origin-validated (GET → 405), resolving the host
  plugin-facade / catalog contracts through the service registry and ENQUEUING the
  operation (deferred, so a self-reload does not hold the request open). Plugin state
  arrives purely from the same-origin overview poll, which backs off on failure and
  stops on teardown. `docs/web-panels.md` corrected (it claimed cross-origin panels
  may connect to the host WS). Test: `DiagnosticsSurfaceTests.ControlEndpoints_...`.

## 1. Verified incident and corrections

Session `4a775d16e2ae40a09cb664ccef79f062` failed at 10:09:45 +02:00.
The local host log records compaction through sequence 215, retention from
216, then HTTP 400 `invalid_tool_history`. Read-only SQLite inspection confirms:

- 215: assistant `bash` call `call_5974efc7d1f984e555d14fac50f7632d`.
- 216: successful result for that same call.

`AutoCompactService.ChooseRetainedFrom` budgets individual messages without
respecting tool exchanges. `CompactAsync` directly returns summary + tail;
`AgentRuntime` installs it without sanitizing. The existing sanitizer only
fills missing results, using a global set of result IDs; it neither removes
orphan results nor validates their position in the corresponding batch.
This establishes the reported failure mechanism (PLAN sections 31–33 and 46).

The proposed direction needs one correction: moving the boundary BACKWARD
includes the earlier assistant call and the entire result batch. Moving it
forward can only summarize/discard more of the batch; it cannot retain its
earlier call. Treat a complete tool exchange as an indivisible retention unit.
The keep-recent token target is soft; transcript validity takes precedence.

Compaction changes the logical transcript and must reset Responses chaining
(PLAN sections 14c/14d). `BuildResponsesPayload` already compares message
fingerprints: a changed prefix sends full input without `previous_response_id`.
This does not imply a literal purge of all server KV/prefix caches; unchanged
prefixes may still benefit from backend caching. No cache hit is guaranteed.
A normal post-compaction reset should succeed without a 404.

HTTP 404 `response_not_found` is a separate, recoverable stale-reference case
(server restart, eviction, etc.). The provider already attempts one pre-content
reset on the same Responses wire. HTTP 400 `invalid_tool_history` is malformed
input and must not be reclassified as a cache miss or blindly retried.
The exact incident request body was not captured in this investigation;
the chain-reset description above is verified code behavior, not a packet trace.

Diagnostics repeatedly opens `ws://<hostname>:5173/ws` from port 5274 and retries
on close. The host log confirms rejected upgrades from that origin. The host's
same-origin rejection is correct; the panel client and its documented connection
model disagree with the control boundary.

## 2. P0 — Make retention boundaries batch-aware

Primary files: `plugins/NetPI.AutoCompact/AutoCompactPlugin.cs`, shared transcript
helpers in `src/NetPI.Abstractions`, and context-read code where needed.

1. Represent legal cuts over messages ordered by persisted sequence. A batch is
   an assistant message containing tool calls plus its contiguous matching result
   messages; support several calls/results per message and several result messages.
   Non-message storage entries do not themselves break a tool exchange.
2. Calculate the token candidate, then move backward to the start of any exchange
   crossing that cut. Verify call IDs and ownership, not just the Tool role.
   Recalculate summary range and estimates from the final boundary.
3. Keep the newest exchange complete even when it exceeds `KeepRecentTokens`.
   If retaining a huge exchange prevents sufficient reduction, summarize that
   entire completed exchange as an explicit fallback, allowing a summary-only
   context. Never split it or silently drop its output. If no valid reduction fits,
   return a clear context-capacity failure without persisting a broken checkpoint
   or looping indefinitely through ineffective compactions.
4. Handle read-window edges: use bounded backward lookups to recover the owning
   call when an initial history page begins mid-batch. If the call is genuinely
   unavailable, use the recovery policy in P1 rather than inventing a call.
5. Validate the constructed context before appending the checkpoint. Persisted
   sequence boundaries must describe the actual retained source range; synthesized
   repair messages are not assigned fictitious source sequences.

Acceptance: the incident boundary becomes 215, with the complete call/result
retained. Multi-call batches remain complete on both sides of every legal cut.

## 3. P0 — Normalize rebuilt transcripts and recover old checkpoints

Primary files: `src/NetPI.Abstractions/TranscriptSanitizer.cs`, AutoCompact,
`plugins/NetPI.Agent/AgentRunner.cs`, `plugins/NetPI.Agent/AgentRuntime.cs`.

1. Replace the global resolved-ID heuristic with a batch-local ordered scan.
   Preserve all valid messages and identities unchanged. Match each tool result
   to an outstanding call in its immediately preceding exchange; consume each ID
   once. Do not accept a result merely because that ID occurs elsewhere.
2. Retain existing synthetic interrupted-error semantics for genuinely missing
   results, inserting them within their exchange before subsequent conversation
   messages. Never claim the tool did not execute or automatically replay it.
3. For legacy damaged history, first recover a boundary call from durable history
   when possible. For irrecoverable orphan/duplicate/misplaced results, remove the
   invalid structured result from model input and preserve useful output as a
   clearly labeled, deterministic historical-recovery note outside the tool batch.
   Do not manufacture an assistant call. Ambiguous/invalid call IDs that cannot be
   repaired without guessing must yield an actionable local validation failure.
4. Keep repairs idempotent and deterministic, including note/synthetic IDs. Do
   not rewrite original database messages or checkpoints merely to sanitize them.
   Recovered calls from before an old checkpoint may overlap its summary; document
   this bounded compatibility behavior and avoid re-execution.
5. Apply one shared normalization policy to automatic compaction returns, manual
   compaction, checkpoint reconstruction, and the runner's direct-store fallback.
   Validate the runtime's installed rebuilt transcript before its next request;
   normalization must operate on the complete transcript, not a chained wire delta
   whose call legitimately resides in server-held history.
6. Log repair category and counts with session/checkpoint identity; avoid dumping
   complete tool output. Clean transcripts should produce no repair noise.

Acceptance: resuming the existing broken checkpoint produces valid model input
without editing the database, losing valid neighboring results, or running tools.
Sanitizing twice has identical output and stable fingerprints.

## 4. P1 — Verify the whole reconstruction contract

Adjacent issues found during code inspection need explicit regression coverage:

- Checkpoint reads use `ReadAfterAsync(..., MaxContextMessages)`, whose ascending
  LIMIT can omit the newest entries when the active range exceeds the cap. Page
  the complete active range for compaction, or introduce explicit safe reduction;
  never silently replace current history with its oldest bounded portion. Apply
  batch-safe edges to recent-history reads without a checkpoint as well.
- Run-start reconstruction explicitly restores the active project instruction
  snapshot; the in-run compaction path only prepends the first system message.
  Share reconstruction of the run prompt, summary, retained tail, and durable
  project snapshot across both paths. Verify pending project changes still apply
  only at their safe boundary and appear exactly once (astra-1 D).
- Usage message counts originate from the runtime transcript, while compaction
  estimates use stored Message entries excluding summary/system/project entries.
  Align the usage baseline with the exact context measured, and invalidate stale
  usage after a rebuild until fresh provider usage arrives. Verify added tool
  output is counted and repeated compaction does not grow old summaries without
  bound; re-summarize the prior summary with newly summarized material into a
  bounded replacement rather than indefinitely concatenating summaries.
- Ensure cancellation or summary failure leaves the last usable checkpoint and
  original transcript intact. Summary calls must remain independent of the main
  session chain and retain existing lane/cloud admission guarantees.

These are related hardening items, not additional proven causes of this incident.

## 5. P1 — Make Responses reset and recovery observable and precise

Primary file: `plugins/NetPI.Provider.AiProxy/AiProxyProvider.cs`.

1. Preserve prefix-based automatic reset: valid compacted full input, all effective
   instructions, no prior response ID, then a new head only on success. Keep the
   existing wire-only continuation anchor when no user query survives.
2. Compute the actual request mode once and use it for serialization, diagnostics,
   and recovery eligibility. Currently diagnostics infer `chained` from existence
   of a head even when the payload's prefix check chooses reset.
3. Allow the existing one-shot stale-chain retry only when that actual request
   referenced a prior response and emitted no content. Classify structured error
   codes/status (and the supported missing-prior-query compatibility case), not
   arbitrary 404s or broad substring matches alone. Never replay after content,
   switch to chat, or retry invalid tool history as stale state.
4. Record reset/chained mode and bounded reason such as prefix changed or stale
   reference. Avoid describing a request as a cache hit from chaining alone.
5. Audit fingerprints for fields affecting wire history: tool-call name and
   arguments are currently omitted. Cover them so a same-ID content correction
   cannot incorrectly reuse an obsolete prefix. Preserve stable identities for
   unchanged reconstructed history.

Acceptance: compaction sends a successful reset directly; a genuine stale chained
request gets at most one reset retry; invalid history gets no cache-recovery retry.

## 6. P1 — Repair Diagnostics controls without weakening origin checks

Primary files: `plugins/NetPI.Diagnostics/DiagApp.cs` and
`plugins/NetPI.Diagnostics/panels/diagnostics.html`.

1. Remove the cross-origin host WebSocket and hardcoded host port. Read plugin
   state through the panel's existing same-origin overview endpoint and bounded
   refresh mechanism; ensure any new polling stops on teardown and backs off on
   service failures.
2. Add narrowly scoped same-origin POST endpoints for reload, reload-all, scan,
   and model refresh. Resolve host/plugin/catalog contracts through the service
   registry and reuse existing operation semantics, including deferred reload.
   Do not add plugin-assembly references or a general command proxy.
3. Enforce loopback Host and same-origin Origin on these new control endpoints,
   matching the established control-boundary policy at the Diagnostics port.
   Mutations must reject GET and foreign origins. No permissive CORS or origin
   allowlisting on the host WebSocket; keep the panel bridge navigation-only.
4. Surface disconnected, failed, deferred, and completed actions accurately.
   Reloading Diagnostics itself must not depend on holding a request open while
   its own generation drains: acknowledge scheduling and observe the next state.
5. Correct `docs/web-panels.md`, which currently claims cross-origin panels may
   directly connect to the host WebSocket despite `docs/protocol.md` forbidding it.

Acceptance: Diagnostics actions work at configurable ports, the rejected-upgrade
loop disappears, and hostile-origin control requests remain rejected.

## 7. Verification and delivery order

1. Add regressions reproducing the 215/216 split and legacy checkpoint resume.
   Extend AutoCompact, ContextReads, and TranscriptSanitizer tests with multiple
   calls/results, partial interruption, orphan/duplicate results, tiny budgets,
   oversized exchanges, storage-page cuts, interleaved metadata, and idempotency.
2. Add an agent-loop integration test: execute tools, force real compaction,
   serialize the next Responses request, validate batch ownership/order, return
   model success, and verify each side-effecting tool executed only once. Include
   manual compaction and restart reconstruction equivalence.
3. Extend ResponsesSse/ResponsesIdentity tests for direct post-compaction reset,
   summary-only reset anchor, actual 404 `response_not_found`, generic 404, invalid
   history 400, failure after content, failed reset, new-head continuation,
   fingerprint changes, and truthful request-mode diagnostics. Exercise both
   Chat and Responses serialization of repaired complete batches.
4. Add reconstruction tests for active-range overflow, project snapshots, usage
   alignment, repeated summary bounding, cancellation, and summary failure.
5. Test Diagnostics controls and origin rejection with the same rigor as
   ControlBoundaryTests, including non-default ports and self-reload scheduling.
6. Run targeted tests while implementing, then `dotnet build NetPI.sln` and the
   full host test suite. Build the Svelte frontend only if frontend files change.
   Update AGENTS.md and relevant deep docs for the final behavior (PLAN sections
   14c/14d, 31–33, 46; protocol and panels); mark this plan's completed steps.
7. Publish through immutable artifacts. An Abstractions implementation change
   requires rebuilding/staging the host and restarting at an idle boundary because
   its contract assembly is permanently loaded; plugin reload alone is insufficient.
   Republish affected plugins against that build. Plugin-only subsequent changes
   can use normal reload policies. Verify identity/build IDs before smoke testing.
8. Smoke-test on a disposable session with forced compaction and a real provider.
   Verify no orphan outputs, expected reset then chaining, and working Diagnostics
   controls without rejected upgrades. For the affected real session, inspect
   reconstruction without issuing tools; resume work only when separately requested.

Existing unrelated working-tree changes must be preserved. This plan does not
change Nudge's retry limit: the two logged nudges are a separate bounded empty-turn
behavior, not evidence that compaction failed. Diagnose that separately only if
empty turns remain problematic after transcript correctness is restored.
