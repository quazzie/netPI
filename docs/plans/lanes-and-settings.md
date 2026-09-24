# Plan: lanes you set up per model, and a settings dialog with real controls, 2026-09-24

Status: agreed with the user (2026-09-24); being built in the order below.

## Where we are

- **Any agent can use any lane.** Provider credentials are global and nothing ties a lane to the agents that may use
  it. The test "an agent on a local model spawns onto a pool defined in settings" covers a local-model agent delegating
  to a one-model cloud lane.
- **Lanes are "pools" today.** One per cloud provider and one per local model, created automatically, plus
  `lanes.pools` with model globs. `lanes.budgets` counts tokens per day.
- **Agents can't tell lanes apart.** `lanes_list` shows key, busy/capacity, queue, models and holders, nothing about
  when to use a lane or what it costs.
- **Settings are JSON.** The dialog has the UI preferences and the raw settings.json.

## Design (the user's simplification)

A lane is a model you set up, with a capacity, a note on when to use it, and an optional budget. No pools, globs, tags
or policy fields:

```jsonc
"lanes": {
  "bunny": { "model": "openrouter/stealth/space-bunny-alpha", "capacity": 2,
             "use": "Free. General coding, research, reading code. The default for subagents." },
  "qwen":  { "model": "aiproxy/qwen3.8-27b", "capacity": 2,
             "use": "Local and free, fast. Searches, small edits, summaries." },
  "opus":  { "model": "openrouter/anthropic/claude-opus-4.1", "capacity": 1,
             "use": "Costs real money. Only for hard design or debugging the free lanes could not solve.",
             "budget": { "limitUsd": 5 } }          // per day; also limitTokens; cost: { input, output } $ per Mtok
}
```

- **Budget:**
  - `cost` is the price per million input/output tokens. It is inferred (OpenRouter's catalog has it, local models are
    free) and you only type it where it is missing or wrong.
  - `limitUsd` / `limitTokens` stop the lane for the day; OpenRouter reports the real cost of every call.
- **Facts shown automatically, nothing to configure:** local or cloud, context window, image input, reasoning, the
  free-tier rate limit, loaded/offline, today's spend.
- **Agents:**
  - `lanes_list` shows one line per lane, cheapest first: `opus · openrouter/anthropic/claude-opus-4.1 · 0/1 busy ·
    $15/$75 per Mtok · $1.20 of $5 today · 200k ctx · "Costs real money. Only for …"`.
  - `agent_spawn` takes `lane` (the id). Without it, the error lists the lanes and their `use`, so the model can retry at
    once.
  - With no lanes set up, a subagent runs on the caller's model, as today.
  - The lanes prompt section (static) says: read the lanes' `use` and cost, prefer free lanes, use a costly one only
    when the task needs it. The budget is the hard stop.
- **Automatic lanes:** a model you chat with that has no lane of its own still gets one (capacity from the catalog, or
  `laneDefaults.localCapacity` 1 / `cloudCapacity` 4). It shows in the Work tab but is not offered to agents.
- **Compatibility:** `lanes.pools` / `lanes.budgets` / `lanes.*DefaultCapacity` are still read (a one-model pool
  becomes a lane); the docs and the dialog only know the new shape.
- **Not kept:** several models sharing one lane's capacity (e.g. two models on one GPU). Nobody needs it now.

## Budget and cost tracking (user, 2026-09-24: one global budget, visible to agents, usage tracked)

- **Ledger:** one row per model call, for every call (agents, compaction, anything a plugin asks a model). Today only
  agent turns publish `usage.recorded`, as token totals per day. Each row holds:
  - time, session, agent, lane, model, purpose;
  - tokens: input, cache read, cache write, output;
  - cost in USD and where it came from: `reported` (OpenRouter returns the real cost of each call; today it is only
    stored on the message), `estimated` (tokens × the lane's or catalog's price) or `free` (local models).

  The cost travels in an additive `Usage.CostUsd`, and the rows go in SQLite.
- **One global budget:**
  - `budget.monthlyUsd` (the month starts on `budget.resetDay`, default the 1st) and optionally `budget.dailyUsd`, with a
    warning at `budget.warnPercent` (80);
  - a lane's `budget.limitUsd` stays an optional extra cap ("Opus at most $5 a day");
  - when a budget is spent, `budget.onLimit` decides:
    - `stop` (default): paid calls stop, with a clear message;
    - `ask`: your own chats stop with "allow this chat to go over", which continues it; subagents stop either way.

    Free and local lanes are never blocked.
- **Agents see it:**
  - `lanes_list` starts with `Budget: $12.40 of $50 this month (25 %), $1.10 today; free lanes don't count`, and every
    lane shows its price and spend;
  - the lanes prompt rule adds: paid lanes spend the user's budget; above the warning level use them only when the
    user asked.
- **You see it:** the Work tab's usage section shows this month against the budget, today, and a breakdown per lane and
  model. Each chat shows its own cost (subagents included). A banner appears at the warning level. Settings → Budget
  holds the limits.
- **Goals:** a goal's budget can be dollars too (`goal.budgetUsd`, from the same ledger), next to the token budget.

## Settings dialog with real controls

- **Schema.** Each plugin declares its settings once (key, type, default, label, help, section, options, limits, secret):
  - a new additive contract registered through `ctx.Services`, so hot reload removes it; the host declares its own;
  - `settings.schema` returns them all.
- **The dialog renders them:**
  - toggle, number with unit and limits, text, text area, secret (masked, "show", `env:NAME` accepted), choice, list,
    model picker, folder picker;
  - every field saves on change (`settings.set`), shows the default while unset and has "reset to default";
  - settings.json stays as **Advanced**.
- **Sections:**
  - **General:** UI preferences.
  - **Models & providers:** default model; per provider base URL, API key and model list, with a "test" button.
  - **Lanes:** a row per lane: model picker, capacity, use, budget with the inferred cost; "add lane".
  - **Agents & goals.**
  - **Context:** custom instructions, AGENTS.md, compaction, nudge, retry.
  - **Tools & plugins:** on/off for every tool and plugin; shell, files, web, SSH.
  - **Workspace & logging.**

## Order and tests

1. The ledger and the budget (every model call, cost, `budget.*`, the stop for paid lanes).
2. Lanes: the new shape, `lanes_list` with budget and prices, `agent_spawn { lane }`, the prompt rule.
3. The settings schema, the renderer and the simple sections, Budget included.
4. Providers, the lanes editor, the tool and plugin switches, the usage view in the Work tab.

Tests:
- Lanes suite: lanes from settings, automatic lanes, the ledger (reported, estimated and free costs), the global and per-lane
  budget stops, the `lanes_list` text with the budget line.
- Agent suite: `agent_spawn` by lane, the error listing lanes, the fallback without lanes.
- Host: schema collected and removed on unload.
- UI mock: forms save and reload, the lanes editor.
