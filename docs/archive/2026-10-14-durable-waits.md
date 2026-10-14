# Durable waits for the user, no drain, the shell's own reload hold (2026-10-14)

Follow-up to the reload gate (`IReloadGate`, merged as `ebc581c` the same day). Owner's decisions: the whole thing rather
than a narrower hold; of the plugin state that outlives a run, protect the shell's processes (browser tabs and MCP
connections stay as they are).

## Why

The reload gate made every agent run hold every plugin reload. Five tool calls waited for the user *inside* a run, in
memory (`IAgentRuntime.WaitYieldedAsync` on a `TaskCompletionSource` in the owning plugin): `ask_user`, a guardrail's
`ask:` rule, `plan_submit`, `plan_enter` and a proposed schedule. So an unanswered question kept its run alive, and with
it held every reload; a runtime reload would cancel it, the owning plugin's reload withdrew it, a restart lost it. The
gate's drain (`plugins.reloadDrainMinutes`) then made it worse: after 30 minutes a user's `agent.send` waited inside the
RPC, before the message was stored, behind a question nobody was answering. Separately, the shell's `StopAsync` killed
every process it started, so a Shell reload killed a dev server a chat left running, with no run active at all.

## What was built

1. **No drain** (`fec3fd6`): nothing waits for a deferred reload; a busy app keeps the old versions until it is idle, or
   until the next start.
2. **A plugin's own reload hold** (`1f0304e`): `IReloadGate.HoldOwn` holds only that plugin's reload; a batch applies the
   others. The shell's process registry holds it while any process tree may be alive. A batch also asks the holds again
   before each plugin, so a run a plugin's start begins from inside the batch stops the rest instead of having it
   swapped under it.
3. **Durable waits** (`82eb920`): `ToolResult.WaitForUser(UserWait)` / `ToolCallDecision.Wait` end the run at the call
   (outcome `waiting`, the agent `Yielded` with the wait's activity, no slot, no hold). The runtime keeps the wait in the
   agent record (`agent_records.pending`), `MarkInterrupted` leaves it alone, `IAgentRuntime.Waits` lists them,
   `ResolveAsync` answers one (a result, or `Allow`: the call runs, the hook that asked reads `turn.AllowedCalls`), and
   when every wait of the batch is answered a run finishes the batch (without taking a model slot first, so an answer is
   not lost when the chat's agent went away meanwhile) and calls the model. A user's message instead ends them with the
   owner's `IfSteered`, a stop with `IfCancelled`; anything else for the chat queues. Events `agent.waiting`,
   `agent.waitEnded`; RPC `agent.waits`. A subagent, and a call from inside a script (`codemode`), cannot wait.
4. **The owners onto it**, keeping no state of their own: Ask (`c685236`), Guardrails (`4ac5ee1`; `AllowedCalls` carries
   the whole wait, so the rule that was allowed passes and another rule of the same call still asks), Plan (`87cca58`),
   Schedules (`96666a0`). Their `*.pending` lists are the runtime's waits, their card events are relayed from the
   runtime's, and a reload of any of them withdraws nothing (the old `withdrawn`, `stopped` and `replaced` results are
   gone; the UI still renders them for old transcripts).
5. `WaitYieldedAsync` removed; the goal plugin neither pauses nor continues a goal while a run waits (`07078cd`).

## Results

- Unit suites (all six, the contracts changed): Tools 113 passed / 1 skipped, Agent 290, Providers 104 / 2 skipped,
  Storage 118, Host 236 + 1, Aux 372 + 1 / 3 skipped. The two failures were already failing on master (a fake ssh
  started without `CreateNoWindow`; the Aux read-only RPC list missing `processes.killCall`) and are fixed in `d226f48`.
- New tests: `waits:` (8, Agent: answer, steer with a queued notice, stop, a runtime reload, a hook's wait with two
  asking calls, two waits in a parallel batch and an answer while the batch runs, a steer while it runs, a subagent);
  `ask_user: a question survives a reload of the ask plugin and of the runtime`; the guardrails and plan reload tests
  rewritten to "the wait survives the reload"; the Schedules tool tests on a fake runtime that holds the wait;
  `goal: a run that ends waiting for the user`; gate and manager tests for own holds (Host); the shell's hold (Tools).
- A flaky test found on the way: the subagent-workspace tests' fake model spawned a new child on every report, so the
  parent was idle only for moments (6 children in half a second) and the idle check timed out under a loaded suite run
  (2 of 2 full runs); fixed in `82eb920`, now one child.
- E2E (whole suite): 92 passed, 8 failed — `mcp.deferred`, `ui.mcp-panel`, `ui.webhooks-tab`, `ui.smoke`,
  `ui.chat-decisions`, `ui.work-tab`, `ui.tool-cards`, `ui.dev-proxy` — every one with the same checks on master
  (`ebc581c`, run `20261014-184810-1edd`); five already failed on 2026-10-13. Left as a separate task.

## Not done

- Browser tabs and MCP server connections are still lost when their plugin reloads (owner's choice).
- A nested call from a `codemode` script that would wait for the user is refused with a pointer to make the call
  directly; before, the script waited in memory.
