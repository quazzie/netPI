# astra-2 — Configurable agent lanes, delegation, and work overview

Status: **substantially implemented** — packages A–F are in place (contracts +
persistence, strict local lanes following AiProxy capacity, child/delegation,
mailboxes + waits, the combined Work panel, orchestrate-mode persistence, and the
cloud budget/gate). §13/§16: full capacity is an accepted queue (a durable `Queued`
assignment + ACK, never a rejection) and `agent.cancel` handles queued/suspended
records — implemented and tested. §15.B: the runtime re-validates a pooled run's
lane permit against the scheduler before EVERY model call (in-run compaction
included) and fails closed on a stale/foreign permit — implemented and tested.
§3.3: the catalog wire-capability probe is now LAZY — negotiated once on the
owner's first real request (inside the run, where a pooled run's permit was just
re-validated), with the verdict cached so later runs never re-probe; a catalog
refresh never probes — implemented and tested. Remaining open gates: the
`agent_lane_journal`/`agent_checkpoints` tables are declared but never written,
mailbox drain at turn boundaries, crash-after-tool-effect quarantine, workspace
modes, and orchestrate-mode coordinator guidance.
Consolidated 2026-09-22 from the user's decisions and a targeted inspection of the
current repository.

This document is an implementation specification for local implementors. Complete
and verify the packages in §15; do not interpret examples as existing APIs. The
requirements in §1–3 take precedence over earlier conversational proposals.
In particular, **request-scoped admission and round-robin agent scheduling were
explicitly rejected**. AiProxy availability is useful information, but netPI must
enforce agent-lane ownership itself, independently of the inference engine.

The user authorized design deviations that better serve the intended workflow.
One deliberate refinement below is to consolidate the two existing work views
under Activity's presentation ownership, while preserving the Background panel's
identity and BackgroundTasks' process-management responsibility (§12).

Related references: [astra-1](astra-1.md), [Responses wire](responses-wire.md),
[plugin architecture](../plugin-architecture.md), [protocol](../protocol.md),
[web panels](../web-panels.md), [publication](../launch-and-publish.md), and
[original design](../archive/PLAN-v1.md), especially PLAN §6, §10–14, §27–33,
§36/§41, and §44–45.

## 1. Accepted scope and priorities

The initial deployment is the local `qwen3.8-27b` model, currently configured for
**two lanes**. The accepted capacity policy is **Follow AiProxy**, with an optional
user cap. Two is an initial backend setting, not a permanent netPI limit. If the
active backend is reconfigured for four simultaneous agents, netPI adopts four
after validating that configuration; a fifth waits under the same ownership rules.
The NUC/small-model pool is excluded from the initial configuration and delivery:
the user considers those models unreliable. Do not implement Yue2 coordination
as a prerequisite. Future pools remain configurable.

The user wants:

1. At most N admitted local agent assignments, where N follows the active
   deployment's total capacity reported by AiProxy (optionally capped by the user).
   With the initial N=2, a third waits until an owner
   finishes, is cancelled and drains, or deliberately hands off/surrenders its
   lane. Tool calls, retry backoff, and normal model-turn boundaries retain it.
2. Independent child contexts, bounded assignments, agent-to-agent messages,
   completion notifications, and collaborative task planning.
3. Explicit suspension/delegation on the parent's pool: the child performs the
   detailed work and the parent resumes with a summary, without copying every
   child step into the parent transcript.
4. An optional orchestrate mode in which the main agent plans, delegates,
   waits, reviews, and integrates work using the same lane machinery.
5. Ordinary cloud sessions can run **directly, outside lane pools**, when the user
   enables/selects a cloud model. Optional cloud pools remain an advanced policy,
   not a prerequisite for cloud chat. No automatic spending because local is busy.
6. The existing **Background** right-panel plugin becomes a combined overview of
   background processes and agents, including queued/waiting/suspended agents.
   Both lists are newest first. Clicking an agent opens/selects its chat tab.

Cloud does not change the local pool's capacity. A direct cloud session or an
explicitly configured cloud pool never consumes or creates a local lane. With the
initial N=2, two local workers and a direct cloud chat/coordinator can run together.
With only local execution enabled, the backend-derived N bounds executing agents.

This is a plan-writing task only. Implementors must not treat this document's
publication or deployment instructions as evidence those actions already ran.

## 2. Terms and ownership

| Term | Meaning |
|---|---|
| Session | Persisted conversation, independently openable in a normal chat tab |
| Agent | Stable participant identity backed by one session; root or child |
| Assignment / logical run | One accepted piece of work; retains one RunId across suspension/resume; a later follow-up is a new run |
| Execution segment | The actual runtime invocation between admission and completion/suspension; several may belong to one logical run |
| Team | Root agent plus descendants, shared task board, and policy/budget scope |
| Deployment | A concrete provider route/model selection; initially an AiProxy model ID |
| Pool | A configured capacity shared by all deployments using the same inference resource |
| Lane | One netPI-owned slot in a pool, e.g. `local-big/1`; not an nInfer slot ID |
| Lane ownership token | Internal, fenced authority for one assignment to use a lane; not a model-visible argument |
| Direct cloud execution | Normal tracked agent execution on an enabled cloud deployment, with no lane/pool ownership; subject to cloud permission/budget/provider limits |
| Profile | Optional capability/preferences/tool policy for an agent; creates no capacity |
| Mailbox | Durable addressed messages with an ordered consumption cursor |

A parent/child tree defines ownership and cancellation scope. Messaging can form
a graph within the team. Task dependencies form a directed acyclic graph. These
are distinct relationships; peer communication does not change parentage.

Every root chat submission and child uses the same trusted execution-policy
resolver: a pooled deployment requires lane admission; an explicitly configured
direct cloud deployment requires cloud authorization without lane admission.
Opening a tab is observation, not admission. Closing a tab is not cancellation.
Agents can outlive the browser connection and remain inspectable afterward.

## 3. Hard scheduling invariants

### 3.1 Assignment-held lanes

For every pool P, admission must never create more ownership tokens than its
validated effective capacity N. A later capacity reduction may leave existing
owners above the new target temporarily: report draining and admit nobody until
the count drops below that target (§5.3). Do not evict or rotate existing owners.
Each assignment owns at most one lane and submits at most
one model request at a time. Count owners, not current HTTP requests.

An owner keeps its lane during:

- Context preparation after admission, model streaming, and tool batches.
- Shell execution, including slow foreground commands.
- Retry delays and automatic in-run nudges.
- In-run compaction and other explicitly accounted owner maintenance.

There is no time slice, idle timeout, token quantum, or priority preemption that
temporarily inserts a third agent between turns. Low utilization while an owner
runs tools is an accepted tradeoff for preserving working contexts.

Queued agents are stored work records: they execute neither model calls nor
agent tool batches. They need not retain live runtime tasks, provider leases,
or contexts in memory. Queueing many records is not permission to round-robin
their execution. Bound the queue and team size independently of lane capacity.

Normal admission is FIFO by durable ready-sequence within the allowed pool;
explicit handoff continuations (§6) are the defined exception. No automatic
migration between pools/models to escape a queue. Return queue reason and
position as an estimate, not a guaranteed start time.

### 3.2 Example trace that must pass

```text
lane 1: A model -> A tools -> A model -> A retry -> A model -> A done
lane 2: B model -> B tools --------------------> B model ---------->
queue : C ------------------------------------------------> C admitted
```

C must make **zero** inference requests and run **zero** agent tools before A
releases lane 1. Even if B is in a long shell call, C cannot borrow lane 2.
After C is admitted, A is terminal; a new user follow-up to A is a new queued
assignment, not an entitlement to interrupt C.

### 3.3 Enforcement boundary and fencing

Enforce admission inside netPI, not in prompts, the frontend, AiProxy's queue,
`previous_response_id`, or backend-reported free slots. Before every model call,
validate an internal execution permit against deployment, RunId, SessionId and
host generation. A pooled permit additionally requires pool/lane/owner epoch;
a direct-cloud permit requires enabled deployment, task allowance and budget
authority. Reject missing/stale/mismatched permits before network I/O. Direct
execution is a different authorized policy, not an exception that bypasses checks.

Do not trust model-supplied agent IDs for authority. Runtime-created ToolContext
identity supplies the caller; a tool may name a target but cannot impersonate it.
Cancellation does not free a lane until its provider stream and foreground tool
batch have actually unwound. A stuck call is visible as stuck/cancelling and
retains ownership; never expire a live token and admit another agent on top.

All routes to inference must be audited: ordinary chat, child runs, CLI runs,
retries, compaction, manual compaction, diagnostics probes, and catalog wire
capability probes. Model-list HTTP requests are metadata and need no lane;
an inference-based capability probe does. Prefer lazy wire negotiation on the
owner's first real request over eager background inference during catalog refresh.

In-run maintenance is serialized under its owner and cannot launch an independent
helper agent. A compaction summary may deliberately change that owner's working
context; isolate its provider chain from the main conversation and record the
maintenance call. Do not run unrelated summaries/probes as untracked third
conversations. Manual maintenance for an idle session queues as an assignment.

### 3.4 netPI and the proxy have different jobs

netPI owns admitted agent count and assignment lifecycle. In Follow AiProxy mode,
AiProxy supplies the active deployment's total concurrency, which sets netPI's
capacity target. Its instantaneous free-slot report cannot release/reassign an
owned netPI lane. AiProxy may also queue requests or coordinate hardware.
Unknown/stale capacity is not extra capacity: hold new admission until refreshed
or explicitly overridden. A model being unloaded is not automatically unavailable:
some backends load on demand.

The guarantee covers requests made through this netPI instance. Other applications
calling the backend can still affect its cache; do not claim global exclusivity
unless an external admission authority is later configured. No engine-specific
reservation or cache API is required for correctness.

## 4. Current implementation and concrete integration points

Repository-relative paths below are implementation targets, not new cross-plugin
assembly dependencies.

| Current file / surface | Required direction |
|---|---|
| `src/NetPI.Abstractions/IAgentRunner.cs` | Extend lifecycle/query contracts for queued and suspended logical runs; retain one active logical assignment per session |
| `plugins/NetPI.Agent/AgentRunner.cs` | Replace whole-run busy rejection with durable admission/queueing; track every execution task, not only `_runTask` |
| `plugins/NetPI.Agent/AgentRuntime.cs` | Explicit execution-segment outcome; safe suspension after a persisted complete tool batch; internal permit on all inference |
| `plugins/NetPI.Agent/AgentPlugin.cs` | Register runner integration; stop/await every segment; migrate `maxConcurrentRuns` semantics |
| `src/NetPI.Abstractions/Model.cs` | Add execution authority and deployment identity without exposing engine-specific cache concepts to scheduling |
| `src/NetPI.Abstractions/Tools.cs` | Trusted AgentId/RunId/TeamId/tool-call identity and typed runtime control directives |
| `plugins/NetPI.Provider.AiProxy/AiProxyProvider.cs` | Preserve Responses chain behavior; validate permits; eliminate unadmitted inference probes |
| `plugins/NetPI.AutoCompact/AutoCompactPlugin.cs` | Owner-scoped maintenance and queued manual compaction; no admission bypass |
| `plugins/NetPI.Storage.Sqlite/SqliteSessionStore.cs` | Versioned schema migration plus orchestration transactions through a dedicated store implementation |
| `plugins/NetPI.Web/WebApp.cs` | Accepted-but-queued chat, lifecycle snapshots, targeted cancellation, busy-session checks |
| `plugins/NetPI.BackgroundTasks/BackgroundTasksPlugin.cs`, `BgWebApp.cs`, `panels/background.html` | Retain process ownership/APIs; transfer the `background` panel registration and presentation to Activity |
| `plugins/NetPI.Activity/ActivityPlugin.cs`, `ActivityWebApp.cs`, `panels/activity.html` | Own the combined Work panel, reuse existing agents/process UI, query the canonical lifecycle projection |
| `web/netpi-web/src/App.svelte`, `components/RightPanel.svelte`, `ws.ts` | Harden/reuse panel navigation, select correct tab without automatic background navigation |
| `web/netpi-web/src/store.svelte.ts`, `types.ts`, `components/SessionTabs.svelte` | Session-scoped queued/waiting state and tab indicators |

Findings to account for rather than copying existing limitations:

- The runner currently rejects once `maxConcurrentRuns` is reached. Its run
  registry supports concurrent runs, but `_runTask`/shutdown still need a complete
  ownership audit before supporting suspension and multiple live segments.
- AgentRuntime holds self/provider/tools/catalog/store leases throughout a run.
  A durable suspension must unwind that segment and release those leases.
- The provider currently keys Responses chain heads by `sessionId|modelId`.
  Children need independent session identities, never the parent's chain head.
- The Background API sorts jobs by start time, but its HTML re-groups running
  jobs ahead of completed jobs. That violates the requested strict newest-first
  ordering and must change.
- Activity already lists runs and processes and posts
  `netpi.activity.openSession`. App.svelte handles it through `ws.openSession`.
  That listener currently checks the message type but not origin/source.
- `docs/web-panels.md` still says there is no postMessage bridge, despite that
  implementation. Correct the documentation when shipping the panel package.
- Web session-deletion and other busy checks need per-session logical lifecycle
  checks; a representative global active session is not sufficient.

## 5. Architecture, contracts, and configuration

### 5.1 Service ownership

Keep all contracts and immutable DTOs in `NetPI.Abstractions`. Add two focused
plugins; they must not reference Agent, Web, SQLite, or each other's assemblies.

| Owner | Proposed service | Responsibility |
|---|---|---|
| `NetPI.Lanes` | `lanes` / `ILaneScheduler` | Pool registry, atomic admission, ownership tokens, ordered queue, explicit handoff, permit validation |
| `NetPI.Orchestration` | `orchestration` / `IAgentOrchestrator` | Team/agent lifecycle, tools, mailbox/wake rules, task dependencies, budget policy |
| `NetPI.Storage.Sqlite` | `orchestration-store` / `IOrchestrationStore` | Durable records and transactional lifecycle transitions alongside sessions |
| `NetPI.Agent` | Existing `runner` | Execute admitted segments; report yielded/terminal outcome to the orchestrator |
| AiProxy provider | `provider-capacity` / `IProviderCapacitySource`, plus availability hints | Supply active deployment's validated total concurrency and freshness for Follow AiProxy; no agent ownership decisions |
| `NetPI.BackgroundTasks` | Existing process services/APIs | Continue process ownership, output, and stop behavior |
| `NetPI.Activity` | Combined Work panel | Query orchestration/process services; never own/advance agent execution |

Interface names may be refined once before implementation. Preserve the ownership
boundaries. Publish changes through the existing event bus only after storage
commits. Use short service leases for reads and mutations; retain execution leases
only while a segment is actually live. Resolve services lazily so startup order
is not a new hardcoded plugin dependency graph.

ILaneScheduler needs operations equivalent to enqueue, acquire ready work,
validate ownership, release after drain, hand off, list snapshots, and drain a
pool. IAgentOrchestrator needs submit/spawn, message, wait registration,
resume/follow-up, cancel, query, and segment-completion handling. Runtime commands
must delegate here rather than maintain a second queue.

One serialized scheduler state machine plus transactional compare-and-swap storage
updates is sufficient; do not build a distributed scheduler. Establish one lock
order and avoid holding a session gate while awaiting another session, an agent
result, provider I/O, or a plugin reload. Use internal ownership epochs to reject
late callbacks and double releases.

### 5.2 Initial configuration example (new schema)

```json
{
  "plugins": {
    "netpi.lanes": {
      "schemaVersion": 1,
      "enabled": true,
      "deployments": [
        {
          "id": "local-qwen",
          "providerService": "provider",
          "modelId": "qwen3.8-27b"
        }
      ],
      "pools": [
        {
          "id": "local-big",
          "enabled": true,
          "capacity": {
            "mode": "provider",
            "sourceService": "provider-capacity",
            "maxAgents": null,
            "unknownPolicy": "hold-new"
          },
          "admission": "assignment",
          "residency": "single-deployment",
          "deploymentIds": ["local-qwen"]
        }
      ],
      "maxQueuedAssignments": 128
    },
    "netpi.orchestration": {
      "enabled": true,
      "defaultPool": "local-big",
      "defaultMode": "chat",
      "maxAgentsPerTeam": 12,
      "maxDelegationDepth": 3,
      "maxOutstandingMessagesPerAgent": 100,
      "cloudAllowedByDefault": false
    }
  }
}
```

The numeric safety bounds above are proposed defaults, not model capabilities.
Make them configurable and validate positive bounded values. Pool/deployment IDs
are stable and unique. Declare deployment `executionMode` as `pooled` (the default
for the local example) or `direct-cloud`. A pooled deployment maps to exactly one
resource pool; direct-cloud has no pool membership. Aliases for the same backend
must not manufacture independent capacity or relabel the local backend as direct
cloud. Resolve execution policy from trusted configuration, not a model/tool
argument or a name containing "cloud". Initially bind
`local-qwen` to a stable concrete route, not a policy alias that can secretly
switch to another backend or a paid model.

Each deployment records local/cloud billing class, capabilities/context limits,
and route identity as appropriate; validate those against configured policy and
provider metadata. Profiles can prefer a deployment but cannot override its
execution policy. Reject models with no configured execution policy; never fall
back to the first model in the catalog or silently run outside authorization.

When lane mode is enabled, `netpi.agent.maxConcurrentRuns` is superseded by pool
capacity for pooled execution, with a migration diagnostic explaining that
queued/suspended records and direct-cloud runs do not count as pool owners.
Do not retain a hidden global limit of one or two that also blocks direct cloud.
Legacy mode
may retain the old behavior only when lane mode is explicitly off and orchestration
is disabled. A missing lane service must fail closed for pooled execution;
direct-cloud authorization must not acquire a lane or depend on pool availability.
A missing required orchestration/authorization service fails closed for the
execution it governs while keeping history/UI usable.

Configuration changes are atomic. Disable means drain: no new owners, current
owners may finish. Reducing capacity below live ownership stops new admission
until enough owners finish; do not evict them. Display live ownership and the new
target distinctly (e.g. `4 owned / target 2 — draining`). Removal waits for
references to drain; queued work gets an explicit blocked reason. Never silently
move it to a different model.

### 5.3 Discovery versus user-owned lane policy

AiProxy supplies discovery: model IDs, capabilities/context limits, routes/backend
identity where exposed, availability and reported total concurrency. netPI stores
the user's execution policy in `~/.netpi/config.json` (or NETPI_HOME's config):
resource-pool membership, capacity source/optional cap, enabled state, residency
policy, and pooled/direct-cloud classification. Follow AiProxy explicitly authorizes
capacity changes for an already configured pool/binding. Discovery must not create
or enable unrelated pools, change deployment membership, or authorize cloud spend.

Provide a compact Settings execution section using provider-discovered choices:

| Field | Example / semantics |
|---|---|
| Pool name / stable ID | Local GPU / `local-big`; identifies a resource, not nInfer or a model name |
| Model / deployment | Discovered concrete AiProxy route, initially qwen3.8-27b |
| Lane capacity | Follow AiProxy (default); currently 2, automatically 4 if the validated backend reports 4 |
| Optional maximum | None by default; a user value such as 2 caps provider-derived capacity |
| Reported backend concurrency | Read-only observed limit with source and age, or Unknown |
| Enabled | User-controlled; disabling drains current work |
| Model residency | Single deployment for this local resource |

For `capacity.mode=provider`, compute N from a fresh trustworthy **total** backend
capacity, limited by `maxAgents` only when that optional cap is set. Increases from
2 to 4 allow two additional owners; a fifth remains queued. Reductions from 4 to 2
stop new admission until fewer than two owners remain; let existing owners finish.
This never changes ownership at a request/tool boundary or time-slices agents.

Never derive N from instantaneous free slots, active-request count, model size,
context-window size, observed throughput or the number of entries in `/v1/models`.
Context size and configured total concurrency are separate provider facts.

Unknown/stale/missing capacity uses `hold-new`: existing owners may finish on their
unchanged deployment, but no new ownership or handoff admission occurs until a
fresh valid observation arrives. Show the last known value with its age and an
explicit blocked reason; do not silently substitute 1, 2 or unlimited. Persist
last-known information for display, not as authority to admit after a restart.
A user may explicitly select manual mode with a validated positive `agents` value;
that is a fallback/override, never an automatic switch away from Follow AiProxy.

IProviderCapacitySource should report deployment/backend identity, total concurrency
or Unknown, observation time, freshness/validity, and a configuration/binding
revision when available. Poll metadata at a bounded interval and refresh on model
catalog/binding changes; no inference probe is needed. Expose current capacity and
the optional cap independently in lane snapshots so UI changes are explainable.

The current AiProxy status route has slot-related fields, but implementors must
verify that a stable configured total is available while idle for each adapter.
Do not use a conditional live-metrics field as if it were always authoritative.
If absent, add a small backward-compatible metadata field in AiProxy (with its
own repository instructions/tests), or show Unknown until that contract exists.
Do not claim current API support for a route revision or hardware-resource ID
without checking it. Follow AiProxy is the delivery default, so wire this source
before enabling the feature rather than leaving it an optional future integration.

Configure grouping explicitly where discovery cannot identify shared hardware:
nInfer and llama.cpp on the same local GPU belong to the same pool, even if AiProxy
gives them different backend IDs. netPI remains responsible for enforcing N;
changing inference engines does not replace or bypass its scheduler.

### 5.4 Replacing nInfer/model without replacing the lane design

The provider endpoint remains AiProxy. The lane scheduler knows deployment/resource
identity and capacity, not the backend implementation. Supported replacement flow:

1. Request a new model/deployment binding for the existing Local GPU pool in
   settings. Show the proposed model, backend, context window and total concurrency.
2. Stop new admission to that pool and let its current assignments finish, or
   explicitly cancel them and wait for their operations to drain. Do not switch
   the actual upstream engine out from under those owners.
3. Configure/start the replacement backend using the existing AiProxy/AiSwitcher
   management path. netPI does not start/stop inference engines as a side effect
   of selecting a model. Refresh discovery and validate the resulting binding.
4. Activate a versioned deployment binding atomically once the pool is drained.
   Keep pool ID, Follow AiProxy policy and any optional user cap. A replacement
   reporting four usable slots yields four lanes by default; one yields one.
   With an explicit cap of two, those cases yield two and one respectively.
5. New assignments use the new binding. Previously queued assignments retain
   their accepted model and show `deployment replaced` unless the user explicitly
   retargets them. Retargeting revalidates context/tool capabilities and budgets;
   never silently change the model of accepted work.

The same rules cover a smaller-context, four-parallel configuration of the same
model. Refresh context limit and total concurrency independently. A confirmed
capacity-only change may update N without disturbing existing owners. A changed
context/model/route contract requires a drained, validated binding revision first:
do not shrink an active conversation's supported context underneath it. If changing
the backend profile itself requires an engine restart, drain before that restart.
New or explicitly resumed work uses the new context limit and lane count; the
number of lanes is not inferred from the context reduction.

For the local pool use `residency=single-deployment`: the first owner pins the
pool's active model/route binding until all its owners finish or explicitly drain.
A second owner must use that same binding. A different local model waits for the
whole pool to drain, even if one lane is free. This prevents two legal lane owners
from alternately loading different models on a single-resident backend. A same-pool
delegate to an incompatible deployment is rejected at preflight while another
owner still pins the old binding; the caller can queue that task for a later drain.

An unexpected external backend/route replacement is not a normal model retry.
When detected, stop new admission and surface unavailable/binding-changed status;
checkpoint affected work for explicit recovery instead of redirecting it. Reset
incompatible provider response chains on a binding revision change; preserve each
session's transcript. Rebuild context against the new model's limits when the
user resumes there. Neither missing Responses support nor changing to Chat
Completions alters the pool's ownership rules.

## 6. Lifecycle, waiting, and explicit handoff

### 6.1 Logical lifecycle separate from execution phase

Do not overload `AgentState.Idle` to represent every non-streaming condition.
Retain execution phases such as CallingModel/ExecutingTools/Compacting/Retrying
and add a logical assignment lifecycle with stable serialized values.

| Logical state | Lane owned? | Behavior |
|---|---|---|
| Queued | No | Ready to execute; awaiting allowed capacity or enabled pool |
| Running | Yes if pooled; no if direct cloud | One live segment, including tools/backoff/preparation |
| Waiting | No | Durable checkpoint, awaiting child/message/dependency condition |
| Suspended | No | Durable checkpoint, explicitly paused or awaiting user/recovery action |
| Cancelling | If pooled, until drained | No new model calls; await active operations, then terminal |
| Completed / Failed / Cancelled | No | Terminal; immutable result/outcome |

Record `waitingFor`, reason, checkpoint sequence, desired pool, and actual lane
separately. Persist executionMode/deployment; pool/lane are null for direct cloud,
and the panel must not infer Queued from the absence of a lane. `Waiting` is
entered by an explicit wait/delegate action; a foreground
tool waiting for process output is still Running and retains its lane.

Separate `HasLiveExecution` (reload/shutdown), `HasNonterminalAssignment(session)`
(session mutation and send exclusion), and panel lifecycle. Preserve compatibility
for `IsRunning` callers until each has been audited; do not change its meaning in
one place and leave host reload or project switching unsafe elsewhere.

### 6.2 Spawn versus delegate

`agents.spawn` creates a child assignment and independent session, returning IDs
and admitted/queued status promptly. Parent may continue. If all lanes are owned,
the child stays queued: spawn is not an automatic handoff.

`agents.delegate` combines spawn with explicit suspension and a targeted transfer
of the parent's lane to that child, on the same pool. It works with capacity one.
Validate the child's policy/model/workspace before committing the handoff. A
transfer is an intentional cache-context change accepted by the caller, not an
optimization that the scheduler may insert by itself.

`agents.wait` registers conditions and yields at a safe boundary. It holds no
lane while waiting. If waiting for a queued same-pool child, target that child as
the next owner of the released lane. If waiting for already-running children,
release the lane for normal admission. A parent awakened by a result re-enters
admission; waking is never permission to execute without a lane.

For targeted delegate chains, record a return continuation so the parent is next
eligible for the child's released lane when the wait condition is satisfied.
The handoff stack is depth-bounded; it cannot rotate parent and child between
model requests. Parent resumes after the child assignment terminates, unless an
explicit interruption/cancellation changes the plan. Nested delegations follow
the same rule. Unrelated waiting work remains queued throughout that chain.

If the parent waits for several children, a completed child may make another
ready child eligible while the parent still waits. Do not reserve an unused lane
for a parent whose dependencies are not satisfied. User/cloud priority never
preempts a current owner. Limits on team size/depth and visible cancellation bound
runaway delegation; do not introduce time slicing to solve it.

### 6.3 Suspension transaction and tool protocol

Tools must not block inside `Task.WhenAll` waiting for a child to finish. Add a
typed runtime control directive (e.g. Wait/Delegate) to the orchestration result
path. It requests suspension; it does not release capacity from inside the tool.

Recommended sequence:

1. Preflight conflicting wait/delegate directives in one batch before executing
   it. Permit at most one suspension decision; otherwise return a clear error.
2. Execute the batch, finish all sibling tool calls, and persist exactly one
   tool result for every call. Delegation returns an acceptance receipt with
   child IDs; later completion arrives as a separate mailbox event.
3. Commit the checkpoint, dependency/wake condition, and handoff intent with
   idempotency keys. Run **no further model/compaction call** in this segment.
4. Unwind the segment and its plugin/service leases. Only after quiescence,
   transfer/release ownership and make the child or other ready work runnable.
5. When the wait condition is satisfied, persist readiness and acquire a lane
   before rebuilding the parent context and starting its next segment.

Store the handoff as a recoverable intent so a crash between commit and unwind
cannot start two owners. Persist a wake even if a child finishes before the
parent's segment finishes suspending; no lost-wakeup window. Never synthesize an
unmatched tool output or leave an unfinished tool call for Responses replay.

Reject dependency cycles (including self-wait and descendant waiting on its
blocked ancestor). A queued child is allowed to exist while its parent works,
but waiting on that child must yield/transfer rather than retain its lane.

Suspension is not AgentCompleted. Add explicit segment/lifecycle events and stop
the nudge plugin from interpreting suspension as an empty completed model turn.
Only terminal logical outcomes notify dependents of completion.

## 7. Persistence, identity, and restart behavior

Use the existing SQLite database and versioned migration system. Add normalized
records (names indicative) for:

- `agent_teams`: root identity, mode, allowed pools/direct deployments, budget policy.
- `agents`: AgentId, TeamId, ParentAgentId, unique SessionId, title, creation time.
- `agent_assignments`: RunId, AgentId, prompt/task reference, lifecycle, pool and
  deployment, execution mode, ready sequence, timestamps, checkpoint and result references,
  version for compare-and-swap transitions.
- `agent_waits` / dependencies: awaited runs/messages, any/all policy, deadline,
  handoff/return continuation, satisfied status.
- `agent_messages`: MessageId, sender/recipient/team, per-recipient sequence,
  type, bounded body/artifact references, timestamp, idempotency key.
- `agent_checkpoints`: schema version, transcript cursor, frozen project/workspace
  context, mailbox cursor, remaining budgets, resume metadata.
- Lane ownership/handoff journal with host epoch, assignment, pool/lane, and state.
- Task-board rows with owner/dependencies/status/version, and budget reservations.

Use the existing short-lived pooled connections and transaction discipline.
Creation of a child session, agent, assignment, initial input/instruction snapshot,
and spawn-operation record must be atomic. Delivery consumption and transcript
append must also be atomic or deduplicated by durable IDs.

Provide store operations spanning the needed records; separate calls to existing
ISessionStore methods are not magically one transaction. Enforce one nonterminal
assignment per session with a database constraint or equivalent transactional
check. Spawn/message/wait/cancel retries return the original outcome. A repeated
operation ID with different arguments is a conflict, not a new operation.

On restart, fence all previous-host tokens. Reconcile persisted assignments:

- Queued/waiting work remains visible and is reconstructed once.
- A clean checkpoint may resume via normal admission after dependencies and
  current policy are revalidated. Snapshot the accepted instructions/workspace;
  do not silently rebuild from changed project files.
- An interrupted stream or tool batch with uncertain side effects becomes
  Suspended with `recovery-required`. Never replay a write, process launch, or
  delegation merely because its result is missing.
- Completed results/messages are delivered idempotently after recovery.

The initial migration adds no fake active runs for old sessions; create their
AgentId lazily on a new accepted submission. Keep terminal metadata bounded or
paginated, but never prune nonterminal assignments, unread messages, or unconsumed
results. Session deletion must reject nonterminal assignments and unresolved
dependency references rather than orphan another agent's wait.

## 8. Context, Responses chains, and workspace policy

netPI currently uses `/v1/responses` with `previous_response_id` chaining (the
user's “prev_id”). Preserve that optimization without making it a scheduler.
The system must pass the same admission tests with a stateless fake provider and
with Chat Completions; swapping inference engines cannot weaken the lane cap.

Children get their own SessionId, transcript, compaction state, and provider
response chain. Never copy a parent response ID into a child. Returning to the
parent may reuse a still-valid chain or use the existing full-context fallback;
that is transport behavior, not authority to acquire a lane.

Chain identity must include effective deployment/route where needed so identical
model names on different backends cannot share a chain. Keep main conversation
and maintenance chain scopes distinct. Pin deployment for an assignment; a model
or route change is explicit and invalidates incompatible chain state. Do not
persist provider IDs as a substitute for recoverable conversation checkpoints.

Default child context is a task brief plus selected facts/files and the frozen
effective instructions/tool policy. Full parent transcript inheritance is an
explicit option, never the default. Instruction/tool permissions cannot expand
through delegation. Project changes for a nonterminal session remain pending
until a defined safe policy boundary; for this version use logical completion,
not the mere existence of a suspension checkpoint. Descendants retain their own
accepted snapshots when a parent later changes project.

Each child result contains outcome, concise summary, artifacts/changed files,
verification evidence, unresolved issues, and its session/run references. The
parent receives this bounded result, not token streams or every intermediate tool
step. Reading the full child history is explicit and paginated.

Workspace modes must be explicit:

- `shared-read`: research/review with read-only tool policy. A generic unrestricted
  shell is not read-only enforcement; withhold mutating tools/shell capabilities
  or state clearly when a profile provides only advisory restrictions.
- `isolated-worktree`: independent code changes from a recorded base revision,
  branch `codex/...`, with workspace-relative tools pointed at that worktree.
- `shared-write`: explicit file/task ownership; coordinator handles conflicts.

An isolated worktree does not automatically include the parent's uncommitted
changes. Record whether the task needs a captured patch/snapshot and apply it
deliberately; do not silently omit relevant changes. Non-Git workspaces need an
explicit shared-write choice or bounded snapshot; do not assume Git exists.
Keep dirty worktrees and result artifacts until reviewed. No automatic push,
merge, destructive cleanup, or checkpoint commits as a side effect of delegation.

## 9. Agent tools and communication

Names below are proposed tool names; use compact schemas suited to local models.
All tools derive caller identity from runtime context and enforce team/policy.

| Tool | Essential inputs / behavior |
|---|---|
| `lanes.list` | Optional pool filter; owners, capacity, enabled/draining state, queue count, availability age; read-only snapshot, never a reservation |
| `agents.spawn` | Assignment, expected result, pool or authorized deployment/profile, context selection, workspace mode, operationId; returns AgentId/RunId/SessionId and queued/admitted status |
| `agents.delegate` | Same task fields; same-pool suspension/handoff and result-only return |
| `agents.message` | Target AgentId, kind, bounded body/references, operationId; durable enqueue without inference |
| `agents.wait` | Target run/message conditions, any/all, optional deadline; safe suspension, not polling or a blocked live tool |
| `agents.inspect` | Status/result by ID; optional bounded history cursor |
| `agents.continue` | Explicit follow-up for a terminal agent; creates a new assignment in its existing session and queues normally |
| `agents.cancel` | Own descendant or authorized target; explicit subtree flag; cancellation idempotent |
| `team.tasks` | List/create/claim/update bounded task records with expected version and dependencies |

Message kinds: assignment, question, finding, answer, blocked, completion, and
control notification. Sender/type metadata survives compaction. Render them as
agent communication, not as if the human user issued new instructions. Use a
provider-compatible message representation with explicit provenance; do not
invent unsupported wire roles.

Drain mailboxes at model-turn boundaries after complete tool batches, with bounded
message count/size per turn. Preserve remaining messages for later. Deterministic
batching/coalescing needs no summarizer model call. Never interrupt a tool batch
for ordinary peer messages.

Running recipients see queued messages at their next boundary. Waiting recipients
wake only for registered conditions; they then need admission. Manually suspended
recipients stay suspended. Terminal recipients stay terminal until explicit
continue. Notifications do not themselves occupy a lane or wake every agent.
Persist a deadline wake event rather than repeatedly invoking the model to check.

Parent cancellation defaults to its whole descendant subtree, including queued
children and waiters. Cancelling one worker alone wakes its parent with a
cancelled result; it does not cancel siblings. Background processes remain owned
by the existing manager: cancelling an agent stops its foreground batch, while
already detached background jobs retain their existing explicit stop semantics
and remain visible. Do not silently kill unrelated or detached processes.

## 10. Orchestrate mode

Add a session/team mode: `chat` or `orchestrate`. This is runtime/prompt policy,
not another scheduler. Persist the selected mode; default to chat. In either
mode, pooled manual user work and delegated work obey the identical pool cap.
Direct-cloud work has no local lane membership and follows §11 instead.

Orchestrate guidance:

1. Define concrete deliverables, dependencies, workspaces, and acceptance checks.
2. Delegate bounded independent tasks; use the task board for ownership.
3. Yield while workers run; review summaries and request specific follow-ups.
4. Integrate artifacts and verify the combined result before completion.

With two local lanes, coordinator plus one worker can run concurrently. To run
two workers, coordinator must checkpoint and relinquish its lane. It cannot run
extra planning calls in the background while both workers own the pool.

That paragraph applies to a **local coordinator**. A user-selected direct-cloud
coordinator can plan/message/review while both local workers run; it consumes
cloud usage but no local lane. It can suspend while awaiting results to avoid
unnecessary paid turns. When it delegates local work, it queues that work normally
and cannot claim a same-pool handoff from a local lane it does not own.

Suggested walkthrough: coordinator occupies lane 1, spawns worker A into lane 2,
then delegates worker B on lane 1 and waits for both. Whichever worker completes
first releases its lane; another ready child may run, but coordinator resumes
only when its chosen wait condition is met and capacity is acquired. The panel
shows all three agents, with coordinator Waiting and only two occupied lanes.

Do not automatically fan out trivial tasks, recursively spawn until every lane
is full, or let every agent read the entire team transcript. Team/depth/token/tool
and wall-time limits must be checked before new work is admitted. Hitting a limit
produces a clear blocked/terminal result, never a hidden cloud escalation.

## 11. Ordinary sessions, direct cloud, and optional cloud pools

### 11.1 Session creation and model selection

Keep the existing New session action, normal session tabs, history picker, project
selection and composer. The Work overview is the existing collapsible right panel,
not a new full-screen workspace or replacement for chats. Every agent is a normal
openable session; no special agent-only chat window is necessary.

User flow:

1. Create a normal session and optionally select its project.
2. Choose the model with the existing composer model selector. Group/label entries
   by execution policy: `Local — qwen3.8-27b · 2 shared lanes` and
   `Cloud — <model> · direct`. A separate lane-management wizard is not required.
3. Send the message. A local selection claims or queues for a local lane. An
   enabled direct-cloud selection starts independently of local occupancy.
4. The session appears in Work and remains in the normal tab/history system.
   Show `Cloud · direct` on its row; do not give it a fake lane number.

Chat/orchestrate mode is separate from model selection: either a local or cloud
model can be used for either mode. Ordinary cloud chat needs no delegation/team
setup by the user. An internal single-agent team may supply uniform accounting.

Selecting a cloud model in a user-created session explicitly allows that selected
deployment for that assignment within the user's configured spending limits.
`cloudAllowedByDefault=false` means no implicit cloud choice, fallback, or child
escalation; it must not make a deliberate authorized cloud selection unusable.
Authorizing a cloud root does not automatically authorize paid child fan-out:
descendants follow a separate team delegation allowance. Disabled deployments
are unavailable until enabled in settings by the user, not by an agent.

Model changes during a nonterminal assignment take effect only on an explicitly
new assignment or after safe cancellation/suspension and policy revalidation.
Switching tabs or selecting another model cannot move a live local owner into
direct execution or steal another owner's lane.

### 11.2 Cloud execution policy

Configure cloud deployments as `direct-cloud` by default, outside every pool.
They still have normal run identity, cancellation, transcripts, progress, mailboxes,
and budget accounting. Optional provider request/rate limits protect cloud usage;
they do not constitute assignment-held local lanes. If the user later wants a
fixed number of cloud agent lanes, configure that deployment as pooled instead.

Define concrete routes through the existing provider where possible; add provider
plugins only when needed, behind Abstractions. Credentials stay in provider
configuration, never task prompts, lane tools, panel payloads, or checkpoints.

Cloud eligibility requires a user-enabled deployment, explicit session/team
allowance, eligible model and remaining budget; pooled cloud additionally requires
an enabled pool and its lane. Agents cannot enable paid execution, increase limits,
change rates or bypass restrictions through an AiProxy alias. User-facing settings
show enabled cloud models and per-team usage. Disabling direct cloud stops new
requests (including retries) and checkpoints a running assignment at its next safe
boundary after in-flight work drains. Pooled-cloud disabling follows the pool
drain rules. Provide separate explicit cancel for active work.

Reserve estimated maximum request cost transactionally before sending each paid
request; reconcile reported usage afterward. All descendants and retries share
the team's budget. Bound output tokens so reservations have a calculable ceiling.
Unknown prices/usage are shown as unknown and require an explicit token-based
policy or user allowance, not a false claim of a precise currency hard cap.
When a budget stops a run, checkpoint/block or terminate it at a safe boundary,
releasing a lane if it owns one. Do not leave an invisible owner waiting indefinitely.

### 11.3 Availability hints and deferred NUC support

Targeted AiProxy source inspection found backend/model slots, queues and active
requests in `/aiswitcher/status`, per-backend admission in `BackendQueue.cs`, and
Yue2 demand tracking in `InferenceProxy.cs`. netPI currently reads capabilities
from `/v1/models` but does not expose this live status via ModelInfo. The capacity
adapter is required for the default Follow AiProxy mode (§5.3). Unknown capacity
holds new admission; it never bypasses assignment ownership. Do not infer total
concurrency or deployment availability from model residency alone.

If the NUC is reintroduced later: it is **one exclusive resource**, LLM or Yue2,
never concurrent work. Its automatic loader can load an LLM when the machine is
free. Resource ownership belongs in AiProxy/AiSwitcher and is consumed as a hint
or admission response; the initial astra-2 implementation does not add this pool.

## 12. Combined Background panel and chat navigation

### 12.1 Product behavior

The companion conversation mockup is for user review of naming, density, panel
width, and metadata detail. Treat that visual styling as provisional; the lifecycle,
newest-first ordering, assignment-ownership rule, and navigation semantics below
are binding. The displayed two lanes illustrate the initial provider configuration;
the shipped panel must render the reported N rather than hardcode two slots.

Replace the separate Background and Activity tabs with one **Work** tab containing
**Agents** and **Background processes** sections plus a compact lane summary.
Preserve foreground-process inspection from Activity as a secondary collapsible
section. This changes the existing Background work overview rather than adding
another tab beside it.

Use `NetPI.Activity` as the presentation owner: it already reads both agent and
process services and has session navigation. `NetPI.BackgroundTasks` retains
process ownership, tools and `/api/bg/*` APIs on port 5275, but stops registering
its own panel. Activity registers the combined panel with ID `background`, title
`Work`, order 5, served from its existing port 5276 and `/panel/activity` route.
Do not keep a second registration with ID `activity`. No new presentation plugin
and no copy of the same UI maintained in two HTML files.

Migration details:

- Existing `rightTab=background` remains valid. One-time persisted selection
  migration maps `activity` to `background`, preserving width/open state; this
  migration is not a hardcoded panel-content branch in the shell.
- Keep `/panel/activity` working. The old `/panel/background` endpoint may resolve
  the registered combined panel and redirect to it, or show a compatibility link;
  it must not become a second evolving work UI. Handle Activity being unavailable.
- Activate both registration changes together at a safe lifecycle boundary.
  Test mixed/staged versions and avoid two live plugins claiming `background`.
  Do not kill running background jobs to force their plugin to reload; defer that
  part of activation until its existing leases drain.
- Unloading the presentation plugin removes the view only. Background jobs and
  agents keep running. Losing the process plugin must leave the agent view usable.

Agents section lists **all nonterminal agents across every session/team**, including
root agents, admitted workers, Queued, Waiting and Suspended. Do not restrict it
to the selected chat or the runner's live execution-task dictionary. Show bounded,
paginated recent terminal assignments as history; terminal history limits must
never hide nonterminal records.

Each row includes task/session title, logical state and execution phase, pool/lane
(or `waiting for local-big`, or `Cloud · direct`), parent/team hint, model, created/start times, and
useful waiting/failure reason. One current-assignment row per agent, with older
assignments in history, avoids duplicating a paused/resumed agent for every segment.
Counts distinguish occupied lanes, running direct-cloud agents, queued agents,
waiting and suspended agents. Three running rows may mean two local owners plus
one direct-cloud agent; the local lane display still reads `2 / 2`.

Sort the current Agents list by assignment `createdAt DESC`, then stable ID;
processes by `startedAt DESC`, then stable ID. “Last first” means newest-created
work first, **not** most recent token, heartbeat, state transition, or running-first
grouping. State badges do not change row order. New assignments may move their
agent to the top. If terminal history has a separate section, keep that section
newest first too. Do not carry Background HTML's running/done regrouping into
the consolidated view.

Clicking an agent row/title or keyboard-activating its link selects its existing
chat tab or adds and selects that session if not already open. It must not spawn,
resume, cancel, or otherwise change lane ownership. Do not create a new browser or
desktop window. Process Output/Stop and agent Cancel buttons must not also trigger
row navigation. Preserve scroll, focus, expanded output, and selected rows during
refreshes; avoid replacing the whole DOM and losing click targets every poll.

Keep current background output truncation indicators and process-tree stop
behavior. Add origin validation to any new control endpoints and maintain the
localhost control boundary from PLAN §41 / astra-1 §11a. The presentation plugin
must not own agent lifetimes: unloading it must not cancel agents.

### 12.2 Data contract and refresh

Extend ActivityWebApp's existing `GET /api/activity/agents` to return lifecycle
rows, pool snapshots, revision and explicit service availability. Reuse its process
API and lazily leased service access; preserve `/api/bg/jobs` and other process API
compatibility. Agent cancellation delegates through the orchestration contract
using RunId and explicit subtree semantics; queued/suspended assignments must
also be cancellable. No direct calls across plugin assemblies.

Use one canonical query projection for panel and WS snapshots. Timestamp/state
formats must be explicit and consistent (choose documented lower-case lifecycle
strings; do not compare lower-case literals against C# enum `ToString()` output).
Missing Agent/Lanes/Orchestration service shows `unavailable` in that section while
process controls still work. Partial failures must not blank the other section.
Preserve the existing background-tail behavior: Activity's current output path
must be checked against the manager's bounded ring/cursor semantics so the combined
view returns the latest requested tail, not the tail of only the first fetched
chunk. Reuse a contract-level tail operation or correct paging; never reach into
BackgroundJobManager's concrete type from Activity.

Modest 2-second polling while mounted/visible is acceptable for the first version;
no inference requests, no overlapping polls, backoff on failure, resync on return.
Bound/page terminal history and output independently. Reuse revisions or events
later if needed; UI polling does not advance the scheduler.

### 12.3 Reuse and harden the existing iframe bridge

Use a generic versioned envelope such as:

```json
{
  "type": "netpi.panel.openSession",
  "version": 1,
  "payload": { "sessionId": "..." }
}
```

Accept it only when `event.source` equals the currently mounted panel iframe's
`contentWindow` AND `event.origin` equals the origin of that registered panel's
entry URL. Validate the message shape and bounded session ID. An origin/type
string alone is insufficient. Reject messages from old/unmounted frames, sibling
windows, arbitrary loopback pages, and unregistered sources. Preserve the old
Activity envelope temporarily with the same checks, then document deprecation.

Derive the actual shell origin via a validated bridge initialization/handshake;
panel sends to that exact targetOrigin, not `*` and not a hardcoded 5173 origin.
Keep this in the generic RightPanel bridge rather than adding hardcoded content
branches by plugin ID. App navigation then uses the existing `ws.openSession`,
`session.open`, and store tab machinery. Handle deleted/unknown sessions with a
small visible notice; do not silently navigate to a different session.

A child being created or updated must **not** steal focus from the parent. Today
`session.created` normally navigates; publish background child creation as metadata
or add explicit navigation intent/request correlation. Only deliberate user opens
or user-requested session creation should select a tab. Ensure an agent session
outside the first 50 history rows can still open by ID. Streaming events from
other agents update their state/unread markers without replacing the selected
conversation. Test rapid successive clicks and reconnect ordering.

## 13. Web protocol, controls, and observability

Retain the existing envelope and extend rather than fork the protocol:

- `chat.send` acceptance returns stable operation/session/run/agent IDs and a
  disposition (`queued` or `admitted`). Full capacity is accepted queueing, not
  an “all concurrent runs busy” rejection. Validation/policy/queue-limit errors
  remain errors; retrying the same operation cannot create another assignment.
- Extend `runs.list` with logical lifecycle and identity or add `agents.list`
  backed by the same projection; document the compatibility choice.
- Add pool/agent snapshots and lifecycle deltas (`lanes.state`, `agents.state`,
  `agent.updated` are proposed names) with monotonic revisions. Reconnect can
  obtain an authoritative snapshot after missed deltas.
- Keep token events scoped to SessionId/RunId. Include AgentId/TeamId where
  necessary; avoid broadcasting all child transcripts into the parent's context.
- `agent.cancel` by RunId also handles queued/suspended records. Stop controls
  target the displayed session, not an arbitrary globally active run.
- User steering to a Running assignment is consumed at its existing safe
  boundary. Steering to a Waiting/Suspended assignment is persisted and shown;
  it does not silently revoke its wait or steal a lane. Explicit resume/continue
  commands express that intent and still enter admission.

Composer, session tabs and stop controls distinguish Queued/Waiting/Suspended
from Idle. Keep `assistant.completed` a message/turn event, not a claim that a
logical run released its lane. Session model/project edits cannot silently mutate
an accepted assignment's frozen policy/context; preserve pending-change behavior.

Log structured ownership transitions: pool, lane, AgentId, RunId, epoch, reason,
queue time, admission/release time, and deployment. Track owner count, in-flight
requests, wait time, intentional handoffs, and rejected stale permits separately.
Provider cache-hit/prefill metrics are optional diagnostics, not correctness gates.
Panel occupancy should agree with those transition records even during tool calls.

## 14. Reload and shutdown rules

Lane ownership and collectible-ALC service leases are different resources. An
admitted execution segment holds the appropriate execution/service leases and
prevents its owning generation from unloading. A cleanly checkpointed Waiting or
Suspended record holds no old runtime task, provider object, tool object, callback,
or service lease.

For scheduler/orchestrator reload, stop admission first, drain live segments or
report reload deferred, persist intent, and transfer generation ownership only
after old dispatch/callback tasks stop. New generation reconciles durable queued
and suspended state once. No old/new scheduler overlap and no default-empty queue
that silently forgets accepted work. Do not define reload as a reason to rotate
active agents off their lanes merely for convenience.

Audit the host's `AgentIdle` gate and session-busy callers separately. Queued and
checkpointed runs should eventually permit safe plugin reload without making their
sessions appear free for another assignment. Keep conservative reload deferral
until that distinction has been implemented and verified end to end.

Fix per-run task ownership before enabling this feature: shutdown must await all
segments, not just the last `_runTask`. A timeout cannot release a still-executing
segment's token or unload code it still uses. Persist interrupted/uncertain status
when a process shutdown prevents clean completion. Do not automatically restart
the host or stop nInfer as part of implementing or testing these rules.

## 15. Implementation packages and completion gates

Keep every package independently reviewable and verified. Mark completion here
only with evidence. No production feature flag until its invariants pass. Start
with fake providers; do not spend cloud credits to test scheduler correctness.

### A — Contracts, persistence, and execution ownership

- [ ] Add lifecycle/identity/permit/checkpoint contracts and storage migration.
- [ ] Implement atomic child creation and idempotent operations; preserve existing
      session/history APIs and project snapshots.
- [ ] Replace the runner's single-task shutdown bookkeeping with ownership of all
      segments. Separate logical session busy from live execution.
- [ ] Introduce explicit segment outcomes (terminal versus yielded), without
      string-matching a failure note or emitting completion on suspension.
- [ ] Verify migration rollback/reopen, duplicate IDs, concurrent submission,
      all-task shutdown, and old-session compatibility.

### B — Strict local lanes (first usable milestone)

- [ ] Add NetPI.Lanes and register it in solution/publication/test references.
- [ ] Configure the local pool to Follow AiProxy (initially two lanes, no manual
      cap); implement the provider-capacity contract and migrate admission.
- [ ] Queue normal root submissions as well as children; hold ownership through
      tools, retries, maintenance and model-turn boundaries.
- [ ] Guard all provider inference, including AutoCompact and catalog probes.
- [ ] Verify the A/B/C trace from §3 with explicit synchronization barriers.
      C must remain absent from provider logs while A/B own the lanes.
- [ ] Test cancellation/drain races, stale permits, disabled/reduced pools and
      failure paths. Passing semaphore/request-concurrency tests alone is insufficient.
- [ ] Verify provider capacity 2→4 admits two more owners and queues a fifth;
      4→2 drains without preemption. Cover optional caps, unknown/stale metadata,
      and separate context-limit changes without inferring concurrency from context.

### C — Child agents and same-pool delegation

- [ ] Add NetPI.Orchestration tools, bounded independent child contexts, and
      result summaries linked to full sessions.
- [ ] Implement wait/delegate directives after persisted complete tool batches,
      durable handoff, lost-wakeup handling, and return continuation.
- [ ] Verify capacity-one delegation and capacity-two parent/two-worker flow.
- [ ] Verify no deadlock while both parents delegate; no third model caller;
      distinct response chains; no duplicate tool outputs after resume.
- [ ] Add workspace modes/ownership and preserve dirty worktree artifacts.

### D — Mailboxes, collaboration, and recovery

- [ ] Add durable messages/cursors, task board, conditions/deadlines and cycle checks.
- [ ] Add explicit continue/cancel/resume behavior and subtree cleanup.
- [ ] Recover queued/clean checkpoints across restart/reload; quarantine uncertain
      side effects rather than automatically replaying them.
- [ ] Verify concurrent deliveries, deduplication, late results, orphan prevention,
      mailbox bounds, and service-generation fencing.

### E — Combined Background panel and navigation

- [ ] Consolidate Background/Activity into the Work view owned by Activity, retain
      panel ID `background`, and migrate old `activity` selections safely.
- [ ] Extend Activity's query payloads with lanes/lifecycle and two newest-first lists.
- [ ] Include every nonterminal root/child and expose waiting/suspended reasons.
- [ ] Reuse/harden iframe navigation and handle child session creation without focus theft.
- [ ] Preserve process output/stop behavior and independent missing-service states.
- [ ] Use the canonical projection without another registry; preserve foreground
      inspection, background tail/stop behavior, and legacy process APIs.
- [ ] Verify browser/WebView behavior: row click opens/selects a tab, no new window,
      queued/suspended chat inspection, no implicit resume, no ordering churn.

### F — Orchestrate mode and optional cloud

- [ ] Add persisted mode and concise tool guidance; coordinator yields for two workers.
- [ ] Add direct-cloud model selection and execution authorization, optional cloud
      pools, allowlists, shared reservations/budgets and usage UI.
- [ ] Verify paid deployments stay disabled by default; agents cannot enable them;
      cloud never changes the local pool's provider-derived target or optional cap.
- [ ] Verify an ordinary user-created cloud session runs while both local lanes
      are occupied, without changing their ownership or requiring an agent setup flow.
- [ ] Verify pool disabling, exhausted budget, retries, unknown usage and route-alias
      constraints using test providers. Do not require a NUC/Yue2 integration.
- [ ] Add discovery-backed execution settings and the drained backend/model rebind
      flow (§5.3–5.4); preserve Follow AiProxy policy and any explicit cap across
      engine changes while updating effective capacity from the validated backend.

### G — Delivery and operational verification

- [ ] Update AGENTS.md, protocol, plugin architecture, web-panels, and Responses
      documentation to describe shipped behavior and new service IDs/config.
- [ ] Run focused tests first, then `dotnet build NetPI.sln` and `dotnet test NetPI.sln`.
      Extend existing ConcurrentRuns, RunRegistry, RunCleanup, ResponsesIdentity,
      AutoCompact, ActivitySurface, BackgroundSurface and control-boundary coverage.
- [ ] From `web/netpi-web`, run `npx svelte-check --tsconfig ./tsconfig.app.json`
      and `npx vite build`; report unrelated pre-existing warnings separately.
- [ ] Publish changed plugins through `tools/publish-plugins.ps1`; verify pointers,
      active build IDs, and expected services after activation. Host/Abstractions
      changes require a coordinated staged restart, not a claim of hot reload.
- [ ] Smoke-test two real local owners and a queued third, retaining tools/retries
      between requests; inspect netPI admission logs. Backend cache metrics can
      support performance observations but cannot replace the correctness proof.
- [ ] Commit only complete verified work if requested/appropriate under repository
      discipline; never push without the user's instruction. Keep artifacts/dist
      and runtime state out of Git.

## 16. Required acceptance matrix

Use controllable fake provider streams, barriers and an injectable clock. Avoid
timing-only sleeps as proof of concurrency. Test provider-entry identities and
ownership transitions, not just counts of simultaneous HTTP requests.

| Scenario | Required observable result |
|---|---|
| A/B admitted, C queued | Only A/B reach provider across many turns; C starts after release |
| A tools/retry delay while C waits | A retains lane; C performs no model/tool execution |
| Other local root chat submitted while A/B run | Persisted queue entry and visible chat; no bypass |
| Enabled direct-cloud chat submitted while local A/B run | Cloud runs independently; A/B retain both local lanes; all three sessions visible |
| Cloud coordinator delegates two local workers | Coordinator holds no local lane; children need ordinary local admission |
| Local alias requested as direct-cloud by an agent | Rejected before inference; execution mode comes from trusted deployment config |
| Lane capacity 1, parent delegates child | Parent fully quiesces, child runs to terminal, parent resumes with summary |
| Two parents delegate at once | Two atomic handoffs, no deadlock, never more than two owners |
| Child finishes before parent suspension unwinds | Durable wake retained; exactly one resume |
| Ordinary message to suspended parent | Mailbox changes; no inference or automatic preemption |
| Wait dependency cycle | Rejected with actionable reason; no stranded lane |
| Cancel running/queued/waiting child | Correct target terminal; parent gets one outcome; lane released only after drain |
| Provider ignores cancellation temporarily | Lane stays occupied/cancelling; queued work cannot overlap it |
| Duplicate spawn/send/delegate operation | Same records/results; no duplicate child/session/assignment |
| Crash after tool effect before result persistence | Recovery-required state; side effect not replayed automatically |
| Reload with only durable waiting/queued records | New generation adopts once; no old callbacks or lost work |
| Reload/shutdown with two live segments | Both tracked/drained or visibly deferred; no orphan task |
| Compaction, manual compact, wire probes | No unscheduled third agent/context; proper owner or queued maintenance |
| Chat Completions / different fake engine | Identical ownership guarantees without response IDs/cache support |
| Parent and child use same model | Separate sessions/chains; parent gets bounded result, not child transcript |
| Cloud disabled or team disallows cloud | No paid provider call despite local queue |
| Parallel cloud requests near budget limit | Shared reservations prevent double-spending the same remaining allowance |
| Local cap reduced or pool disabled | Drain shown; no eviction and no excess new owners |
| Backend replaced from nInfer to llama.cpp | Same Local GPU pool and Follow AiProxy policy; new validated binding/capacity, no engine-specific scheduler code |
| Provider capacity increases 2→4 | Four admitted owners allowed, fifth queued; no replacement of existing owners |
| Provider capacity decreases 4→2 | Existing owners finish; no new admission until below target; no forced rotation |
| Provider reports four slots with explicit cap two | Two admitted owners; cap remains visible and user-controlled |
| Provider capacity absent/stale | New admission/ownership transfers held with a visible reason; no guessed numeric fallback |
| Context reduced and concurrency explicitly reported as four | Refresh both facts, validate/drain changed context binding, then use four lanes; context size alone cannot change N |
| Two different models share a single-resident local pool | No mixed-deployment ownership; other model waits for full drain |
| Discovery adds a backend or changes a model route | No silently enabled pool, paid route, or retargeted assignment; validated capacity updates for the already approved binding are followed |
| Panel has running, queued, waiting, suspended agents | All visible across sessions; newest-created first, stable during token updates |
| New completed process versus old running process | Newer process appears first; no running-first regrouping |
| Agent session absent from loaded history page | Row opens it by ID and selects/creates exactly one tab |
| Child creation or offscreen agent streams | Current tab remains selected; unread/status updates only |
| Agent row clicked during a run | Navigation only; lane count and lifecycle unchanged |
| Spoofed/stale iframe message | Rejected; registered active frame still navigates normally |
| Background/agent service unavailable separately | Other section remains usable; no chat failure |
| Old Background/Activity panel selection after migration | Exactly one Work tab; selected view/width retained; no duplicate registration |
| Long background output beyond first chunk | Latest requested tail and truncation marker preserved in combined view |

## 17. Explicit non-goals and implementation traps

- No request-round-robin scheduler, spare-lane borrowing during tools, or hidden
  inference probes. A generic SemaphoreSlim around HTTP is not this feature.
- No dependence on nInfer slot IDs, KV-cache APIs or Responses storage for the
  admission invariant. Cache optimization and execution permission are separate.
- No NUC/small-model/Yue2 work in the initial delivery.
- No automatic paid escalation, arbitrary global broadcasts, or self-expanding
  teams beyond configured budgets/depth/queue limits.
- No parent waiting inside a long-lived tool invocation that holds the last lane.
- No runtime-only mailbox/checkpoint as a substitute for durable state, and no
  claim of exactly-once filesystem/process side effects across crashes.
- No duplicate agent scheduler in Web/BackgroundTasks/Activity, no plugin assembly
  references, and no hardcoded right-panel content in the Svelte shell.
- No treating a visible chat tab, a live HTTP stream, and a logical agent assignment
  as the same lifecycle. Their boundaries differ throughout this feature.

Completion means the tests demonstrate **N stable local owners, following AiProxy
capacity, until terminal completion or explicit handoff**, the user can inspect every agent from the
combined Background panel, and suspension/delegation/messaging work without
depending on the inference engine or silently consuming cloud capacity.
