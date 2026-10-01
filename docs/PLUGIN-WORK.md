# Work plugin (`netpi.work`)

The **Work** tab (right panel) shows what is running right now: the agents you set up (always, with their state and an
on/off switch), runs and subagents, shell processes and
today's token usage. The plugin owns no state. It has one RPC method that collects data from the plugins that provide
it.

- Plugin: `plugins/NetPI.Work`, id `netpi.work`, start order 80.
- Tab: `{ id: "work", title: "Work", panel: "right", icon: "work", order: 10, module: "ui.js" }`. The UI module goes in
  `plugins/NetPI.Work/wwwroot/ui.js` (source in `plugins/NetPI.Work/ui/`).

## `work.snapshot`

Takes no parameters and returns:

```ts
{
  agents: AgentSlots[] | null;       // agents.list        (netpi.agents)
  runs: AgentInfo[] | null;          // runs.list { includeFinished: true }   (netpi.runtime)
  processes: ProcessInfo[] | null;   // processes.list    (netpi.tools.shell)
  usage: UsageSummary | null;        // usage.summary     (netpi.agents)
  time: string;                      // server time, ISO 8601 (for "x s ago" labels)
  errors?: { agents?: string; runs?: string; processes?: string; usage?: string };  // only when a call failed
}
```

- The four parts are fetched in parallel through `IRpcRegistry.InvokeAsync`, with a combined 10 s timeout.
- A part is `null` when its method is not registered (the plugin is missing or disabled) or when the call failed. A failed
  call also leaves an entry in `errors`. The tab should render every section as optional and show a short "not
  available" line for a `null` part.
- The shapes are the ones in `docs/PROTOCOL.md`:

```ts
interface AgentSlots { key; provider?; capacity; busy; queued; models: string[]; owners: SlotHolder[]; waiters: SlotHolder[]; source; status?;
  configured; model?; use?; available; unavailable?; disabled }   // an agent (configured) or model calls without one (see PROTOCOL.md)
interface SlotHolder { agentId; sessionId?; label?; since }
interface AgentInfo { id; sessionId; name; parentAgentId?; parentSessionId?; isSubagent; depth; status; model?; agent?; activity?;
  createdAt; startedAt?; finishedAt?; runs; turns; toolCalls; inputTokens; outputTokens; queuedMessages; task?; result?; error?; children: string[] }
interface ProcessInfo { id; pid; shell: 'bash'|'pwsh'; command; cwd; sessionId?; agentId?; background: boolean;
  status: 'running'|'exited'|'killed'|'timeout'; exitCode?; startedAt; endedAt?; outputBytes }
interface UsageSummary { day; providers: { provider; inputTokens; outputTokens; cacheReadTokens; calls; budgetTokens? }[];
  budget: BudgetStatus /* budget.status */; models: { agent?; provider; model; calls; inputTokens; outputTokens; costUsd; unknownCost }[] /* this period */ }
interface BudgetStatus { monthlyUsd?; dailyUsd?; warnPercent; resetDay; onLimit: 'stop'|'ask'; periodStart; periodEnd; spentUsd; todayUsd; warning; exhausted }
```

`agents` includes finished agents (completed, failed and cancelled subagents), so the tab can show recent results. Use
`parentAgentId` and `children` to build the agent tree.

## Refreshing

Load `work.snapshot` when the tab is shown (`onShow`). Then patch the view from events, or re-fetch with a short debounce
(about 250 ms) when any of these broadcast events arrive:

| event | data | suggested handling |
|---|---|---|
| `agent.status` | `{ agent: AgentInfo }` | upsert the agent by `id` (no re-fetch needed) |
| `agents.changed` | `{ agents: AgentSlots[] }` | replace `agents` |
| `process.started` | `{ process: ProcessInfo }` | upsert by `id` |
| `process.exited` | `{ process: ProcessInfo }` | upsert by `id` |
| `usage.recorded` | `{ provider, model, usage }` | re-fetch (or re-fetch only `usage.summary`) |
| `usage.changed` | `BudgetStatus` | the ledger recorded calls (their cost): re-fetch `usage.summary` |

`process.output` (`{ id, chunk }`, background processes only) can drive a live tail of an expanded process row. The full
output is available through `processes.output { id, tail? }`.

Useful actions from the tab (other plugins' RPCs):

- `agent.abort { sessionId }`: stop an agent.
- `processes.kill { id }`: kill a process tree.
- `ctx.app.openSession(agent.sessionId)`: jump to an agent's or subagent's session.

Pause the event-driven updates while the tab is hidden (`onHide`) and take a fresh snapshot on `onShow`.

Physical owners remain visible through executor replacement, including provider cancellation acknowledgement that exceeds the shutdown timeout. Work shows acquisition time and retirement/cancellation state and can inspect the correlated model call. A returned provider may still belong to a run holding its admission lease.
