# Plan: agents instead of lanes, 2026-09-25

Status: agreed with the user (2026-09-25); being built in the order below.

## Why

Lanes were a model with parallel slots, plus automatic lanes for every other model; the Work tab showed "0/13" for two
lanes the user had set up, and chats picked a model while subagents picked a lane. The user's idea: the things you
set up are **agents**, and chats and subagents both run on them.

## Design

- **An agent** is a named worker on one model: `agents.<id> = { model, instances, use, disabled, cost, budget: { limitUsd } }`
  (the id is its name; `agents.maxDepth` stays a setting).
  - `instances`: how many runs at once. Default: a local model's slots (qwen3.8-27b: 2), 1 for a cloud model.
  - `use`: the note on when to use it (orchestrators read it); `cost` overrides the price, `budget.limitUsd` caps a day.
  - Several agents on one local model share its slots: NetPI never runs more on the model than it serves, and the
    agent dialog warns when their instances add up to more.
- **Active** only while it can run without disturbing anything:
  - a local agent while AiProxy reports its model **loaded** (NetPI never loads a model: loading one could evict
    what another agent runs; the user switches in AiSwitcher and the agents follow within seconds);
  - a cloud agent while its model is listed and not offline;
  - and not **disabled** by the user (in the agent's dialog, or its switch in the Work tab).
  An inactive agent stays listed, greyed, with the reason ("qwen3.8-27b isn't loaded", "disabled").
- **Chats run on an agent.** The model picker becomes an agent picker (agents with their state, "New agent…" that
  starts from the model list). A chat on an inactive agent gets a notice at once instead of a 2-minute wait; a chat
  whose agents are busy waits its turn. A chat without an agent takes the first agent on its model; with none, a
  notice says to pick or create one. Profiles stay separate: the agent is where a chat runs, the profile who it is.
- **Subagents** start on an agent: `agent_choices` lists the agents (free / busy / inactive, the note, the price, today's
  spend, the budget), `agent_spawn { agent }` takes one (a busy one queues; an inactive one is refused with the list).
  The owner still chooses the subagent's tools.
- **Work tab:** every agent, always: idle, "1/2 busy" with what each instance runs, queued, not loaded, disabled; a
  switch to disable or enable it. The running agents' tree below is "Runs".
- **Model calls without an agent** (a separate summarizer model, internal calls) get a slot per model while they run;
  they show in the Work tab only while busy.
- **Budget:** unchanged; a lane's daily cap is now the agent's.
- **Upgrade:** on the first start with no agents, the lanes the user set up become agents, plus one for the default
  model when none runs it. A new install starts with an agent for the default model. With no agent at all (tests, a
  settings file without any) chats run as before, on a slot per model.

## Order and tests

1. Backend: agent definitions and the upgrade; the scheduler's pools are the agents (instances, the shared model
   slots, active / disabled); the catalog re-read every few seconds; a run takes its chat's agent (`meta.agent`);
   `agent_choices`, `agent_spawn { agent }`, the prompt section; `agents.use`, `agents.setEnabled`; the ledger's caps per
   agent.
2. UI: Settings → Agents & budget (rows and an agent dialog with the model search, instances, note, enabled, price,
   cap, the slot warning); the agent picker with "New agent…"; the Work tab's agents with their switches.
3. Mock, UI e2e, the .NET suites, docs.

Tests: agents from settings (instances, disabled, not loaded → inactive, the shared model slots), the upgrade, a chat
on an inactive agent, `agent_choices` / `agent_spawn { agent }`, the Work tab switch and the picker in the UI mock.
