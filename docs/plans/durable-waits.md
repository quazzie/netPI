# Durable waits for the user (and what else a reload must not take)

Started 2026-10-14, after the reload gate (`IReloadGate`, master `ebc581c`). Owner's decisions: the whole thing, not a
narrower hold; of the plugin state outside runs, protect the Shell's processes (not browser tabs, not MCP connections).

## The problem

Five tool calls wait for the user inside a live run, in memory (`IAgentRuntime.WaitYieldedAsync` on a
`TaskCompletionSource` in the owning plugin):

| plugin | wait | activity |
|---|---|---|
| Ask | `ask_user` | waiting for your answer |
| Guardrails | an `ask:` rule, before the call runs (a hook) | waiting for your OK |
| Plan | `plan_submit` | waiting for your decision on the plan |
| Plan | `plan_enter` | waiting for your OK to plan |
| Schedules | `schedules add` (a proposal) | waiting for your decision on the schedule |

So the run stays alive while nobody answers. A live run holds the reload gate, so an unanswered question holds every
plugin reload; a runtime reload would cancel it, the owning plugin's reload withdraws it (`WithdrawAll`), a restart loses
it. The drain (`plugins.reloadDrainMinutes`) made it worse: after 30 minutes a user's `agent.send` itself waited, before
the message was stored, behind a question nobody answered.

Separately, the Shell plugin's `StopAsync` kills every process it started, so a Shell reload killed a dev server a chat
left running in the background, with no run active at all.

## Design

1. **No drain.** A deferred reload waits for the first moment no run exists, or the next start; runs never wait for one.
2. **A plugin may hold its own reload** (`IReloadGate.HoldOwn`): only that plugin's reload waits, everything else goes on.
   The Shell plugin holds it while any process it started is alive.
3. **A wait for the user ends the run.** The tool returns `ToolResult.WaitForUser(UserWait)` (a hook returns
   `ToolCallDecision { Wait = … }` before the call runs). The runtime stores the wait with the agent (the agent record),
   the batch stops at that call (later calls of the batch stay open), and the run ends with outcome `waiting`: no slot,
   no hold, the agent shows `Yielded` with the wait's activity, as before. The model's call stays without a result.
   - **Answer** (`IAgentRuntime.ResolveAsync(waitId, WaitResolution)`): `Answer(result)` stores that call's result now;
     `Run()` lets the call run after all (a hook's wait: the hook reads `turn.Resolved`). When every wait of the batch is
     resolved, a run starts that finishes the batch (the open calls, in order) and then calls the model.
   - **The user writes instead** (`agent.send` from the user, not a queued follow-up): each unresolved wait gets its
     `IfSteered` result, the open calls after it are skipped by the steer, then the message, then a run.
   - **Stop**: `IfCancelled` (or the abort marker) for the waits, the abort marker for the open calls; the agent is idle.
   - **Anything else** (a subagent's report, a goal, a schedule or webhook notice) queues until the user answers.
   - The wait survives a runtime reload and a restart (agent record; `MarkInterrupted` leaves it alone) and an owner's
     reload (the owner keeps no state: its `*.pending` lists are `IAgentRuntime.Waits(kind)`).
   - `agent.waitEnded { sessionId, agentId, callId, waitId, kind, reason }`: answered | steered | cancelled | deleted. The
     owners relay it into their own `*.closed` events for the cards.
   - A subagent never waits (nobody watches its chat): the runtime turns a wait into an error result.
4. The four plugins move onto it; `WaitYieldedAsync` goes. Goal: a run that ended `waiting` neither pauses nor continues
   the goal.

## Pieces (one commit each)

1. Host + Runtime: remove the drain; docs.
2. Host: `HoldOwn`; Shell holds its reload while its processes live; tests; docs.
3. Abstractions + Runtime: durable waits (`UserWait`, `WaitResolution`, `Waits`, `ResolveAsync`, `agent.waits`,
   `agent.waitEnded`); Agent tests with a fake waiting tool and hook.
4. Ask, 5. Guardrails, 6. Plan, 7. Schedules: onto durable waits; their tests.
8. Remove `WaitYieldedAsync`; Goal; docs (PLUGINS, PROTOCOL, TOOLS, UI, HANDOFF); full gate (contracts changed).

## Open questions

- None blocking. Browser tabs and MCP connections are still lost on their plugin's reload (owner's choice, 2026-10-14).
