# Lanes, the budget, settings as controls, tools per chat (done 2026-09-24)

The user asked how to add an OpenRouter lane for the free stealth model, and whether lanes should have a cost and
capabilities so agents can choose a model. Together we simplified that to lanes the user sets up per model, one global
budget that agents can see, real controls in the settings dialog instead of JSON, and tool switches per chat. Kept for
reference: the settings are in `docs/SETTINGS.md` (Lanes, Budget), the RPCs in `docs/PROTOCOL.md`, the tools in
`docs/TOOLS.md` ("Which tools an agent is sent"), the UI in `docs/UI.md` (Settings dialog, Composer).

## Decisions with the user (2026-09-24)

- **A lane is a model you set up:** `lanes.<id> = { model, capacity, use, cost?, budget?: { limitUsd } }`. The note
  (`use`, when to use it) is written by hand; the price, context, local/cloud and today's spend are inferred and shown,
  and the price can be overridden. No globs, no pools of several models, no tags.
- **Any agent can use any lane.** Credentials are global, so a local `qwen3.8-27b` agent can spawn onto the stealth
  lane or a paid one (tested).
- **One global budget:** `budget.monthlyUsd` from `budget.resetDay`, optional `budget.dailyUsd`, a warning at
  `budget.warnPercent`, and `budget.onLimit`: `stop` (paid calls stop) or `ask` (a chat stops with "let this chat go
  over"; subagents stop either way). Free and local models are never blocked. Agents see the budget and every lane's
  price in `lanes_list`, and the lanes prompt section tells them to prefer free lanes.
- **Settings are controls:** the host and each plugin declare their settings; settings.json stays for everything else.
- **Tools are switched per chat; globally, whole plugins are.** The user found per-chat switches more useful than a
  global switch per tool. A change in a started chat is allowed: it applies from the next model call, which re-reads the
  conversation once (tool definitions lead the request), and the menu says so.

## As built

- **Ledger** (`plugins/NetPI.Lanes/Ledger.cs`): the `LedgerMiddleware` (order -100) wraps every model call (agents,
  compaction, anything a plugin asks a model), checks the budget before a paid call and records one `usage_calls` row
  after it: session, root session, agent, lane, model, purpose, tokens, cost and where the cost came from (`reported`
  by OpenRouter's `usage.cost`, carried in the additive `Usage.CostUsd`; `estimated` from the lane's or the catalog's
  price; `free`; `unknown`). `usage.changed` (debounced) carries the budget status.
- **Budget:** `BudgetExceededException` (additive) ends the run with a `budget` notice; with `ask`, `canOverride`, and
  `budget.allow` lets the chat go over until the period ends (`meta.budgetAllowedFrom`) and continues it.
  `budget.status`, `usage.summary` (with the budget and this period per model), `usage.session` (a chat, with its
  subagents).
- **Lanes:** configured lanes first, automatic lanes for every other model (cloud: `lanes.cloudDefaultCapacity`, local:
  the catalog's concurrency or `lanes.localDefaultCapacity`); the legacy `lanes.pools` / `lanes.budgets` still work.
  `lanes_list` starts with the budget line and lists the lanes cheapest first; `agent_spawn` needs `lane` when lanes are
  set up (the error lists them), otherwise a subagent runs on its parent's model.
- **Settings schema:** `SettingsSection` / `SettingInfo` (additive, `src/NetPI.Abstractions/SettingsSchema.cs`),
  registered through `ctx.Services` so a plugin's section goes with it; `settings.schema` returns the host's section and
  every plugin's (20 sections). The dialog: General, Lanes & budget (lanes editor, budget view, budget settings),
  Models, Agents, Context, Tools & plugins (plugin switches with their tools), settings.json, About.
- **Tools per chat:** `meta.toolsOff` (`SessionTools` in the contracts), `agent.tools` / `agent.setTools`; the runner,
  `context.preview` and subagent spawning honour it; the `tools` notice names tools "the user switched off for this
  session". The composer's tools button, with "N off", the re-read warning in a started chat and **All on**.
- **Seeing the money:** the chat's cost next to the context ring, the budget in the Work tab, a top-bar pill from the
  warning level (red when spent), and the "go over" link on a budget notice.

## Found on the way

- **Compaction on small windows.** The E2E suite's 12k-context run overflowed: the system prompt and 36 tool schemas
  now take ~7k tokens, and compaction kept 30 % of the window of recent messages regardless, leaving the chat ~88 %
  full. What is kept now leaves the context at most 60 % full with the largest summary (large windows unchanged).
- **Svelte 5 trims whitespace at the start of an element**, so `<span> / $50</span>` rendered as "$0.68/ $50"; three
  such spans (Work tab header, budget table, web results) now use `&nbsp;`.
- `node web/scripts/build-plugins.mjs --no-copy` builds plugin tabs without touching a running app.

## Results

- Unit suites: Providers 41 (330 checks), Tools 54, Agent 79 (budget 4, session tools 4), Aux 97, Host 38.
- E2E: 55 tests, 703 checks (UI smoke included). UI mock e2e: 150 checks (settings fields, lanes, budget, plugins,
  tools per chat, chat cost, Work tab budget, budget notice and pill).
- Commits: `f1bb994` (plan), `a8d36fe` (ledger, budget, lanes), then the settings dialog, tools per chat and the budget
  views.

## Not done

A dollar budget for goals (`goal.budgetUsd`); a "test" button per provider; live checks with a paid model and with
`qwen3.8-27b` choosing lanes (after deployment).
