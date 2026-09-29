# Agents: two local slots plus cloud workers

Status: proposed implementation plan; no implementation or deployment performed.
Date: 2026-09-29. Reviewed source baseline: 3ee1c22.

## Objective

Make NetPI predictable with Qwen3.8 on one RTX 5090 serving two simultaneous full-context conversations, plus configured cloud agents. Preserve the small host and existing plugin boundaries. Improve capacity correctness, delegation decisions and visibility before adding more orchestration features.

Read AGENTS.md, README.md, docs/HANDOFF.md, docs/SETTINGS.md (Agents and Budget), docs/TOOLS.md (Agents), docs/PROTOCOL.md, docs/PLUGINS.md and docs/TESTING.md before implementing. Historical rationale is in docs/archive/2026-09-25-agents.md; current source takes precedence.

## Existing behavior to preserve

- A configured agent is a named model with an instance limit, use note, enable switch and optional price/budget. Roles and profiles are separate. Keep the Agents name; lanes are legacy migration terminology.
- Local agents on the same model share its advertised concurrency. NetPI does not load models automatically.
- agent_choices describes available workers; agent_spawn starts one or a batch; agent handles wait/send/list/result/cancel. Keep these three public tools.
- Spawn waits by default; background is explicit. Waiting releases the parent's slot and reacquires with priority. Children have independent sessions and automatic final reports.
- Children receive a self-contained task, not the parent's conversation. They inherit project and default tool selection. Explicit tool selection may include tools the parent lacks; this is an intentional contract, not a sandbox.
- Keep existing durable model-call budget reservations. Do not replace the ledger or weaken admission checks.

The review's live snapshot showed Qwen capacity 2, busy 2, queued 2, and Space Bunny capacity 2, idle, configured for planning/design. This is an observation, not a persistent configuration requirement or a performance measurement. Refresh diagnostics before drawing conclusions about a later run.

## Operating policy

| Situation | Local work | Coordinator |
| --- | --- | --- |
| Everyday task | Main Qwen + one independent helper | Main Qwen |
| Local batch | Two children; parent yields while waiting | Same local parent resumes afterward |
| Larger task | Two bounded local workers | An explicitly selected cloud agent |

Use one Qwen configuration with two instances by default. More named configurations must not create more physical capacity. Extra work queues. Never switch a chat's model or send paid work merely because local slots are busy. Cloud placement follows capability, user policy and budget; retain known-price checks for capped paid calls.

Prefer direct children and bounded independent assignments. Initially implement this as guidance; preserve the existing maxDepth setting and configured values. Do not introduce a hidden global depth change or a permanent reserved coordinator slot.

## Execution order and ownership

One coordinating agent owns integration, contracts and final documentation. Each implementation agent uses its own branch/worktree and explicitly stages only its files. Do not have two agents edit the same files concurrently. Shared test harness changes go through the coordinator.

Run A and B independently. Once A's additive snapshot contract is agreed, run C while A finishes. Integrate A, B and C before D. E is deferred until D demonstrates a need. A local coordinator should use one background local worker, or yield while a batch of two workers runs. A cloud coordinator can supervise both local workers.

### A. Scheduler correctness — first priority

Own: plugins/NetPI.Agents/AgentScheduler.cs and scheduler tests in tests/NetPI.Agent.Tests. Coordinate AgentRuntime/AgentRunner and abstractions changes with the integrator.

1. Reproduce each suspected gap with a deterministic test before changing behavior: missing model concurrency bypassing a shared fallback cap; starvation across named pools on one model; slot accounting during plugin replacement. These are source-review risks, not all proven live failures.
2. Define one effective local resource capacity: valid catalog concurrency, otherwise the existing localSlots fallback, clamped to at least one. Apply it across every pool on that resource, including unconfigured model calls. Keep per-agent instance limits as an additional restriction. Do not automatically merge different provider/model refs without explicit resource identity evidence.
3. Schedule eligible waiters across the shared resource by priority then arrival order. Keep parent-resume priority. Skip pools at their own cap without blocking eligible peers. Document fairness within a priority class and avoid claiming starvation freedom across priorities without a policy/test.
4. Preserve capacity accounting across old/new scheduler overlap. Inspect the actual plugin swap lifecycle. Active old calls must remain counted until they finish or transfer ownership; no third call while two are active. If durable lease ownership needs a stable service, propose the smallest contract-based mechanism before implementing; do not retain plugin-defined objects in host storage.
5. Add shared capacity/busy/queued/availability data to snapshots where needed. Distinguish resource saturation from an individual agent reaching its own limit. Keep existing fields compatible.
6. Correct read-only RPC metadata for agents.list if still missing. The reviewed CLI refused this read-only endpoint without its write flag; verify using rpc.list and the CLI afterward.

Acceptance: cap holds with two named agents, missing concurrency, mixed named/unnamed calls, runtime settings changes and reload during two active calls plus a waiter. Cancellation removes waiters; disable/unload refuses new work; re-enabling recovers; repeated release is harmless. Preserve current compaction slot reuse.

### B. Agent tools and task coordination

Own: plugins/NetPI.Tools.Agents, AgentChoices.cs/prompt guidance in plugins/NetPI.Agents, and dedicated tool tests. Coordinate shared test files with A.

1. Make guidance describe the three operating modes above. Explain available physical capacity, busy versus unavailable, batch spawning, and when background work is useful. Preserve model choice by the owning agent; avoid automatic rerouting.
2. Keep fresh self-contained tasks as default. Guidance should request goal, relevant paths/evidence, constraints, file ownership and a concise final report containing results, changes, validation and blockers.
3. Review all-or-none batch validation, interrupted waits, timeout/report races, resume priority and cancellation propagation. Fix demonstrated defects with tests. Do not promise atomic startup for runtime failures after validation unless implemented.
4. Preserve owner-selected tools, no inherited subagent profile, append-only notices and current budget enforcement. Confirm cloud children cannot inherit a parent's budget override.
5. Add new arguments only for a demonstrated gap. Do not add a second delegation API, autonomous routing service or hardcoded cloud model.

Acceptance: malformed batch starts none; a valid batch of two local children completes while parent yields; background reports are neither lost nor duplicated; user steering reaches the parent when capacity permits; cancel propagates; failures and budget refusals become actionable reports. Guidance must not imply instant preemption of active inference.

### C. Settings and Work visibility

Own: web/src/components/modals/AgentsEditor.svelte, AgentDialog.svelte, relevant Work components and their UI tests. Locate current components before editing. Depends on A's agreed snapshot shape.

1. Show model resource capacity separately from named agent instance limits: for example, Qwen: 2 total, 2 running, 2 queued.
2. Show which tasks hold capacity and why a task waits. Distinguish running, yielded, queued, disabled and model unavailable.
3. Keep instance limits, use notes, prices and budgets editable as today. Explain that several agent entries share the same model capacity. Do not expose old lanes terminology or imply that raising instances increases GPU capacity.
4. Keep narrow panels usable at 230–320 px. Preserve the current saved settings and model selection.

Acceptance: two entries on Qwen never suggest four physical slots; occupancy/queue changes render correctly; offline/disabled/yielded states are distinguishable; existing settings round-trip; focused UI tests and production UI build pass. Commit only relevant generated bundles under repository conventions.

### D. Integration, validation and docs

Owner: coordinator, after A–C.

- Run relevant owning suites, then the normal full build/test gate. Use MockLlm for deterministic concurrency, waits, reports, reload, compaction and budget scenarios. If abstractions changed, rebuild every plugin.
- Test two active workers plus waiting parent; a second foreground chat; nested waits up to configured depth; user interruption; a queued cancellation; settings changes under load; model becoming unavailable; scheduler reload with active streams; concurrent cloud reservations hitting the cap.
- Record observed maximum simultaneous provider calls, not just scheduler counters. Use explicit synchronization in tests rather than timing-only sleeps.
- Update docs/SETTINGS.md, docs/TOOLS.md, docs/PROTOCOL.md and docs/TESTING.md for changed contracts. Update README/HANDOFF only where behavior or decisions changed. Describe policy separately from guarantees.
- Deliver changed files, commits, commands and results, unresolved issues and any migration notes. Inspect the integrated diff and verify branch ancestry after merging.

Builds stay in each worktree's artifacts/dev/app. Do not publish, alter live settings/data, restart NetPI or launch benchmark agents into the user's occupied local slots as part of this handoff. Validate actual Qwen long-context performance in a separately scheduled idle window: compare parent+helper, two workers with yielded parent, and cloud coordinator+two workers. Measure task completion, queue time, first-token latency and cached/uncached input. Do not assume that releasing a scheduler slot preserves backend KV residency.

### E. Optional conversation inheritance — later decision

Only after D: assess whether explicit conversation snapshots improve real tasks over self-contained task briefs. If justified, propose an additive opt-in context mode with a clear snapshot boundary, context-size accounting and provider compatibility rules. Preserve frozen sent history; do not copy transient run state, approval grants or budget overrides. Define how a child role is appended without rewriting the inherited prefix. Keep fresh context as default. No fork implementation is required for A–D completion.

## Definition of done

A–D are integrated with documented tests; two-slot limits survive reload and missing metadata; tools explain practical delegation; Work/settings show truthful shared capacity; reports/cancellation remain reliable; existing budgets and tool-selection semantics remain intact. Any unverified live performance claim is explicitly left unverified. E remains a separate proposal unless the user selects it.

## Dispatch template

Read this plan and repository instructions. Implement assignment <A/B/C> in a separate worktree. Own only the listed files; coordinate contract/shared-file changes with the parent. Reproduce suspected defects before fixing them, preserve documented behavior, add tests for behavior changes, and do not publish or modify live setup. Report your commit, changed paths, tests with results, remaining risks and integration dependencies. The coordinator owns final acceptance and docs.
