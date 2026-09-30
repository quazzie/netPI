# MCP client plugin

Status: proposed implementation plan; no implementation or deployment performed.
Date: 2026-09-30. Reviewed NetPI baseline: 9fe2a96.
Scope confirmed by the user: NetPI uses external MCP servers. Deferred discovery and minimal model context are requirements, not a later optimization.

## Outcome

Add plugins/NetPI.Mcp (plugin id netpi.mcp). User-configured MCP tools remain selectable in the tools picker, profiles and subagent allowlists, but their definitions stay out of model context by default. Two small discovery/invocation tools expose only what the task needs, while execution preserves the existing runtime hooks and result handling. Keep protocol, subprocess and connection logic inside the plugin. No plugin-to-plugin assembly references.

The earlier decision in docs/archive/2026-09-26-harness-gaps.md dropped MCP in favor of native plugins. This proposal revisits that decision for access to existing external servers; native plugins remain the home of NetPI behavior. Update docs/STATUS.md when implementation is accepted; preserve the historical archive.

## Source findings and constraints

- IToolRegistry.Register returns an IDisposable. ScopedToolRegistry tracks it for unload; PluginScope compacts disposed registrations. Dynamic tool catalogs need no new registration interface.
- ToolDefinition.Name uses lowercase letters, digits and underscores. MCP names can be longer, case-sensitive and contain punctuation. Preserve exact remote identity separately.
- ToolDefinition.Parameters is a JsonObject. ToolResult supports Content, IsError, Images and UI-only Details; structured MCP data required by the model must also reach Content.
- Context/ToolNotices compares names only. ToolChanges recognizes plugin reloads, profiles and existing settings, but has no remote-server cause. New events alone will not fix this: extend the consumer and tests.
- Plugin replacement starts the new instance before stopping the old. StartAsync must tolerate overlapping subprocesses and cannot return with an empty replacement catalog while promising a seamless swap.
- The repository uses framework libraries and NuGet-free console tests. Implement the required MCP subset with System.Text.Json, Process and HttpClient; do not add an SDK/package dependency.

## Deferred discovery: required default

The user explicitly wants as little context clutter as possible. The original eager model-facing registration design is superseded by this section. Discovery, execution eligibility and model visibility must be separate.

### Research findings

- [OpenAI tool search](https://developers.openai.com/api/docs/guides/tools-tool-search) supports hosted and client-executed search. Discovered definitions are injected at the end of context to preserve caching. In the Responses API this requires a supported model; ordinary OpenAI-compatible endpoints cannot be assumed to implement it. Individually deferred functions still expose their name/description, while grouping by namespace/server hides individual function details until discovery. Client-executed search can return schemas absent from the initial inventory.
- [Anthropic tool search](https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool) uses defer_loading and tool_reference blocks. Complete definitions are still sent to the API, but deferred schemas stay out of the initial model prefix; references expand inline. Custom search can return references, and the corresponding definitions must exist in the tools request. API payload size and model context size are different measurements.
- [Anthropic progressive disclosure](https://www.anthropic.com/engineering/code-execution-with-mcp) also describes provider-independent search with configurable detail: names, summaries or full schemas. It describes filtering large results outside model context. Code execution is another approach, but adding a general execution environment is outside this plugin's first milestone.
- MCP catalog discovery remains a client-side concern: tools/list supplies the catalog; there is no need to depend on an unverified server-side search extension. Cache/index schemas in NetPI memory, not in the prompt.

### Recommended portable behavior

Keep existing native tools as they are. MCP contributes only two compact schemas to normal model requests:

1. mcp_search: natural-language query, optional server filter, detail=summary|schema, and bounded limit (default 3). Summary results contain stable tool id, server, one short description and availability. Schema mode returns only explicitly requested definitions, with default limit 1. Return JSON without duplicated explanatory prose.
2. mcp_call: stable tool id, discovery revision/token, and arguments object. Its schema must not enumerate remote tool names or embed a union of their argument schemas.

The agent searches for a capability, inspects the best match's schema, then calls it. Permit exact-id schema lookup within mcp_search. Search never invokes a remote tool. Do not auto-load all siblings from the matched server. Repeated schema lookup can return a short reference only when that exact revision remains in the model's current context; after compaction, return it again.

Index tool names, descriptions, server labels and argument names/descriptions locally. Start with deterministic lexical/BM25-style ranking using framework code, exact-id matching and optional user synonyms. Do not require embeddings or another model call. Evaluate realistic natural-language searches, including weak matches and ambiguous verbs; return a small shortlist and a clear no-match response, never the full catalog as fallback.

Apply the current session/profile/subagent eligibility filter before search results. Discovery grants no additional permission. Keep a per-session record of disclosed schema revisions; fork only corresponding records whose discovery messages were actually copied, and children begin undisclosed. Discovery records must not live only in a plugin instance: reconstruct from persisted JSON metadata and handle compaction deliberately.

### Runtime integration required

Add a default-false Deferred property to ToolDefinition, or an equivalent additive contract agreed before implementation. Catalog proxies remain registered through ctx.Tools so existing selectors can control each remote tool. Runtime and Context must distinguish all eligible tools from model-visible definitions; prompt guidelines, token estimates, context.preview and tool-definition history use the visible set. Selection UI and execution authorization use the eligible set. Do not merely remove tools in provider serialization: that still leaks their guidelines and misreports context usage.

Introduce a small generic indirect-call resolution contract in NetPI.Abstractions, implemented by mcp_call. Resolve to a concrete registered target and inner arguments in the runtime before target policy hooks. The runtime checks both gateway eligibility and target eligibility, rejects recursive delegation, and rechecks after policy changes. Never call a target's ExecuteAsync directly inside the gateway.

Run before/after hooks against the effective target identity and arguments, and preserve hook blocking/argument changes. Persist the assistant's original mcp_call envelope and matching result identity; expose resolvedTool/serverId in additional event/UI metadata. Keep exactly one result per original call id. Target removal/disable between search and execution blocks the call. A changed schema revision requires fresh discovery; harmless transport reconnect alone need not invalidate the schema.

With explicit allowlists, enabling a gateway must not implicitly enable every remote target. Define infrastructure visibility consistently: an eligible deferred target may make discovery/invocation available unless the gateway itself was explicitly disabled; an allowlist containing only mcp_call must grant zero remote targets. Test profile inheritance and explicit subagent selections. Tool choices are existing NetPI selection semantics, not a new security sandbox.

Keep the first version serial for indirect calls; use target read-only classification only after it is resolved and local trust policy permits it. Parallelization must not use a remote annotation or the outer gateway's generic classification.

### Context and output budgets

- Initial MCP model-facing text should be bounded independently of catalog size: just the two compact schemas and one short instruction to search before calling. No server-by-server inventory, tool-name stubs, long manuals or connected-server instructions copied into the frozen prompt.
- A normal search returns at most three summaries; schema inspection returns one selected schema. Add configurable character budgets and complete, well-formed results. If a schema exceeds the budget, provide a schema artifact/handle that can be inspected explicitly; never silently truncate a schema or omit its constraints.
- Keep result payloads small too. Return useful text/structured data within the normal result limit; large results are saved once with size/format and a short preview plus a file reference. Support server-provided pagination/filters; do not invent a summary that hides important errors or changes meaning. General local result projection/code execution is a later feature.
- Retain disclosed schemas in append-only tool-result messages, not by adding them to the top-level tools array on every request. This keeps the portable tools prefix stable. Old schema messages still consume tokens until compaction; deferred loading is not zero context cost.
- Catalog refreshes do not announce every hidden tool. UI gets full events; a chat gets a brief notice only when a disclosed/pinned tool's definition or eligibility changes. An undiscovered server with 1,000 tools must not add a 1,000-name notice.
- Optional user-pinned tools may be eager, with visible token cost. Default is zero pinned MCP tools.

### Native provider optimization: follow-up, not a dependency

Once the portable path is measured, add optional native deferred-loading adapters for supported Anthropic and OpenAI Responses models. Preserve discovery/reference items in provider-neutral history through additive contracts; teach streaming, replay, fork and compaction about them. Anthropic requires deferred inventory in the API payload; OpenAI client-executed search can avoid declaring the whole catalog. Group when required to avoid thousands of name/description stubs. Capability must be explicitly known, not inferred from an OpenAI-compatible URL or model-name substring.

Reuse the same search index, eligibility checks, catalog revisions and target dispatch. Do not bolt defer_loading onto current provider JSON without handling the returned search/reference events. Unsupported/local models keep the portable path. Never transparently retry an executed tool because an adapter failed.

### Acceptance measurements

First milestone includes deferred discovery; do not ship eager loading and defer this requirement.

Test 10, 100 and 1,000 synthetic catalog entries with realistic schemas. Assert that the initial serialized model-visible tools, MCP prompt guidance and initial notices remain constant for the portable mode. Capture actual provider requests, not just registry counts. Record characters, provider token counts where available, first successful-call latency, search recall/top-3 selection, schema inspections and argument errors. Do not claim a vendor's reported savings as NetPI measurements.

Verify a task loads only its selected schema; the next model request adds no remote schemas to the tools prefix; repeated search does not duplicate definitions unnecessarily; compaction permits rediscovery; profile-disabled targets cannot be searched or called; guessed ids cannot bypass schema disclosure; stale revisions fail clearly; another chat/child does not inherit disclosed state; failed searches do not dump the catalog; hidden catalog changes do not spam notices; effective-target hooks run once with real arguments; images/results and cancellation still work.

## Protocol compatibility

Use a version-specific protocol layer behind common discovery/list/call operations. Deliver tools only initially; do not advertise sampling, elicitation, roots, resources, prompts or tasks before implementing them.

The official 2026-07-28 revision introduced a stateless lifecycle; older servers use initialize/notifications/initialized. Implement modern discovery and request metadata plus legacy initialization as separate paths. Start with explicitly tested support for 2026-07-28 and 2025-11-25. Add older revisions only with fixtures; fail clearly on an unsupported negotiated revision.

For stdio, follow the specified server/discover probe and legacy fallback rules. For HTTP, follow its own compatibility rules, including distinguishing modern JSON-RPC method errors from legacy endpoint errors; do not treat every 404 or authentication failure as a reason to downgrade. Select one mutually supported revision and use its framing, capabilities and cancellation semantics consistently. A recognized modern version error must not cause legacy initialization.

Implement paginated tools/list. Validate and bound remote message sizes, catalog size, schema depth, repeated cursors and subscription traffic. Unsupported legacy server requests receive a protocol error rather than hanging. Unexpected modern input_required results produce a clear unsupported-interaction error; never invent user input or repeat a call to bypass it.

Primary references:
- [Current release and compatibility context](https://blog.modelcontextprotocol.io/posts/2026-07-28/)
- [stdio, probing and shutdown](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio)
- [Streamable HTTP and compatibility](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http)
- [Tools, pagination, names and results](https://modelcontextprotocol.io/specification/2026-07-28/server/tools)

## Configuration and identity

Use an mcp.servers map in settings.json, keyed by a stable user-chosen server id. No implicit project-file discovery or automatic server installation.

Each entry has enabled, transport, connection fields, timeouts, and an optional exposed-tool allowlist. For stdio: command, args array, fixed absolute cwd (default NetPI workspace), and environment overrides. For HTTP: URL and header values supplied through environment-variable references. Configuration/status responses redact credentials. Do not forward the entire inherited environment to subprocesses; document the minimal inherited platform variables and explicit overrides.

Server processes are shared across chats. A session project switch does not change their cwd or restart them. Project-specific server instances and roots are later features.

Expose a bounded name such as mcp_<server_slug>_<tool_slug>_<hash>, with a deterministic hash of the original server id and exact remote tool name. Retain the exact mapping; check collisions and provider length limits. Names must survive reconnects and restarts and distinguish case/punctuation variants. Never shadow native tools.

Clone schemas into immutable definition snapshots; preserve constraints and descriptions rather than silently flattening them. Reject incompatible definitions individually with visible diagnostics. Tool descriptions identify the server; keep the full remote description in Help where shortening is necessary. Respect NetPI's existing help handling when the remote schema itself declares a help argument.

Default ReadOnly to false. Remote annotations are metadata, not local trust policy. Allow an explicit user-owned override for trusted tools before enabling parallel read-only execution.

## Components

Suggested plugin files: McpPlugin.cs, ServerManager.cs, ServerConfig.cs, McpConnection.cs, JsonRpcPeer.cs, StdioTransport.cs, HttpTransport.cs, ProtocolVersions.cs, RemoteTool.cs, ToolCatalog.cs, ToolSearchIndex.cs, McpSearchTool.cs, McpCallTool.cs, DiscoveryState.cs and ResultAdapter.cs. The runtime owns generic deferred visibility and indirect dispatch; the MCP plugin owns protocol, search and disclosure behavior.

ServerManager owns connection generations, reconnect tasks, pending requests and registration handles. RemoteTool resolves its current server generation per call. Use a lock for stdin writes and request-id correlation for responses; one stdout reader and a separate bounded stderr reader. Drain both continuously. Stderr output alone is not a failure.

Register through IPluginContext and tie workers to Stopping. Use JSON nodes/elements at event/RPC boundaries; do not retain plugin-defined types in host-wide object containers or JSON caches. StopAsync cancels workers, settles pending calls, awaits readers and closes only the subprocesses this instance owns, escalating to bounded process-tree termination when needed.

## Tool results and failures

Convert text and textual embedded resources to Content; include structuredContent as JSON when not already represented. Map image blocks to existing Images. Retain resource links and binary/audio metadata in Details and describe unsupported model-facing media explicitly; never put base64 blobs into Content. Bound binary payloads before decoding. Do not automatically fetch resource links.

Preserve isError and distinguish protocol errors, server tool errors, cancellation, timeout, authentication failure and transport loss. Include server/tool identity and an actionable message. Redact secrets in diagnostics, progress and stderr.

Honor the caller cancellation token and plugin stop token. Send the cancellation signal appropriate to the chosen version and transport; ignore late responses after completion. A timeout does not prove that a remote side effect did not happen. Never automatically replay tools/call after connection loss, even when a tool claims to be read-only. Retry connection setup and discovery with bounded backoff independently.

## Catalog changes, outages and reloads

Apply complete validated catalog snapshots: register replacements before disposing superseded handles; leave unchanged definitions untouched. Do not replace a good catalog with a partially fetched one.

A temporary outage retains known tool proxies. Calls fail promptly with server-unavailable rather than making the tools vanish from profiles and sessions. Show reconnecting/failed status and an explicit reason. Remove tools on server disable/delete, exposure-policy changes or a successful discovery that confirms their removal. Initial offline servers have no tools until discovery succeeds.

Publish mcp.serverChanged for status and mcp.toolsChanged for catalog diffs: serverId, generation, reason, added, removed and updated names. Suggested reasons: discovery, catalog-refresh, configuration, disabled, deleted. The ordinary registry tools.changed event still drives refresh.

Extend ContextPlugin/ToolChanges/ToolNotices/ToolSets to consume remote-change evidence and track definition revisions, including schema/description changes under the same name. Append brief notices only for affected disclosed or pinned tools at the next model call; undiscovered catalog changes stay in UI events. Never rewrite the frozen system prompt or old messages. Preserve user/profile precedence and the existing notice metadata; document new optional fields and cause values. Do not infer a remote change from a generic plugin reload.

For plugin swap, prepare replacement connections/catalogs within a bounded startup window while the old instance serves. If replacing a currently healthy server would fail or its subprocess requires exclusive ownership, fail candidate startup and retain the old plugin; clean up every candidate resource. Initial cold startup may succeed with some servers offline. Determine old healthy state through a JSON RPC snapshot before registering replacement RPC methods, without caching old plugin objects. Allow active old calls a bounded drain; cancel remaining calls with explicit errors at retirement. Do not promise uninterrupted long-running calls or support for singleton subprocess servers until tested.

## Delivery sequence and acceptance

1. **Stdio vertical slice.** Configuration validation, modern/legacy protocol paths, paginated discovery, deferred catalog proxies, local search/schema inspection, portable invocation through effective-target hooks, text/structured/image results, cancellation and diagnostics. An isolated scripted server must be callable by MockLlm through the normal runtime.
2. **Lifecycle and context.** Reconnect behavior, catalog diffs, definition-change notices, profile/session/subagent selection, shutdown and plugin replacement. Test these before calling the stdio milestone complete.
3. **Streamable HTTP.** JSON and SSE responses, version-specific headers/metadata, modern subscriptions and legacy sessions/notifications, bounded streams, request cancellation and explicit environment-backed credentials. Implement required modern parameter-to-header validation/mirroring. No automatic legacy HTTP+SSE fallback.
4. **Management UI.** A Svelte plugin tab to add/edit/remove/enable servers, inspect connection status and tools, choose exposed tools, and reconnect/refresh. Confirm configuration without losing edits when a connection fails. Keep narrow panels usable. Preserve the existing session tools picker.

Proposed RPCs: mcp.list and mcp.tools (readOnly true); mcp.save, mcp.remove, mcp.setEnabled, mcp.reconnect and mcp.refresh (mutating). Validate structured input; use existing settings persistence and writer rules. Register a SettingsSection for scalar limits, and edit the server map through the plugin UI/RPC rather than forcing an object through a text setting.

Later separate proposals: OAuth login/token refresh, resources/prompts tools, roots scoped by project, elicitation through ask_user, sampling through budgeted model calls, MCP Apps UI and exporting NetPI as an MCP server.

## Validation and documentation

Add McpTests and deterministic stdio/HTTP fixtures to NetPI.Aux.Tests using the current console harness; use owning Agent/Host suites for context and unload changes, and NetPI.E2E for a complete mocked run. Include solution/build wiring only as necessary.

Required cases: modern and legacy version paths; malformed/oversized frames; concurrent request correlation; pagination failures; stable names/collisions; invalid schemas; image/structured/error results; cancellation/late response; crash/reconnect without tool-call replay; duplicate notifications; configuration races; disabled tools/profile/allowlist filtering; same-name schema updates; partial refresh; reload success/failure; owned process cleanup; collectible plugin unload; credential redaction. HTTP fixtures must cover JSON/SSE, authentication failures, version headers, legacy session loss and modern subscriptions. Use synchronization, not timing-only sleeps.

Run the relevant suites, the normal build/test gate and the UI build when UI sources change. Build only into the worktree's artifacts/dev/app; inspect generated bundle changes and keep only source-related outputs. Document RPCs/events in docs/PROTOCOL.md, settings in docs/SETTINGS.md, result details and naming in docs/TOOLS.md, and usage/lifecycle/limits in a new docs/PLUGIN-MCP.md plus README and docs/STATUS.md. Rebuild every plugin if an additive abstraction change proves necessary.

Done means a configured local and HTTP server work through ordinary agent calls; cancellation and failures are visible; catalogs and notices remain consistent; initial model context stays bounded regardless of catalog size; only task-selected schemas are disclosed; settings and tool selections persist; reload/shutdown leave no owned subprocesses or collectible-context leaks; all scoped tests pass. Live publishing, app restarts and real-server installations are separate from implementation validation.
