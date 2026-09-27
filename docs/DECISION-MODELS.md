# Decision models: which model for which task

A living results matrix for NetPI's decision helpers (`plugins/NetPI.Decide`). One model per task, chosen by
measurement. How the numbers were made, and every experiment behind them: `docs/archive/2026-09-26-decisions.md`.

**Terms.** A *decision model* answers typed questions (yes/no, pick one, score) with a probability, without writing
text. *Zero-shot*: used as released. *Fine-tuned*: trained further on our task. *Teacher → student* (distillation): a
large model labels data, a small model is trained on those labels so it answers the same way much faster; the student
cannot know more than its teacher, so the teacher's quality is the ceiling.

**Where things can run** (the 5090 is for training and testing only; it runs NInfer in normal use):

| device (nuc) | memory | good for |
|---|---|---|
| RTX 4070 | 12 GB | the fine-tuned Laya task models (1.2 GB for the server, ~0.85 GB per further model), rankers, training; Kev-9B (10.3 GB) or Kev-4B (6.5 GB) when asked for by name; yue2 renders (up to ~6 GB) |
| Arc iGPU | ~48 GB shared | small and mid encoders (Laya 421M: 0.4 s/line); Kev is too slow here (Kev-4B 4.9 s) |
| NPU | shared | tiny always-on encoders (MiniLM: 5 ms/line); larger models are slow or inexact |
| shared with | | Frigate runs object detection on the NPU and the iGPU, and video decoding on the iGPU |

## Recommended model per task (current best)

NInfer now has `/v1/decision` (fork, 2026-09-26) and AiGateway answers `/v1/systemone` for NInfer models through
it, so the already-loaded **qwen3.8-27b is the first choice for browser, Windows and anything low-volume**
(`decide.model: qwen3.8-27b`, the default since 2026-09-27; 0.461 on the browser, 0.98 on Windows, better than every
other model measured). It costs no memory but shares NInfer's two slots with the agents, so high-volume work (a whole
log file) belongs on the nuc models. Kev-9B is no longer recommended for any task:

| task | recommended | score | speed / where | runner-up | test set |
|---|---|---|---|---|---|
| **Log lines**: subsystem, severity, needs a human, routine | **Laya-logs-qwen** (421M, fine-tuned on Qwen3.8-27B's labels), served as `laya-logs` by `laya-tasks` (nuc :8010, `/v1/systemone`; through AiGateway once D73 is deployed) | **0.78 / 0.93 / 0.93 / 0.97** | 37 ms/line on the 4070 (all 4 questions, p50 over the LAN; 0.42 s iGPU) | Qwen3.8-27B itself 0.79 / 0.92 / 0.95 / 0.93 (0.7 s/line); Laya-logs on Kev-9B's labels 0.53 / 0.89 / 0.84 / 0.91 | 120 real lines, hand-labelled |
| **Browser**: which element next | **MiniLM ranker (fine-tuned) → top-20 → qwen3.8-27b** `/v1/decision` (20 lettered options) | top-1 **0.461** (target in top-20: 0.846); × the Qwen-taught Laya picker 0.478 | ranker 65 ms (nuc) + 124 ms per step on the 5090 | Laya picker taught by Qwen 0.320 (nuc); the old Laya picker × Kev-9B 0.392 | Mind2Web, 475 steps on 14 websites never trained on |
| **Computer use (Windows)**: which control next | **qwen3.8-27b**, zero-shot, over the whole UI Automation list (writes the control's number, thinking off) | top-1 **0.98** (43/44); live, whole tasks: **18/19** at reasoning low with a plan line, 15/19 thinking off ("Live computer use" below) | 139 ms per step on the 5090 (p50; 189 controls at most); live 0.2–0.4 s per step through AiGateway | Kev-9B 0.89, top-3 0.98 (~1 s, 4070); small rankers 0.55 | 44 hand-made tasks in 5 Windows apps; 19 live multi-step tasks in 4 apps |
| **Dangerous command** (guardrail second opinion) | **qwen3.8-27b** `/v1/decision` | agrees with Qwen's generative labels 0.998 / 0.993 / 0.988 / 0.912; at p(yes) < 0.2 on the three risk questions it calls 696 of 852 harmless, none of them risky | 0.31 s per command (4 questions), 5090 | Kev-9B (remote_change unusable: 327 false yes); on the 12 hand-made: Kev-9B 12/12 | 852 real commands, reference = Qwen generative (not human yet; see "NInfer baselines", 0.4) |
| **Agent stuck / looping** | **Kev-9B** or **laya:typed-decisions** | 6/6 | 0.1 s / 9 ms | Kev-4B 6/6 | 6 hand-made traces — **too small** |
| **Issue triage** | **laya:typed-decisions** (9 ms) | 8/8 | 9 ms (Ollaya) | Kev-9B / Kev-4B 8/8 | 8 hand-made issues — **too small** |

### Any LLM as a decision model (logit readout)

No training: the prompt holds the state, the question and lettered options; the model runs one step
(`max_tokens: 1`, thinking off) and the probabilities of the option letters are the decision. Tested through
llama.cpp on the nuc's 4070 (`logit_decide.mjs`):

| model | logs (subsystem / severity / needs a human / routine) | browser top-1 (475) | time |
|---|---|---|---|
| Qwen3.8-9B | 0.60 / 0.72 / 0.34 / 0.83 | – | 218 ms/question |
| **Qwen3.8-27B** (IQ3_XXS GGUF) | **0.76 / 0.90 / 0.86 / 0.91** | **0.425** | 0.6 s/question, 1.1 s/step on the 4070 |
| *Kev-9B, for reference* | 0.54 / 0.87 / 0.82 / 0.90 | 0.307 | |

The 27B beats every other decision model here without training. Yes/no questions must be phrased as questions
("Does a human need to…?"): as statements, "needs a human" dropped to 0.68. Option rotation did not help (it was not
position bias); judgement questions still gain from reasoning (generative 0.95). The 9B is too weak for judgement.
This is the case for a `/v1/decision` path in NInfer on the already-loaded qwen3.8-27b (no extra memory).

## Full matrix

Scores are accuracy (top-1 for element choice). "–" = not measured. Logs columns: subsystem / severity / needs a human / routine.

| model | size | how | logs (120 real) | browser (475) | Windows (44) | guard /12 | stuck /6 | triage /8 |
|---|---|---|---|---|---|---|---|---|
| **Qwen3.8-27B** (NInfer, NVFP4) | 27B | zero-shot, `/v1/decision` (Windows: writes the number) | 0.675 / **0.892** / 0.875 / 0.908 | **0.461** | **0.98** | – | – | – |
| Kev-9B | 9B | zero-shot (GGUF, fork) | 0.54 / 0.87 / 0.82 / 0.90 | 0.307–0.335 | 0.89 | **12** | **6** | **8** |
| Kev-4B | 4B | zero-shot | 0.55 / 0.83 / 0.79 / 0.79 | – | – | 11 | **6** | **8** |
| **Laya-logs-qwen** (served as `laya-logs`) | 421M | fine-tuned (Qwen3.8-27B labels, 3 epochs) | **0.78 / 0.93 / 0.93 / 0.97** | – | – | – | – | – |
| Laya-logs (not served) | 421M | fine-tuned (Kev-9B labels) | 0.53 / 0.89 / 0.84 / 0.91 | – | – | – | – | – |
| **Laya picker, Qwen-taught** | 421M | fine-tuned (all 5,900 Mind2Web training steps, target 0.5 × gold + 0.5 × Qwen) | – | **0.320** | – | – | – | – |
| Laya picker, gold only | 421M | fine-tuned (same 5,900 steps, gold labels) | – | 0.280 | – | – | – | – |
| Laya picker (first) | 421M | fine-tuned (2,415 steps of 5 shards, gold) | – | 0.286 | – | – | – | – |
| **Qwen × Laya picker (Qwen-taught)** | | ensemble (Qwen × Laya^0.5) | – | **0.478** | – | – | – | – |
| **Laya picker × Kev-9B** | | ensemble (product) | – | **0.392** | – | – | – | – |
| laya:typed-decisions | 421M | zero-shot (Ollaya) | 0.35 / 0.63 / 0.76 / 0.29 | – | – | 8 | **6** | **8** |
| laya:en | 421M | zero-shot | – | – | – | 11 | 5 | 6 |
| decider 2B | 2B | zero-shot (CPU only on the nuc) | – | – | – | 11 | 5 | **8** |
| decider:0.8b | 0.8B | zero-shot | – | – | – | **12** | 4 | 6 |
| kev (0.8B) | 0.8B | zero-shot | – | – | – | 8 | 4 | 7 |
| nli / gliclass / von | 0.4–0.6B | zero-shot | – | – | – | 6 | 2–5 | 2–5 |
| MiniLM student | 22M | trained (Kev-9B labels) | 0.50 / 0.84 / 0.83 / 0.86 | – | – | – | – | – |
| MiniLM ranker | 22M | fine-tuned (Mind2Web) | – | 0.175 alone; top-20 holds 0.846 | 0.57 | – | – | – |
| bge-reranker-base | 278M | fine-tuned (Mind2Web) | – | 0.227 alone; top-20 0.844 | – | – | – | – |

### Teachers (who labels the training data)

| teacher | logs on the 120 hand-labelled lines | cost |
|---|---|---|
| Kev-9B | 0.54 / 0.87 / 0.82 / 0.90 | 0.35 s/line, 4070 |
| **Qwen3.8-27B** (NInfer, reasoning low) | **0.79 / 0.92 / 0.95 / 0.93** | 0.7 s/line with 2 parallel, 5090 (only while it is free) |

Caveat: the hand labels were written by Claude, another large language model, so part of Qwen's lead may be agreeing
with the labeller's style; a few dozen of the user's own labels would settle it.

## NInfer baselines

Measured 2026-09-26 (roadmap Phase 0) on the deployed NInfer (fork `74d47184`, qwen3.8-27b QUASAR NVFP4 on the 5090,
`--max-concurrency 2`, DFlash2, `--device-state-slots 0 --host-state-slots 16`) with the user's agents paused.
Scripts: `decisions-lab/scripts/p0_*.mjs`; raw results: `decisions-lab/results/phase0/`.

**0.1 Decision throughput.** 100 real log lines × the 4 log questions (`log_questions_q.json`), one line after
another; a different set of 100 lines per row, except "warm", which repeats the first set.

| path | per line p50 (p90) | per question p50 | shared state reused |
|---|---|---|---|
| `/v1/decision`, cold | 317 ms (346) | severity 68, subsystem 85 (329-token prompt), needs a human 77, routine 75 ms | 12 % of prompt tokens |
| AiGateway `/v1/systemone`, cold | 316 ms (345) | – | 10 % |
| `/v1/decision`, same lines again | 315 ms (345) | same as cold | 11 % |
| `/v1/decision`, one agent generating | 454 ms (486) | 104–122 ms | 5 % |
| `/v1/decision`, two agents generating | 2.35 s (9.4 s), max 10.2 s | waits for a free slot | – |

- A question costs 65–110 ms whether its state is cached or not. The prompts are short (140–330 tokens) and about
  half the time is host work: while decisions ran, the engine's host share was 45–48 % (≈37 ms per question) for
  admission, cache planning and capture. Only 22 % of branches reused the shared state (88 of 400), each through a
  host-to-device state restore, which saves little on a 60-token state. The earlier "~35 ms per question once
  cached" was measured on a freshly started NInfer and did not reproduce with a full cache.
- The gateway adds nothing measurable.
- Bulk: ~3.2 lines/s idle, 2.2 lines/s next to one agent; Laya `laya-logs` on the nuc does 27 lines/s (37 ms) without
  touching the 5090, so a log file belongs on the nuc (1.3).
- Next to one generating agent, back-to-back decisions cut its decode from 139 to 54 tok/s (−61 %) and take 43 %
  longer themselves. With both slots generating, a decision waits for a slot (here up to one 10-s agent turn).
  **Fixed 2026-09-27 (decisions first, NInfer `ba443920`):** with a decision lane, 30 decisions next to two
  generating agents take p50 130 ms, p90 160 ms, max 162 ms (the deployed build before: p90 8.4 s, max 8.5 s); a
  decision during a 42k-token agent prefill takes p50 214 ms instead of 5.4 s, and the agent's answer is unchanged.
  Back-to-back decisions slow two generating agents from ~176 to ~45 tok/s each while they run
  (decisions-lab `p2_first.mjs`; the fork's `docs/decision.md`, "Decisions first").
- Where the time goes (NInfer's per-branch `timing`, 2026-09-26): ≈30 ms of host time per question is
  `program_submit` (kernel launches; prefill has no CUDA graphs) and ≈22 ms is device work. Capturing a short state
  to share it costs more than it saves (a whole ~187 MB recurrent StateImage is copied): with `share_state: false`
  a question takes 54 ms instead of 75.5, a line 218 ms instead of 304. AiGateway now sends `share_state: false` for
  states under 1000 characters (AiSwitcher D75). The remaining cost is per-forward overhead, so the win is running
  all branches in one batched forward (Phase 2 Stage 2), not a tighter per-branch loop.
- Batched branches (Phase 2 Stage 2, NInfer `060d7bf3`, deployed 2026-09-26; `decisions-lab/scripts/p2_batch.mjs`,
  `p2_gold.mjs`):

  | case | one request per branch | batched |
  |---|---|---|
  | log line, 4 questions, state unshared | 220 ms p50 | 95 ms |
  | log line, state shared | 316 ms | 118 ms |
  | 4.5k-token state, 4 questions | 250 ms | 121 ms |
  | 12 long questions (27 of 36 fit one forward) | 842 ms | 345 ms |
  | log line next to a generating agent | 454 ms (0.1 above) | 133 ms |

  Gold accuracy on the 120 log lines, batched vs one request per branch: severity 0.892 / 0.883, category
  0.675 / 0.675, actionable 0.875 / 0.892, routine 0.908 / 0.900. The choices differ in 27 of 480 answers, as
  often as two per-branch runs that only chunk the prompt differently (26 of 480): NInfer's results depend on
  chunk boundaries and kernel routes, so near-ties can go either way; nothing is lost on average.

**0.2 Two agents prefilling at once.** Stateless chat, cold prompts built from NetPI's docs.

| case | time to first token | prefill |
|---|---|---|
| 20k tokens alone | 2.6 s | 8.8k tok/s |
| 40k tokens alone | 6.2 s | 7.0k tok/s |
| 20k, then 40k 20 ms later | 2.6 s / 8.9 s | one after the other |
| 40k, then 20k 20 ms later | 6.2 s / 9.0 s | the 20k prompt waits 6.2 s |
| an agent decoding while 40k prefills | – | decoder 138 → 14.5 tok/s (longest stall 230 ms); prefill −12 % |

Prefill runs one request at a time and is compute-bound at these lengths, so batching two long prefills would not
finish them sooner (both done at ~9 s either way). What costs the user is ordering (a short prompt waits behind a
long one) and a starved decoder (10 % of its speed). Multi-lane prefill (2a) is therefore about latency and
fairness (shorter first, interleaved chunks, decode rows inside prefill chunks), not throughput.

**0.3 Decisions on an agent's cached context.** A 22k-token conversation sent the way NetPI sends it (Responses,
`store: false`, reasoning replayed), then a decision whose messages are that conversation plus one question, then
the agent's next turn.

| agent reasoning | decision's state | decision | agent's next turn |
|---|---|---|---|
| `none` | the agent's last input | 22197 of 22239 cached, 112 ms | **0 cached**: 2.7 s re-prefill (control: 22293 cached, 0.2 s) |
| `none` | the agent's head (input + answer) | 22292 of 22336 cached, 113 ms; the head was *moved* | 22292 cached, via the decision's own shared capture |
| `low` | either | 0 cached: 2.7 s, evicts other entries | unaffected |
| `low`, decision with thinking on | either | 0 cached | unaffected |

- The reasoning effort is part of the rendered prompt: the same input with effort `low` and then `none` or `medium`
  reuses 0 tokens; `low` and `low` reuse 3151 of 3156. `/v1/decision` renders thinking off with no effort, so it
  reuses only the cache of an agent running with reasoning `none`. Historical reasoning itself renders the same on
  both paths (+20 tokens each for the same text).
- A decision that does reuse an agent's lineage consumes it (state *move*) instead of copying it (*fork*): the
  agent's next turn loses its cache unless the decision's own shared capture happens to land on the same frontier
  (then the agent's context survives only as a Disposable shared prefix).
- So in-conversation checks (3.2) need two NInfer changes: decisions rendered with the agent's reasoning effort
  (thinking on, then an empty closed think block before the answer), and decisions that fork a protected head.
- The fork is done (NInfer fork `31443f05` + `c8735fe2`, deployed 2026-09-26): a decision on the agent's input or
  head now forks it, and the agent's next turn stays cached (22276 of 22308 after an input decision, 22300 of 22332
  after two head decisions, ~190 ms).
- The effort rendering is done too (NInfer `a61d418c`, deployed 2026-09-26): `/v1/decision` takes
  `reasoning_effort`, renders the agent's preamble for it and closes the think block empty. With the agent at
  effort `low`: a decision on its input reuses 22354 of 22396 tokens (160 ms), one on its head 22534 of 22578
  (114 ms, a second 94 ms), and the agent's next turn stays cached (was: 0 cached, 2.7 s). Label mass 0.98–0.995.
  So in-conversation checks (3.2) can now send the agent's exact messages with its effort.

**0.4 Guard accuracy.** The 852 real commands × the guard questions phrased as questions (`guard_questions_q.json`),
state = context, tool and command. Reference: Qwen3.8-27B's generative answers with reasoning low
(`guard_qwen.jsonl`; yes-counts: destructive 12, stops_process 41, remote_change 18, read_only 571). Its agreement
with the Qwen decision is partly the model agreeing with itself; the user's labels are the real test.

| model | destructive P / R | stops_process P / R | remote_change P / R | read_only agreement | per command |
|---|---|---|---|---|---|
| qwen3.8-27b `/v1/decision` | 1.00 / 0.83 | 0.87 / 1.00 | 0.68 / 0.83 | 0.91 | 311 ms (5090) |
| Kev-9B | 1.00 / 0.75 | 0.93 / 0.95 | 0.05 / 1.00 | 0.81 | 248 ms (4070) |
| laya:typed-decisions | 0.03 / 0.17 | 0.33 / 0.83 | 0.03 / 0.78 | 0.34 | 20 ms (4070) |

P / R = precision / recall of "yes". "Harmless" = p(yes) below a threshold on destructive, stops_process and
remote_change: the Qwen decision at 0.2 calls 696 of 852 commands harmless, none risky in the reference (at 0.5:
782, including 4 borderline ones: scripts written to a remote `/tmp`, a test snapshot overwritten); Kev-9B at 0.2
calls 364 harmless, none risky. `results/phase0/guard_to_label.json` holds 50 commands where the strong models
disagree (the hard cases), hand-labelled 2026-09-26 (by Claude at the user's request; conventions in
`guard_label_conventions.json`; `scripts/guard_label_report.mjs`): 1 destructive, 5 stop a process, 8 change a
shared service or the nuc, 39 read-only. On them the Qwen decision is safe but underrates read-only (6 of 39 at
0.5; `dotnet test` gets 0.05–0.4), so a read-only bar of 0.8 clears none; the three risk questions alone at 0.2
clear 20 of the 39 and no risky command (at 0.3 a hub quit through the tray menu slips through). Accuracy at 0.5:
Qwen decision destructive 49/50, stops 39/50 (8 false yes), remote 45/50; Qwen generative 47 / 45 / 43; Kev-9B
48 / 46 / 10 (40 false remote); Laya 31 / 37 / 19. So the Guardrails second opinion clears on the risk questions
only (NetPI after 2026-09-26).

## CLM-8B (Contrastive Language Models), evaluated 2026-09-27

A System One model from Stanford/NVIDIA (https://contrastive-lm.notion.site, github.com/Contrastive-LM/CLM): a frozen
Qwen3-8B encoder (last-token pooling) and two trained 20M-parameter heads (state, action) scored by cosine × 100;
answer embeddings are cached. Its server (`clm-serve`) speaks TypeSafe `/v1/systemone`, so it could sit behind
AiGateway as a `systemone` engine. Tested on the nuc 4070: the Q8_0 encoder GGUF (czl/CLM-v0.1-8B-GGUF) in llama.cpp,
fully on the GPU (9.0 GB; yue2 stopped for the run), the reference head `CLM_v0.1-8B.pt`. The setup reproduces the
authors' own reference answers (their parity file's invoice anchor: billing 0.98); the base64 embedding transport and
tokenization match; a run with the encoder partly on the CPU gave the same answers.

| task | CLM-8B zero-shot | constant / random | Qwen3.8-27B decision |
|---|---|---|---|
| log lines (120 gold), severity / category | 0.117 / 0.233 | always "warning" 0.117, always "media" 0.242 | 0.892 / 0.675 |
| log lines, actionable / routine (questions) | 0.850 / 0.350 | always "no" 0.850 / 0.308 | 0.875 / 0.908 |
| log lines, actionable / routine (statements) | 0.208 / 0.683 | always "yes" 0.150 / 0.692 | – |
| guard (50 hand-labelled), stops a process | 6/50 (44 false yes) | – | 39/50 |
| guard, destructive (statements) | 1/50 (49 false yes) | – | 49/50 |
| browser (475 Mind2Web steps, ranker top-20), top-1 | 0.034 | random ≈ 0.042 | 0.425 |
| time (4 questions / one step, 4070) | 78–166 ms | | 95 ms batched (5090) |

CLM answers nearly the same for every input, and its yes/no answers flip with the question's wording (questions vs
statements), not with the content. The authors' own parity data shows modest zero-shot accuracy too (the heads pick
the gold answer first in 51 % of their 23,858 test questions). **Not usable zero-shot for our tasks**; the decisions
stay on NInfer (Qwen logit readout), Laya and Kev. What could still work is the architecture with our own data: a head
trained on frozen embeddings takes about an hour (their `train/finetune.py`), and our Qwen-labelled logs, the 852
commands and the Mind2Web training split exist. Files on the nuc: `/home/quazzie/clm` (`start.sh`, `stop.sh`,
README); scripts `decisions-lab/scripts/clm_browser.mjs`, `p0_guard.mjs run clm|clm_s`, `serve_check.mjs`.

## Live computer use (Windows), 2026-09-27

The 0.98 above is one step on a frozen control list. This is the whole loop on real apps. `uia-agent` (decisions-lab,
.NET, no NuGet) reads a window's UI Automation tree, including its menus, popups and owned dialogs, as a numbered list
(35 ms for Calculator's 42 controls, 161 ms for an Explorer window's 79). qwen3.8-27b gets the task, its earlier
actions with what each one changed, and the list, and replies with one action. The agent carries it out through UIA
patterns (Invoke, Toggle, SelectionItem, ExpandCollapse, Value, RangeValue); real clicks and keys are the fallback, sent
only after the target window is confirmed in front. Model calls go through AiGateway `/v1/chat/completions` (client
`decisions-lab/cu_live`, session `<run>/<task>`). 19 tasks in Calculator, Paint, Character Map and Explorer (a scratch
folder per task), at most 15 steps, each checked on the end state (the display, the canvas size, files on disk, the
settings the app saves).

| variant | tasks done | steps | model per step p50 (p90) | per task p50 |
|---|---|---|---|---|
| thinking off | 15/19 | 120 | 175 ms (199) | 4.4 s |
| thinking off, plan line | 16/19 | 93 | 223 ms (295) | 3.9 s |
| reasoning low | 17/19 | 98 | 372 ms (1045) | 4.7 s |
| **reasoning low, plan line** | **18/19** | 88 | 390 ms (602) | 5.2 s |

(18/19 includes a rerun of paint-size after the parser learned `set`: the model had worked out the right value and
written `set Size #53 = 9`.) A step also costs the snapshot, the action (p50 6 ms) and a fixed 500 ms settle. 91 % of
the 322 actions in the four runs used UIA patterns, which do not need the focus: the app can stay in the background.
The other 9 % needed real input (typing into a RichEdit box, key presses, items in Win32 dropdown lists).

What made the difference, in the order it was found (runs 1–4: 11, 9, 11, 16 of 19 with thinking off):
- **Reply format.** With the number first (`29`), the model picked neighbouring numbers (One, Two, Three for "15% of
  80"). With the number and then the name (`33 Five`), the name followed the number, and the model copied attributes
  (`OK id="PrimaryButton"`) that the parser took for text to type. **Verb, name, then number** (`click Five #34`,
  `type Horizontal #8 = 50`) fixed the digits; a number that disagrees with the name is resolved by the name.
- **Feedback.** Each history line says what the action changed ("now shows [text] Display is 8", "no visible change",
  "the order of the controls changed").
- **Intent across steps.** Every step is a new request. At reasoning low the model planned "8 0 × 1 5 % =", clicked 8,
  and on the next turn cleared the 8 as a leftover, seven times in a row. An optional `plan:` line, shown on the next
  turn, fixed it.
- **Thinking** fixes the reasoning slips: which sort order is showing (Explorer), slider position 9 = 10 px (Paint).
- **The harness.** A UIA Select on an item of a Win32 dropdown does not reach the combo box (a real click does); a
  slider's RangeValue is not the value on screen (Paint's Size: position 9 = 10 px, 50 = 78 px); a dropdown's
  "(selected)" follows the mouse pointer.
- **Out of reach for UIA.** Character Map's character grid is custom-drawn and absent from the tree: the € task fails in
  every variant, and the model settles for ₠ "Euro-Currency Sign" and says "done". Such controls need keys or vision.
- **Side effects.** Apps remember state: Paint reopens with the last canvas size (the resize task halved the default
  five runs in a row before it was noticed), Calculator its mode, Character Map its font and view. `cu_restore.mjs`
  puts them back after every run. A lost model copied the prompt's example (`click Five #34`, in Paint) and then
  clicked Close. The test refuses Share, Copilot, sign-in and Delete controls; a real agent needs confirmation for
  those and for Close.
- NInfer reused 0 cached tokens between steps, although the system prompt and task repeat (~1.2k-token prompts, so the
  prefill costs little here).

**Verdict:** viable for built-in and standard Windows apps with a good UIA tree. 16–18 of 19 multi-step tasks
succeed, at 0.2–0.4 s of model time per step, locally and mostly without touching the user's focus. Apps that draw
their own controls need a vision or keyboard fallback. Next: a NetPI plugin (`windows.snapshot` / `windows.act`, the
loop as a tool, confirmation for destructive controls); a done-check through `/v1/decision` (every variant had one
false "done"); harder apps (Office, Electron, Settings read-only); record real steps for training (the traces here
already hold the control list, reply, action and effect per step). Scripts: decisions-lab `uia-agent/`,
`scripts/cu_live.mjs` (`--effort`, `--plan`), `cu_lib.mjs`, `cu_explore.mjs`, `cu_restore.mjs`; results in
`results/cu_live/final-*.json[l]`.

## Known limits and next experiments

- **The log teacher is the ceiling, and the student reaches it.** Retrained on Qwen3.8-27B's labels, Laya went from
  0.53 to 0.78 on the subsystem and matches its teacher on the rest (gold per epoch: 0.79 / 0.93 / 0.95 / 0.91 after
  1, 0.76 / 0.92 / 0.93 / 0.97 after 2, 0.78 / 0.93 / 0.93 / 0.97 after 3: flat after the first epoch, so more epochs
  do not help; a better teacher or more varied lines would). Checkpoint `out/laya/logs-laya-3ep-qwen` on the nuc,
  served by `laya-tasks` as `laya-logs` since 2026-09-26 (first as `logs`); it reproduces its gold scores exactly when served
  (`serve_check.mjs`). The Kev-taught `out/laya/logs-laya-4ep` stays on disk but is not resident (add
  `laya-logs-kev=/t/out/laya/logs-laya-4ep` to compare). `laya-tasks` is a plain container
  that `/home/quazzie/train/serve.sh` starts (not a Dockhand stack); its last line lists the served
  `name=checkpoint` pairs. Next: the user's corrections and tighter subsystem categories.
- **Laya weights in bf16 (2026-09-27).** Laya places its weights in fp32 and runs them under bf16 autocast, so a
  421M model held ~1.7 GB (the whole container 2.7 GB). `serve_tasks.py` now keeps the parameters in the autocast
  dtype (buffers such as the rotary tables stay fp32): the container holds **1.2 GB**. The gold lines score the
  same (0.78 / 0.93 / 0.93 / 0.97), none of the 480 answers flips (probability change p50 0.0003, max 0.047;
  `serve_dump.mjs --diff`), and p50 drops from 37 to 31 ms. Each further Laya model costs ~0.85 GB. `LAYA_WEIGHTS=fp32`
  restores the old placement.
- **Kev-9B and `laya-tasks` do not fit on the 4070 together.** Kev-9B (10.3 GB plus ~0.5 GB of compute buffers)
  failed to load on 2026-09-26 with `cudaMalloc failed: out of memory` while `laya-tasks` held 1.9 GB; with
  `laya-tasks` stopped it loads in 6 s. At 1.2 GB it is still too tight (10.8 + 1.2 of 12 GB). The hub's fit check
  counts only the models it manages, so it reports "fits". The `decide` default is Qwen3.8-27B since 2026-09-27, so
  this only matters when Kev is asked for by name.
- **Browser (2026-09-27).** Qwen3.8-27B on NInfer scores 0.461 (the IQ3_XXS GGUF on the 4070 scored 0.425; the
  candidates' order hardly matters: 0.451 in ranker order), 124 ms per step. As a teacher it labelled all 5,900
  training steps in 6.3 min (0.435 top-1 on them, target always present; `qwen_m2w.mjs label`). The Laya picker
  trained on 0.5 × gold + 0.5 × Qwen reaches 0.278 / 0.314 / 0.320 after epochs 1–3, against 0.234 / 0.253 / 0.280 for the same
  steps with gold labels only: the teacher helps, but a 421M student stays well below Qwen. Qwen × Laya^0.5 gives
  0.478 (the weight was chosen on the test steps, so slightly optimistic). The ranker keeps the target in its
  top-20 on 0.846 of test steps (0.775 of training steps), which caps everything. Files: `decisions-lab` scripts
  `m2w_tops.py`, `qwen_m2w.mjs`, `laya_picker.py` (`TEACHER=`, `ALPHA=`); checkpoint
  `out/laya/picker-laya-3ep-steps-all-qwen0.5` on the nuc. Next: better element descriptions (the ranker's ceiling)
  and NetPI's own browser traces.
- **Computer use (2026-09-27).** Qwen3.8-27B picks the right control in 43 of the 44 Windows cases (0.98; Kev-9B
  0.89) with thinking off, 139 ms per case, reading the whole numbered list (up to 189 controls) and writing the
  number (`qwen_uia.mjs`). Numbers cannot be `/v1/decision` labels (Qwen splits them into
  digits, and a label's first token must be unique), so this is top-1 only. The miss: "Turn on word wrap" in
  Notepad → Settings instead of the View menu. A Windows Laya is not needed for quality; it would only move the
  work off the 5090. Record real steps (control list, choice, outcome) before training one.
- **Guard:** 852 real commands now (0.4), but the reference is Qwen's own generative answer; the user's labels on
  the 50 disputed commands (`guard_to_label.json`) are the first ground truth. Zero-shot Laya is unusable here
  (destructive precision 0.03), although it scored 8–11/12 on the hand-made suite.
- **Stuck, triage:** the suites are 6–8 hand-made cases — enough to rank models roughly, not to trust a choice. They
  need real sets from NetPI's journal before a model is picked for them.
