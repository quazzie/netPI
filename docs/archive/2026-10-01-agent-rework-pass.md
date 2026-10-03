# Agent rework implementation pass

Archived 2026-10-03: completed implementation and validation record. The measurement subsequently completed in idea-0m4hml, with full-context checks kept off by default; pending-measurement wording below describes the original handoff.

This pass implements capacity/delegation, plugin independence, Ideas verification and decision consumers together. Validation follows implementation, as requested. It builds into the worktree's development output; it does not publish, restart the running app or alter live model settings.

## Behavior

- The host owns plain physical lease descriptors across scheduler replacement. Scheduler stop cancels waiters but keeps active inference counted until its owner releases it. Runtime and background callers retain physical admission while a scheduler is absent. Queue cancellation/deadline attachment occurs under the scheduler gate.
- Work shows shared model capacity once, agent instance limits separately, wait reasons and background Ideas outcomes. Delegation guidance covers a parent plus one helper, a yielding coordinator with two workers, and an explicitly chosen cloud coordinator; it preserves model and budget choices.
- Runtime fallback honors profile identity, ordered prompt sections, role instructions and deduplicated tool guidance. It freezes the sent prefix. Explicit profile changes and context.reset increment durable session prompt revisions. Context adopts compatible fallback prefixes and invalidates incompatible persisted prompts.
- Goals distinguish execution-unavailable from user pause. Executor return allows explicit resume and never automatically restarts goals. Delegation tools follow executor availability; budget permission reports whether continuation is available.
- Decide exposes IDecisionService; Files exposes IGitHistory. Consumers resolve capabilities per operation with RPC compatibility. Trusted same-model callers supply their actual live lease rather than acquiring their own capacity twice; RPC callers cannot claim one.
- Automatic save proposals and completion updates require a read-only, low-priority verifier. Higher-priority queued work cancels an attempt; the lease stays held until the provider finishes. At most three attempts run. Completion requires bounded, complete commit patches and rechecks revision and project activity. Verified completion can apply without a card. ideas.verifyUpdate checks other proposed patches against supplied evidence and uses optimistic revision checks.
- Provider-captured conversation snapshots preserve system text, tool schemas, historical reasoning, calls/results and effort. Opt-in checks supply stuck/finished/ask-user hints, routing/skill hints and commit-to-todo suggestions. They do not automatically change models, tools, spending permission or goal status. Bulk decisions use only a configured bulk model. Automatic consumers reject near ties and invalid probabilities.
- UI RPC discovery follows registry changes and reconnect. Composer preserves drafts without execution. Work controls follow operation availability. Diagnostics retains available components without Context and reports registry presence separately from endpoint health.

## Validation and limits

The independence gate checks all plugin projects and shared/imported build files for peer dependencies, and starts/stops each built plugin without peers. Owning regressions cover reload accounting, cancellation/grant races, profile switches without Context, executor availability, shared decision admission, bulk routing and verifier cancellation acknowledgement. All contract changes require rebuilding every plugin and the full E2E gate.

Real NInfer cache hit rates, next-turn cache survival, latency and hint quality require a separately authorized idle measurement window. Payload equivalence and functional tests do not establish performance benefits. Providers that ignore cancellation retain capacity until they return; this is cooperative cancellation, not instantaneous GPU preemption. Large or unavailable patches leave completion proposals pending for manual review.

Validation: all projects/plugins built with zero warnings/errors; 685 unit cases passed across Providers (45), Tools (69), Agent (160), Host (125) and Aux (286). The full real-server/mock-provider E2E gate passed 65 tests and 832 checks, including browser smoke. Focused mock-browser checks passed 18/18 for physical capacity once, dropped work reasons, draft preservation, partial Diagnostics and capability recovery. A scheduler observation found during review was fixed to wait for actual provider entry rather than the earlier Running status; it then passed 20/20 fresh test processes.

Preserved evidence: `artifacts/agent-rework-pass-2026-10-01/README.md` in the main checkout. No changes were published. Cache/latency measurement remains the explicit unfinished part of idea-0m4hml.
