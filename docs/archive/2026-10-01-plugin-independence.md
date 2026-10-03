# Plugin independence and optional enhancement plan

Date: 2026-10-01
Status: implemented in the coordinated agent rework pass; validation linked below.
Reviewed snapshot: master at 792fd70, in the isolated `codex/plugin-dependencies` worktree.
Scope: all 30 projects under `plugins/`, their source UIs, shared contracts, host registries, app UI integration, and existing relevant tests.

## Implementation — 2026-10-01

The user requested one implementation pass followed by testing/fixing, superseding the original six-commit sequence. All six steps are implemented: build-reference and isolated startup/stop gates for all 30 plugins; independent Runtime prompt composition; durable prompt revisions and reset-fork intent; executor-dependent tool/goal availability; typed decision/Git capabilities with shared admission; and capability-aware app, Work and Diagnostics views. The existing shared SessionModel resolver delegates configured-agent lookup through the scheduler snapshot with a no-Agents settings fallback.

Owning regressions cover profile changes without Context, Context return, identity races during rendering, persisted fork reset across Context reload, executor return without autonomous restart, inference surviving Runtime shutdown timeout, cancellation/grant races, same-model decision lease reuse, alternate decision providers, verified updates with concurrent edits and verifier cancellation acknowledgement. All 30 plugins started and stopped independently in the real host gate. Final gate results are recorded in [the implementation pass](../archive/2026-10-01-agent-rework-pass.md).

The findings below preserve the original review snapshot; the inventory describes current ownership. External endpoint health and actual cache performance are separate from registry presence and functional independence.

## Goal and architectural rule

Each plugin loads, exposes its own useful features, and stops or reloads independently. Plugins enhance one another through shared capabilities, optional RPC contracts and events. A missing enhancement changes only the feature that uses it, with an explicit reason.

Some operations inherently require a capability: a model call needs a model provider, delegation needs an executor, and automatic goal continuation needs an executor. Any implementation of the shared capability can satisfy that need. Plugin identity and load order must not be the requirement.

The acceptance baseline is the host plus the plugin under test, with mock capabilities supplied only for an operation that intrinsically requires them. The usable chat baseline is Runtime plus any suitable model provider. Agents, Context and the other feature plugins should enhance that baseline.

## Review findings

### What already works

- All 30 plugin csproj files are minimal. `plugins/Directory.Build.props:15` supplies the common NetPI.Abstractions reference. No plugin-to-plugin ProjectReference or linked source inclusion was found. Provider Common files are local source copies, not assembly dependencies on another provider.
- IServiceRegistry already supports optional lookup and replacement by priority. Most consumers resolve shared interfaces per operation. RPC registration also supports replacement, and IRpcRegistry.Exists allows optional integrations.
- Runtime already runs without Agents and falls back when Context is unavailable. These paths have existing tests.
- Work independently aggregates Agents, Runtime and Shell RPC results. Missing parts are null; failures in available parts have separate errors. Diagnostics similarly tolerates missing integrations in its general views.
- Loops retains deterministic detection without Decide. Guardrails retains its rules and user approval path without Decide; an unavailable second opinion does not clear a command.
- Ideas owns its backlog in SQLite. Files and Decide enhance commit watching, matching and suggestions. Backup copies the database without needing Ideas to be loaded.
- AGENTS.md, Skills and Todo supply their own notices through shared hooks rather than calling Context to inject them. Tool selection is already shared through ToolSelection, including MCP discovery.

### Gaps to fix first

1. **Runtime's fallback drops other plugins' prompt contributions.** `plugins/NetPI.Runtime/AgentRunner.cs:374-414` tries ISystemPromptBuilder and otherwise builds a fixed prompt. The fallback does not read IPromptSection, SessionIdentity, or tool PromptGuidelines. Thus Profiles can still restrict tools but its identity text is ignored without Context; Agents, AGENTS.md and other section contributors lose their system guidance. AGENTS.md and Skills notices still work through hooks. This is functional coupling despite optional service lookup.
2. **Profiles directly invalidates Context's private cache.** `plugins/NetPI.Profiles/ProfileService.cs:83` calls `context.reset` after writing shared session metadata, and catches non-cancellation errors. This loads independently, but prompt correctness relies on that particular implementation being present and handling the call. A profile changed while Context is disabled can leave an old persisted prompt ready to be reused when Context returns. Validate this scenario and make invalidation durable.
3. **Goal can look active while execution is unavailable.** `plugins/NetPI.Goal/Goals.cs:348` and `:467` return when IAgentRuntime is missing. Goal data and tools remain usable, but starting or continuing can silently do nothing. Execution availability needs a visible reason and an explicit resume policy.
4. **Agent tools are offered even when their operation cannot execute.** `plugins/NetPI.Tools.Agents/AgentTools.cs:18-19` correctly returns an error without IAgentRuntime. Registration currently has no availability gate. Keep the capability requirement, but avoid advertising operations that cannot work.
5. **Some configuration and protocol coupling is implicit.** Runtime reads `agents.<id>.model` directly (`AgentRunner.cs:261`); Decide inherits `providers.aiproxy.baseUrl` (`DecidePlugin.cs:89-90`). These settings exist independently of their owning plugin, so these are not load dependencies, but their shared meaning should be deliberate and documented.
6. **Optional feature absence and provider failure are inconsistent in views.** Diagnostics ContextView uses Promise.allSettled, so missing AGENTS.md or Skills does not break its other data; missing Context is shown as an error. Work handles missing parts well, but controls can still become unavailable between snapshot and click. App controls use several separate catch-and-hide patterns. Standardize availability while preserving real errors.
7. **Decision admission is split across consumers.** Decide RPCs make direct HTTP calls outside the host model middleware. Ideas acquires an optional scheduler lease before calling them; Loops and Guardrails call them directly. The decide.parallel semaphore is local to one Decide tool execution, not shared across RPC callers. A unified optional enhancement must preserve capacity and accounting without reacquiring a slot held by the calling run.
8. **Removal and replacement coverage is uneven.** Work missing-provider tests, Runtime no-Agents tests and scheduler reload tests already exist. The reviewed tests do not constitute a systematic matrix for all 30 plugins, alternate capability providers, or the prompt and Goal scenarios above.

## Complete plugin inventory

Names below omit the `NetPI.` prefix. "Executor" means IAgentRuntime, "scheduler" means IAgentScheduler, and "prompt builder" means ISystemPromptBuilder. These are shared contracts, not references to concrete plugin assemblies.

| Plugin | Cross-plugin capability or enhancement | Behavior without it / ownership |
| --- | --- | --- |
| Agents | Model providers; optional executor; shared prompt section and physical leases | Scheduling/ledger load independently. Budget continuation reports availability; Runtime fallback consumes guidance. Host admission survives replacement. |
| AgentsMd | Hook and shared prompt-section consumers | Owns instruction discovery/notices; Runtime fallback and Context both consume guidance. |
| Ask | Optional executor for subagent identity and yielded waiting | Uses ordinary cancellable waiting without executor. Preserve and test both waiting modes. |
| Backup | Host database and settings only | Independent. Full database backup includes inactive plugins' tables. Preserve this storage-neutral boundary. |
| Compaction | Any model provider; optional scheduler and executor snapshot | Summarization uses host model catalog; no scheduler means no acquired lease. Manual compaction works without Runtime. Test admission and model-resolution behavior. |
| Context | IPromptSection contributors; optional executor snapshot; MCP tool-change event | Builds/previews without Runtime; owns cache/previews/notices. Compares durable revisions and adopts compatible Runtime prefixes. |
| Decide | External endpoint; optional URL setting, scheduler/catalog/physical leases | Works without AiProxy. IDecisionService and RPC adapters share bounded admission; trusted same-model callers reuse actual leases. |
| Diagnostics | Optional executor, scheduler, Context, AgentsMd, Skills, Work and RPC views | Retains partial data with explicit missing-component reasons; diag.capabilities separates registrations from endpoint health. |
| Goal | Executor for starting/continuing; hooks/observations | Stores goals independently; executor loss saves execution-unavailable. Return requires explicit resume. |
| Guardrails | Hook consumer; optional executor for yield; optional Decide second opinion | Rules and approval waiting retain their base behavior. Keep missing/failed opinion on the approval path. Test replacement and cancellation. |
| Ideas | Optional IGitHistory, IDecisionService/RPC, models, scheduler, hooks and session data | CRUD remains independent; capabilities RPC reports optional presence. Background outcomes expose wait/drop reasons; save/completion proposals require verification and revision/activity checks. |
| Loops | Hook consumer; optional IDecisionService/RPC | Deterministic checks remain active. Full-context/routing/skill checks are opt-in hints; unavailable decisions skip only model checks. |
| Mcp | Shared ToolSelection, tool-call dispatcher; optional executor snapshot; Context observes its change event | Catalog/connection management and search load independently. mcp_call explicitly requires a compatible dispatcher. Preserve eligibility/revision guards and check alternate consumer behavior. |
| Nudge | IAgentHook consumer | Independent extension; inert until a compatible executor invokes hooks. No concrete peer required. |
| Profiles | Shared SessionIdentity/SessionTools/SessionPrompt metadata; hooks | Persists identity, tools and prompt revision together; no Context RPC required. Both prompt consumers honor durable intent. |
| Providers.AiProxy | Host model catalog and middleware contracts | Independent provider including extra configured endpoints. No Anthropic/OpenRouter dependency. |
| Providers.Anthropic | Host model catalog and middleware contracts | Independent provider. No AiProxy/OpenRouter dependency. |
| Providers.OpenRouter | Host model catalog and middleware contracts | Independent provider. No AiProxy/Anthropic dependency. |
| Retry | IModelMiddleware consumer | Independent. Applies to any caller through the host model catalog, including background model calls. |
| Runtime | Any suitable provider; optional scheduler, prompt builder, hooks/observers; shared SessionModel | Runs without Agents or Context. Fallback freezes identity/contributions/guidance, appends environment changes; physical admission survives shutdown timeout. |
| Skills | Hook consumer and shared tool/session state | Owns catalog/load notices and skill tool. No Context or file-tools RPC requirement. Preserve. |
| Todo | Hooks/session data; optional decisions | Owns checklist/fork restoration without Goal. Opt-in commit checks suggest checklist updates to the agent. |
| ToolRepair | Hook consumer and shared tool-call contracts | Independent extension. No dependency on a particular provider or tool plugin. Preserve. |
| Tools.Agents | Executor; optional scheduler for choices/status | Registrations follow executor availability; invocation checks removal races. Unscheduled model-ref delegation remains available. |
| Tools.Files | Sessions/projects; external Git | Independent file tools/tab; IGitHistory and files.commits expose bounded patch evidence. |
| Tools.Media | Host ToolContext and image-result contract | Independent image tool; runtime renders results through shared contracts. No Web or Files plugin call. |
| Tools.Shell | Host event bus and tool/output contracts; installed shells | Independent process registry and tools. Work optionally displays/control its processes. Shell availability is an external dependency. |
| Tools.Ssh | Host ToolContext; external ssh/configuration | Independent SSH tools; no Shell or Files plugin invocation. Guardrails can enhance tool execution through hooks. |
| Tools.Web | Host ToolContext; external HTTP/browser/search backends; desktop.capture RPC for app screenshots | Browser/fetch/search are independent of peer tool plugins. App-window screenshots require the desktop capability, which is a shell feature rather than another plugin. Preserve specific unavailable error. |
| Work | Optional agents.list/resources, runs.list, processes.list, usage.summary, ideas.work; titles | Independent sections show instances, physical capacity and background outcomes. Actions follow availability; disposed state rejects late responses. |

## Target design

### Small shared contracts, feature ownership in plugins

Use the existing service/RPC/tool registries as discovery. Services describe capabilities by shared interface; stable RPC names are valid optional public contracts. A named RPC is not inherently a bad dependency when its absence affects only the relevant enhancement and its error semantics are documented.

Add typed service contracts only for integrations with multiple callers and significant payloads: decision evaluation (Decide, Ideas, Loops, Guardrails) and git history (Files, Ideas). Candidate names are IDecisionService and IGitHistory; finalize minimal request/result types when implementing. Keep DTOs in NetPI.Abstractions and implementations in their plugins. Retain existing RPC endpoints as adapters for the UI and external callers.

Expose availability using existing rpc.list, services.list and tool registry information. Use plugins.changed/tools.changed for refresh. If service-only replacement needs a finer signal, publish a small host registry-change event containing contract identifiers and registration ownership; keep CLR types and plugin objects out of event payloads. The host retains registry/lifecycle infrastructure; feature status and policy remain plugin-owned.

Shared session metadata such as SessionIdentity, SessionTools and SessionProfile is a valid integration contract. Document which plugin writes each value and which capability consumers interpret it. Each plugin continues to own its tables; other plugins access its data through its public capability or RPC.

### Consistent optional-integration behavior

For every enhancement define: the capability used, the base behavior, the unavailable reason, the failure behavior, cancellation, and behavior on provider return.

Distinguish unavailable, empty, timeout/failure, and user-disabled. UI-facing reasons go in result Details or RPC status fields; concise actionable information goes in model-facing Content where relevant. Keep existing payloads compatible by adding fields.

Resolve services per operation. An operation may retain its capability for the duration of that operation, with a clear cancellation/completion policy; queued or future operations must re-resolve after replacement. Handle disappearance between availability check and execution as well as initial absence.

No startup wait for another plugin. Returning capabilities refresh availability. For potentially autonomous work, availability returning does not itself grant permission to resume a paused goal.

## Implementation sequence

Each numbered item is a separate change and commit in the same isolated worktree; merge each after its required checks before starting the next.

### 1. Establish the independence gate

- Add a build-reference check covering all plugins and imported props/targets: allowed dependencies are shared contracts and framework assemblies; flag references to peer plugin projects or DLLs and cross-plugin source links.
- Extend the existing fake host/test contexts with per-plugin startup/stop smoke coverage. Include no peers, providers arriving later, and alternative shared-capability implementations.
- Record the complete matrix above and base/optional/operation-required classification in docs/PLUGINS.md.
- Start with current expected base behavior; add regression tests for each bug in its owning implementation change rather than making this initial gate red.

Validation: Host build/plugin tests; owning suite for each newly covered lifecycle. Every plugin must load/stop without concrete peers, registrations must be released, and startup must not issue blocking calls to absent peers.

### 2. Make prompt composition independent of Context

- Runtime's minimal fallback honors SessionIdentity, registered IPromptSection contributions in order, tool PromptGuidelines and subagent instructions.
- Define identity replacement, guideline deduplication and failing-section isolation so fallback and Context agree on those shared meanings. Keep Context's richer prompt storage, previews, tool-change history and project notices in Context.
- Give fallback prompts a stable per-session policy. Honor explicit identity/profile changes without making ordinary settings, model or project changes rewrite previously sent history. Keep ordinary changing environment information in notices.
- Cover Context absent at first call, removed mid-session, returning later, and a replacement ISystemPromptBuilder. Preserve the prompt-prefix and fork behavior already tested by Context.

Validation: Agent ContextTests/ProfilesTests plus targeted E2E prompt/profile tests. Demonstrate Runtime + provider + Profiles honors profile identity without Context; other section contributions still reach the model. Existing freeze, fork and guideline tests remain green.

### 3. Replace Profiles -> Context cache invalidation with session intent

- Add a durable session prompt revision/invalidation marker for an explicit profile/identity change. Profiles writes the revision alongside shared identity/tool metadata.
- Context reacts to shared session change intent and compares the durable revision before reusing persisted prompts. The Runtime fallback and alternate builders have the same documented interpretation.
- Keep context.reset as a supported external command; Profiles no longer needs to call it.
- Preserve prompt snapshots at fork points and rebuild exactly when the explicit switch requires it. Define compatibility for stored prompts that predate the revision.

Validation: Agent ProfilesTests/ContextTests, host session-change tests if changed, and targeted E2E. Exercise switching profile with Context off, returning Context, returning to no profile, repeat switch to the same profile, missed events, restart and fork. The next model call must have the chosen identity and tool set.

### 4. Add availability for executor-dependent features

- Goal keeps stored goals accessible, but requests that start/resume work without an executor return a clear execution-unavailable reason. A running goal that loses its executor is visibly paused/unavailable; distinguish that state from user pause. Capability return enables explicit resume.
- Tools.Agents dynamically exposes tools only when an executor exists. Use scoped registrations, resolve at invocation, and still handle the race where it disappears after registration.
- Agents budget.allow reports whether continuation could start; agent choices avoid instructions to use absent delegation tools in model-facing content.
- Delegate Runtime's configured-agent model lookup through the scheduler/shared configuration contract where available; preserve persisted-session model selection and no-Agents behavior.
- Publish tool availability changes through the existing event/notice mechanisms with a cause.

Validation: Agent GoalTests, SchedulerTests, agent-tool tests and targeted E2E. Toggle/replace an executor during idle, queued, running and yielded states; verify saved state, notices, tool availability, no stuck waits, and no unintended goal restart.

### 5. Formalize decision and git enhancements

- Add the minimal shared decision and git-history contracts; Decide/Files register implementations. Migrate Ideas, Loops and Guardrails to per-use lookup with feature-specific fallback.
- Preserve Decide RPC compatibility and explicitly document decide.baseUrl precedence and legacy providers.aiproxy.baseUrl inheritance. Test Decide independently of AiProxy; the external decision endpoint remains its own requirement.
- Ideas reports commit watching, recall, matching and save-suggestion availability separately. Basic backlog operations remain usable without Files, Decide, Agents or Runtime. Treat a missing enhancement as unavailable rather than a successful negative answer.
- Preserve queue bounds, paid-model policy and pending work/cursors across missing, failed or replaced capabilities. Decide's direct HTTP RPC calls have no shared admission; decide.parallel bounds the items of one Decide tool execution. Define admission ownership at the shared decision boundary, including callers that already hold a same-model slot, and test against double acquisition or same-model deadlocks. Keep decision endpoint/model availability distinct from model-provider catalog availability; report missing metadata rather than requiring AiProxy to be loaded implicitly.
- Loops continues its deterministic hints; Guardrails continues rule enforcement and user approvals when decision evaluation is unavailable or fails.

Validation: Aux IdeasCommitTests/IdeasCheckTests/IdeasTests/IdeasNoticeTests/LoopTests and Agent GuardrailsTests; targeted E2E for affected areas. Test missing and alternate implementations, endpoint offline, cancellation, reload between lookup and call, bounded scheduler admission, and cursor retention for pending work. Rebuild every plugin after any Abstractions addition and run the full E2E gate once.

### 6. Align UI and diagnostics with capabilities

- Cache available RPC methods in app state and refresh on registry/plugin changes and reconnect. Expose that discovery to plugin UI contexts through an additive API if needed.
- Work retains independent sections; action availability is checked independently of list availability. Clear stale rows/actions on removal and handle invocation races.
- Diagnostics ContextView shows unavailable components alongside the remaining useful data. Keep genuine provider errors distinct from absent features.
- Disable composer sending with a clear reason when no executor exists; retain session/project browsing. Gate profile, delegation, budget, idea and context actions by available operation.
- Extend Diagnostics with per-plugin base features, active enhancements and unavailable reasons using registry data plus plugin-owned feature status. Keep this reporting in Diagnostics.
- Document changed RPC/events in PROTOCOL.md and any settings in SETTINGS.md. Build and commit bundles only for UI source changed by this work.

Validation: UI tests and targeted browser/E2E tests for live disable/enable, reload and reconnect; run npm build for changed UI. Partial pages remain usable, actions recover when capabilities return, and absent features do not produce repeated error toasts.

## Test and completion criteria

Reuse existing console suites rather than adding a new test framework. Existing footholds include Aux PanelTests (Work without providers), Agent SchedulerTests (without Agents and during reload), Agent ContextTests, ProfilesTests, GoalTests, GuardrailsTests, and Host PluginTests/ModelCatalogTests.

For each actual integration edge exercise: absent at startup; present; a different implementation; removed; replaced; failing; cancelled. Also check useful base behavior with all optional peers absent. Avoid all possible plugin subsets; targeted edges plus a minimal runnable chat and the complete configured harness cover the intended boundaries.

Work with the repository's test loop: one broad unit baseline when implementation starts, then only failing/affected tests while fixing; full unit and E2E gates before each merge. During iteration select E2E with -Changed or -Only. After an Abstractions change rebuild all plugins and run the full E2E gate once. Fix intermittent failures with evidence and controlled mock timing, then measure affected scenarios with -Repeat/-Fresh.

Completion means:

- All 30 plugins build/load/stop independently of concrete peer plugins.
- Each capability requirement and optional enhancement is documented and testable.
- Runtime plus a provider remains a usable chat without Agents or Context, and profile identity/contributed guidance remains correct.
- Every missing operation has an explicit reason; unrelated features continue working.
- Provider replacement and reload neither strand pending work nor cache obsolete plugin objects beyond an operation's lifetime.
- Independent startup plus targeted removal/replacement tests pass, as do required full merge gates.

## Review limitations

The original findings were static. The implementation pass builds/tests in an isolated worktree, including real-host per-plugin startup/stop and mock-provider/browser scenarios. It does not deploy, toggle the running app, benchmark the real model, or establish endpoint health. External executables/services remain separate from plugin dependencies.

