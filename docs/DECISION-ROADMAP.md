# Decision models and multi-prefill: roadmap

Written 2026-09-26 at the end of the session that built `/v1/decision`. It lists every suggestion and next step from
that session, in the order to do them. Results so far: `DECISION-MODELS.md`; the experiments: `archive/2026-09-26-decisions.md`.

## Where things stand

| piece | where | state |
|---|---|---|
| NInfer `POST /v1/decision` | `C:\AI\src\ninfer-windows`, branch `local/main` (`docs/decision.md` there) | deployed. The loaded qwen3.8-27b answers multiple-choice questions from the prefill logits: ~35 ms per question once the shared state is cached, ~190 ms cold. Questions run **one after another** in one slot (an endpoint mutex); nothing is batched on the GPU yet. |
| NInfer deploy | `C:\AI\ninfer-local\current` (AiHub runs it), `tools\windows\deploy.ps1` | one checkout, one build folder (`build-windows`). Rollback build: `C:\AI\ninfer-local\backup-2026-09-26`. |
| AiGateway `/v1/systemone` bridge | `C:\ai\projects\aiswitcher` (D69; `SystemOne.cs`, `SystemOneBridge.cs`) | deployed. TypeSafe System One for NInfer models → one `/v1/decision`; Kev on the nuc router is forwarded as-is. |
| One-NInfer guards | AiSwitcher D70 | deployed. The hub finds NInfer by image name and refuses a second start; no second crash restart while one is starting. |
| NetPI `decide` tool + `decide.ask` RPC | `plugins/NetPI.Decide` | built and deployed. Default model `kev-9b` (nuc, manual switch that also unloads yue2); `qwen3.8-27b` works through the bridge. |
| nuc decision models | router `quazzie/llama.cpp:kev-router` (kev-9b, kev-4b, exclusive); `laya-tasks` :8010 (`laya-logs`); Ollaya `decide` stack :11435 | running. `laya-tasks` serves **Laya-logs-qwen** as `logs` (0.78 / 0.93 / 0.93 / 0.97, was the Kev-taught 0.53 / 0.89 / 0.84 / 0.91) since 2026-09-26 (1.1). Kev-9B does not fit on the 4070 beside `laya-tasks` (OOM on load). |
| test sets and scripts | `C:\AI\decisions-lab` (README there); nuc `/home/quazzie/train` | 120 hand-labelled log lines, 852 real commands with Qwen's guard labels, 44 Windows UIA tasks, Mind2Web on the nuc. |

## Ground rules

- **Never two NInfer processes.** Before any start, `Get-Process ninfer-serve` must be empty. Stop AiHub's NInfer with
  the 5090 loadout **Off** (`POST http://127.0.0.1:8191/control/loadout {"gpu":"5090","loadout":"off"}`), start it
  with loadout **Agent**. A hand-run test build uses **another port** (e.g. `--port 8089`): AiHub adopts whatever
  answers on 8080 and restarts it when it disappears.
- **Ask the user** before stopping or restarting NInfer, AiHub or NetPI (it drops in-flight agent work), before
  touching `%USERPROFILE%\.netpi`, and before adding a NuGet or npm package (CLAUDE.md). Measurements that only send
  requests need an idle moment, not a restart: ask for one.
- The 5090 is NInfer's. The nuc (4070 + NPU/iGPU, 96 GB) is for side models and training; `ssh nuc` works.
- Measure before building; show a design before engine work (Phase 2).
- Commit each finished step with its tests; never push without asking.
- Shell pitfalls seen: Git Bash mangles backslashes in heredocs and arguments (write scripts with the Write tool, run
  Windows paths through PowerShell); `pkill -f` / `pgrep -f` match their own ssh command line; a killed NInfer takes
  up to a minute to release the GPU.

## Phase 0: baselines (done 2026-09-26)

Numbers every later decision depends on, recorded in `DECISION-MODELS.md`, "NInfer baselines".

0.1 **Decision throughput.** 100 real log lines (`decisions-lab/data/lines.jsonl`) × the 4 log questions
(`log_questions_q.json`) through `/v1/decision` directly and through AiGateway `/v1/systemone`: per question and per
line ms, `cached_tokens`, and the same with one agent generating at the same time (does the decision wait for a slot?).
Start from `decisions-lab/scripts/verify_decision.ps1`.
*Result:* ~79 ms per question (317 ms per line) cached or not, about half of it host work; the gateway adds nothing;
one generating agent: decisions +43 %, the agent's decode −61 %; two generating agents: decisions wait for a slot.

0.2 **Two agents prefilling at once.** Two different long prompts (about 20k and 40k tokens, stateless chat
requests) sent together vs one at a time: time to first token of each, prefill tokens/s (`/metrics`,
`/debug/context-cache`, `/slots`), and decode tokens/s of one agent while the other prefills. This says how much
multi-lane prefill (2a) could give the user's two agents.
*Result:* strictly one prefill at a time, compute-bound (8.8k tok/s at 20k, 7.0k at 40k): a 20k prompt behind a 40k
one waits 6.2 s; a decoder next to a 40k prefill drops to 10 % of its speed. Gains from 2a are latency, not throughput.

0.3 **Decisions on an agent's cached context.** Send a long conversation as an agent turn, then a decision whose
`messages` are exactly that conversation plus one question, then the agent's next turn. Check: the decision's
`cached_tokens` (does it reuse the agent's prefix?) and the next turn's cached tokens with and without the decision in
between (does the decision consume or evict the agent's private continuation?). Phase 3.2 depends on the answer.
Decisions are meant to read the cache, capture with Disposable retention and never publish a continuation; verify it.
*Result:* reuse only when the agent's reasoning effort matches the decision's rendering (effort is part of the
prompt; `/v1/decision` renders thinking off, so only agents at effort `none`); and a decision that reuses an agent's
lineage consumes it (move, not fork), so the agent's next turn re-prefills. 3.2 needs both fixed in NInfer.

0.4 **Guard accuracy.** The 852 commands × the guard questions (phrased as questions) through `/v1/decision`, compared
with Qwen's generative labels (`guard_qwen.jsonl`), per question; the same for Kev-9B and laya:typed-decisions on the
nuc. Then ask the user to label ~50 commands where the models disagree: the first real ground truth for guards.
*Result:* the Qwen decision matches Qwen's generative labels closely (at p(yes) < 0.2 it calls 696 of 852 harmless,
none risky); Kev-9B fails remote_change (327 false yes); zero-shot Laya is unusable. 50 disputed commands for the
user to label: `decisions-lab/results/phase0/guard_to_label.json`.

## Phase 1: quick wins (no engine work)

1.1 **Serve Laya-logs-qwen.** *Done 2026-09-26: `laya-logs` = Qwen-taught (`serve.sh` on the nuc; not a
Dockhand stack, nothing else used the container). The Kev-taught one is not kept resident: a second model costs 1.7 GB
of the 4070, which Kev-9B already lacks.* Register the checkpoint in `laya-tasks` as `logs-qwen`, check it with
`serve_check.mjs` on the gold lines, then make it `logs` (keep the Kev-taught one as `logs-kev` for a while). Redeploy
through Dockhand (container restarts are the user's call if something else uses it). Update `DECISION-MODELS.md`.

1.2 **All decision models behind AiGateway.** *Built 2026-09-26 (AiSwitcher D73, branch `claude/decision-engines`):
a `systemone` engine kind; `laya-nuc` / `laya-logs` in the sample and prod configs; the fit check now counts it, so
kev-9b beside it plans as `does_not_fit` instead of an OOM. Ollaya stays out (no `/health`, weak zero-shot). Deploy:
the user adds it to `publish/hub/lab.json`, publishes, restarts hub and gateway.* Register `laya-tasks` (and Ollaya if useful) in AiSwitcher's lab.json
with `api: "systemone"` so NetPI reaches every decision model through one URL and model id; the gateway forwards
System One for non-NInfer engines as-is.

1.3 **Bulk vs interactive in `decide`.** Bulk work (a file of lines) belongs on the nuc (Laya: all questions in one
pass, ~37 ms per line, no 5090); single questions and in-loop checks on NInfer. Options: a `decide.bulkModel` setting
used above N items, or per-call guidance in the tool description. Decide after 0.1. *0.1 says bulk belongs on the
nuc: Laya `laya-logs` is 8.5× faster per line and back-to-back NInfer decisions cut a generating agent's decode by 61 %.*

1.4 **NInfer graceful stop** *Built 2026-09-26 (fork `7e348652`, `docs/admin.md`), not deployed yet.* (AiSwitcher `docs/COMPANION-CHANGES.md` A6): `POST /admin/shutdown?drain_ms=` (loopback
only; stop admitting with 503 `shutting_down`, drain, exit 0) and a console Ctrl handler. AiHub already calls it and
falls back to a kill; today it always kills. A fork-local feature: new files plus one-line hooks, like `/v1/decision`.

1.5 **NInfer `/slots` busy counters for non-streaming requests** *Built 2026-09-26 (fork `46d51b23`), not deployed
yet.* (COMPANION-CHANGES A3; AiSwitcher idea
`idea-k7q2vn`): `n_decoded`, `n_prompt_tokens_processed/_cache`, decode t/s are null while a non-streaming request
runs, so the taskbar shows nothing. Deploy 1.4 and 1.5 together with one NInfer restart.

1.6 **AiHub graceful quit.** *Built 2026-09-26 (AiSwitcher D72, branch `claude/decision-engines`; refuses during a
switch or gateway update unless forced), not deployed yet.* The hub quits only from its tray menu; the switch-over had to kill it. A loopback
`POST /control/hub/quit` (same path as the tray's Quit) makes publishing and restarts clean.

## Phase 2: multi-prefill in NInfer (design first, then build)

Why: `/v1/decision` runs questions one after another, and NInfer prefills one request at a time, so when both agents
get tool results at once one waits for the other's whole prefill. Short prompts cost the GPU about the same whether
it runs 40 or 400 tokens, so batching should raise decision throughput a lot (estimate 5–10×, unmeasured); for long
agent prefills the gain is mostly shorter waits.

2.1 **Read the scheduler.** Engine → GenerationService → Program; prefill ownership; lanes and slots; recurrent (Gated
DeltaNet) state slots (`--device-state-slots 0 --host-state-slots 16`); KV pages and shared prefixes; the ragged
prefill paths that exist (`tests/*ragged*`); DFlash2 speculative decoding and CUDA graphs.

2.2 **Design** (`docs/multi-prefill.md` in the fork), for the user's approval before any code. *Draft written 2026-09-26
(fork `3f440fab`), waiting for approval: decisions are overhead-bound, so batch branches inside one request first
(2–5×); 2a becomes prefill scheduling (shortest first per chunk, admission during prefill, a decode share).*
- 2a *Multi-lane prefill*: the prefill chunks of two lanes in one ragged forward pass, each lane with its own
  recurrent state; a shared chunk budget; how decode and prefill interleave.
- 2b *Multi-branch decisions*: prefill the shared state once, fork the recurrent state per branch (plus copy-on-write
  KV), run all branch suffixes as one ragged batch, capture the logits at each branch's last token, release without
  publishing. Several states (log lines) in one batch as well.
- Expected gains from Phase 0; memory (state slots per branch); risks (CUDA graph shapes, speculative decoding,
  correctness); which part first (2a if 0.2 shows real waits, else 2b).

2.3 **Build and prove it.** Batched vs sequential logits equal within tolerance; ctest serve/runtime set (FORK.md);
throughput vs the 0.1/0.2 baselines; two-agent regression. Deploy by the one-NInfer procedure.

2.4 *(Superseded by the design, §5: one worker and one prefill owner would still serialize parallel decisions.)*
**Interim, if 2 is far off:** let decisions use both slots when the agents are idle (drop the endpoint mutex in
favour of normal admission), measured against 0.1.

## Phase 3: decisions inside NetPI

3.1 **Guardrails second opinion.** *Built 2026-09-26: `guardrails.secondOpinion` (off), `…Model` (qwen3.8-27b),
`…Threshold` (0.2); clears only a confidently read-only command (read-only ≥ 0.8, every risk < 0.2), event
`guard.cleared`, the model's view on the ask card. Turn on after the user's labels confirm 0.4.* For commands the Guardrails plugin would *ask* about, ask `/v1/decision` (the guard
questions, with the command, cwd and a little context) and skip the prompt only when it is confidently harmless; never
relax a *block*; show the decision in the ask card. Off by default; enable after 0.4 shows the accuracy. Tests with a
mock decision server.

3.2 **In-conversation checks** (needs 0.3): "stuck in a loop?", "task finished?", "should it ask the user?", sent with
the agent's exact message prefix so the cache makes them nearly free; results go to the agent as hints, not actions.
Needs a plugin hook on agent turns/tool calls and a way to send the same messages the provider sends.

3.3 **Routing** (measure value first): thinking on/off per turn, which subagent or tool handles a request.

3.4 **Ideas**: suggest closing an idea when a change finishes it.

3.5 **Browser and computer use** (a larger project, own design): browser element choice = ranker top-20 +
qwen3.8-27b logit readout (0.425 top-1, the best measured); Windows = UI Automation list + Kev-9B (0.89 top-1) or
Qwen (not yet measured on Windows). Record every real step for later training.

## Phase 4: model quality (nuc)

4.1 The user's own labels: ~50 log lines and ~50 commands, to check the Claude-written gold labels.
4.2 Tighter log subsystem categories (subsystem is the weakest question at 0.78).
4.3 Browser picker: fine-tune Kev-4B as the picker; better element descriptions (the ranker's recall ceiling is
~84 % in the top 20); NetPI's own browser traces.
4.4 Open question for the user: Ollaya's `MAX_LOADED` cap.

## Phase 5: housekeeping

5.1 Push the three repositories (ask first; NInfer pushes `local/main` to `origin`).
5.2 NInfer: squash the two fixups at the next natpate/upstream sync (FORK.md).
5.3 nuc: clean up test leftovers (containers, `models.ini.pre-kev-*.bak`, old images); ask before deleting.

## Order for the next session

1. 0.1 and 0.3 (an idle moment, no restart), 1.1 on the nuc alongside.
2. 0.2 and 0.4.
3. The Phase 2 design, shown to the user; wait for the go-ahead.
4. Meanwhile 1.4 + 1.5 (one NInfer deploy), 1.2, 1.6, then 3.1.
