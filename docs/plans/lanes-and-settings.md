# Plan: lanes with a profile and cost, and a settings dialog with real controls, 2026-09-24

Status: proposed; decisions so far from the user, questions at the end.

## Where we are

- **Any agent can use any lane.** `agent_spawn { model }` takes a model ref or a lane key; provider credentials are
  global, and nothing ties a lane to the agents that may use it. The only limits are `agents.maxDepth` (3), lane
  capacity and `lanes.budgets`. The test "an agent on a local model spawns onto a pool defined in settings" covers
  exactly that: a local-model agent delegates to a one-model cloud lane.
- **Agents can't tell lanes apart.** `lanes_list` shows each lane's key, busy/capacity, queue, models and holders; nothing
  about what a lane is good at or what it costs.
- **Settings are JSON.** The dialog has UI preferences (General) and the raw settings.json; the ~80 host and plugin
  settings in `docs/SETTINGS.md` have no controls.

## Decisions (user, 2026-09-24)

- **A lane is one model:** "I set up a lane for a specific model", e.g. one for `openrouter/stealth/space-bunny-alpha`
  (free) and one for `openrouter/anthropic/claude-opus-…` (real cost). No glob lanes in the UI.
- **Profile:** a description and capabilities written by the user; everything that can be inferred (cost, context,
  vision…) is inferred but editable.
- **Cost-aware delegation:** agents avoid the costly lane unless they judge it necessary.
- **Controls, not JSON:** as much of the settings as possible gets real controls in the dialog.

## Part 1: lanes as models with a profile

```jsonc
"lanes": {
  "pools": {
    "bunny": {
      "model": "openrouter/stealth/space-bunny-alpha",
      "capacity": 2,
      "description": "Free, strong general coder; slower. Good for research, reading code, first drafts.",
      "strengths": ["coding", "long-context"]
    },
    "opus": {
      "model": "openrouter/anthropic/claude-opus-4.1",
      "capacity": 1,
      "description": "Best at hard design and debugging. Costs real money: only when cheaper lanes fail or the task clearly needs it.",
      "strengths": ["reasoning", "coding", "review"],
      "use": "when-needed"          // default | when-needed | never-for-subagents
    }
  },
  "budgets": { "opus": { "dailyTokens": 500000 } }
}
```

- **`model`:** the one model of the lane. `models` (a list, globs) keeps working in settings.json for compatibility,
  but the dialog and the docs use `model`. Models without a lane of their own keep today's default pools (one per
  cloud provider, one per local model).
- **Inferred facts, each overridable in the lane:**
  - local or cloud;
  - price per million input/output tokens (OpenRouter's catalog already carries `pricing`; local = free);
  - context window and max output;
  - image input and reasoning levels;
  - the free-tier rate limit (OpenRouter `:free` and stealth models: 20 requests/min);
  - loaded/offline, and today's usage against the budget.

  Overrides are `contextWindow`, `cost: { input, output }` and so on; the dialog shows the inferred value until you
  type another. Later: measured speed (tokens/s, time to first token) from recent runs.
- **`use`:**
  - `when-needed`: the lane is listed last, marked "costly", and `agent_spawn` onto it requires a `reason` (why a
    cheaper lane won't do), shown on the spawn row so you can see why the agent chose it;
  - `never-for-subagents`: only you pick it for a session;
  - `lanes.budgets` stays the hard stop.
- **What agents see:** `lanes_list` shows one compact line per lane, cheapest first:
  `bunny · openrouter/stealth/space-bunny-alpha · 0/2 busy · cloud · free (20 req/min) · 200k ctx · reasoning · coding, long-context · "Free, strong general coder; …"`,
  and for `opus`: `… · $15 / $75 per Mtok · costly: only when needed (give a reason) · budget 120k/500k today`.

  The lanes prompt section (static, so the frozen prompt stays valid) adds one rule: "choose a lane by its profile;
  prefer free and local lanes; use a costly lane only when the task needs what it is good at, and say why."

## Part 2: the settings dialog with real controls

- **Settings schema.** Each plugin declares its settings once:
  - key, type, default, label, help, section, options, min/max, unit, secret, needs-restart;
  - a new additive contract (`SettingInfo` in a `SettingsSection` registered through `ctx.Services`, so hot reload
    removes it); the host declares its own;
  - `settings.schema` returns them all;
  - `docs/SETTINGS.md` stays the reference and could later be generated from the schema.
- **The dialog renders it:**
  - toggle, number with unit and limits, text, text area, secret (masked, "show", `env:NAME` accepted), choice, list
    (chips), model (the model picker), folder (the folder picker);
  - every field saves on change (`settings.set`), shows the default while unset and has "reset to default";
  - settings.json stays as **Advanced**.
- **Sections:**
  - **General:** the UI preferences, as now.
  - **Models & providers:** default model; per provider its base URL, API key (masked) and model include list, with a
    "test" button that lists the models it offers.
  - **Lanes:** Part 1 as a table:
    - one row per lane: a model picker, lanes, description, strengths and `use`;
    - the inferred facts with an override each, and the daily budget;
    - "add lane" starts from a model.
  - **Agents:** max turns, max depth; goals (continuation limit, no-progress limit, token budget).
  - **Context:** custom instructions, the AGENTS.md file names and guidance, compaction, nudge, retry.
  - **Tools and plugins:**
    - every tool from `tools.list` and every plugin, with on/off switches (`tools.disabled`, `plugins.disabled`);
    - shell (paths, timeout), files (new-file line endings), web (search provider, SearXNG URL, Brave key, fetch
      limits, browser), SSH.
  - **Workspace & logging:** default folder, log level.

## Order and tests

1. Schema, renderer and the simple sections (most keys, quick win).
2. Models & providers, tools and plugins switches.
3. Lanes: `model`, profile, inference, `use`, `lanes_list`, the prompt rule, the lanes editor.

Tests:
- Host: schema collected from plugins and removed on unload; `settings.set` type checks.
- Lanes: inferred facts and overrides, the `lanes_list` text, the `use: when-needed` reason rule.
- UI mock: the forms save and reload, the lanes editor.

## Questions

1. **Multi-model lanes.** Should they disappear completely, or stay possible in settings.json for models that share one
   GPU's slots (as `models`)? Proposed: keep reading them, but the dialog edits one model per lane.
2. **Strengths.** A fixed tag list (coding, reasoning, review, vision, long-context, fast, cheap) plus the description,
   or free text only? Proposed: tags plus the description.
3. **`use: when-needed`.** Is a required reason on `agent_spawn` the right amount of friction, or should a costly lane
   ask you first (a confirmation in the UI) before a subagent starts on it?
4. **Saving.** Immediately per field (proposed), or a Save button per section?
