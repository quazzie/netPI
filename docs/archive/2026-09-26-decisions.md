# Decision models (Jev/Kev) for NetPI: setup, measurements, training (2026-09-26)

A decision model ("System One": TypeSafe's Jev and its open clones) answers typed questions about a state — yes/no
(`noul`), pick one (`choice`), a score on levels — with a probability per answer, in one forward pass and without
writing text. NetPI uses one as a fast helper the agent drives: Qwen writes the questions, the decision model answers
them for one text or thousands of lines.

## What runs where

- **nuc router** (`llama-router`, Dockhand stack `ai`): image `quazzie/llama.cpp:kev-router`, a build of
  [espetro/llama.cpp](https://github.com/espetro/llama.cpp) (`kev` branch, b11127) with one patch — router mode proxies
  `/v1/systemone` to the model named in the body (upstream forwards every other endpoint but not this one). Presets
  `kev-9b` and `kev-4b` (Q8_0 GGUFs with the pointer head baked in; q4 breaks the calibration) are in `models.ini`.
  Rollback: image `ghcr.io/ggml-org/llama.cpp:server-cuda` and `config/models.ini.pre-kev-20260926.bak`. Build tree:
  `/home/quazzie/build/llama-kev` on the nuc.
- **AiSwitcher** (`C:\AI\Projects\AiSwitcher`, D66/D67): model field `api: "systemone"` (exposed in `/v1/models`) and
  `exclusive: true` (switching to the model drains/unloads yue2 and closes its gate; switching away opens it again).
- **NetPI:** `plugins/NetPI.Decide` (`decide` tool, `decide.ask` RPC, `docs/TOOLS.md`); the AiProxy provider leaves models
  with an `api` out of the chat list.
- **Ollaya** (Dockhand stack `decide`, `:11435`): the small open decision models (Laya, decider, Kev-0.8B, NLI,
  GLiClass, Von, Qwen3Guard) behind the same API; idle it holds no VRAM. It keeps up to 3 models loaded for 10 min, so
  run it only when the router's model leaves room.

### Router regression (every preset on the fork, 4070 12 GB)

All ten presets load and answer on the fork with the same VRAM as stock. `gemma-4` (11697 MiB) and `gemma4q6`
(11799 MiB) only load when the **yue2 process is stopped**: its idle CUDA context (204 MiB, even with its model
unloaded) takes the last headroom. That is true on the stock image too — production's log shows both exiting with
status 1 — so it is an existing issue, not the fork. Kev-9B: 10273–10483 MiB, loads in 5–9 s, ~140 ms per request.

## Zero-shot accuracy (small hand-made suites, labels by Claude)

| model | size / VRAM | warm latency | guard /12 | stuck /6 | triage /8 | logs cat/sev/act /20 | browser step /13 |
|---|---|---|---|---|---|---|---|
| **kev-9b** (GGUF, fork) | 10.3 GB | 85–240 ms | **12** | **6** | **8** | 19 / **19** / **20** | 6 |
| kev-4b (GGUF, fork) | 6.5 GB | 60–180 ms | 11 | 6 | 8 | **20** / 15 / 18 | 5–6 |
| decider 2B (Ollaya) | doesn't fit fp32 | 1.6–8 s (CPU) | 11 | 5 | 8 | 18 / 17 / 17 | 5–6 |
| laya:typed-decisions | ~1 GB | 9 ms | 8 | 6 | 8 | 16 / 13 / 14 | 2–3 |
| laya:en | ~1.2 GB | 8–16 ms | 11 | 5 | 6 | 15 / 12 / 15 | 2–3 |
| decider:0.8b | ~3.5 GB | 85–450 ms | 12 | 4 | 6 | 17 / 13 / 14 | 3–4 |
| kev (0.8B, Ollaya) | ~4.5 GB | 60–350 ms | 8 | 4 | 7 | 18 / 16 / 8 | 2–4 |
| nli / nli:modernbert / gliclass / von | 0.4–0.6B | 10–60 ms | 6 | 2–5 | 2–5 | 11–16 / 7–10 / 10–11 | 3–6 |

Browser steps (operation + target from a synthetic element list) are weak for every model zero-shot.

## Computer use on Windows (UI Automation)

`uia-dump.ps1` (scratch) lists a window's actionable elements from the UI Automation tree as `[type] name id="…"`,
the same shape as a browser element list. 44 hand-made goal → element cases over Calculator (38 elements), Notepad (23),
Settings (36), Paint (60) and File Explorer (189):

| chooser | top-1 | top-3 | top-10 |
|---|---|---|---|
| **kev-9b over the full list** (2.4 s/case, shared GPU) | **0.89** | 0.98 | 1.00 |
| bge-small-en-v1.5, cosine | 0.55 | 0.73 | 0.82 |
| bge-base-en-v1.5, cosine | 0.52 | 0.73 | 0.80 |
| ms-marco-MiniLM-L6 cross-encoder | 0.55 | 0.73 | 0.89 |

UIA names are clean, so Windows apps are easier than the web; Kev-9B picks the right element zero-shot.

## Training (the nuc: `/home/quazzie/train`, `run.sh <script>` = uv env with torch 2.8 cu128)

Scripts: `build_logs.py`, `label_logs.py`, `train_logs.py`, `prep_m2w.py`, `train_m2w.py`, `eval_elements.py`,
`pipeline.sh` in `/home/quazzie/train`; data in `data/`, results in `out/`.

### 1. Log model — distilled from Kev-9B

- **Data:** 5003 unique message templates (numbers, times, ids and IPs masked; one example per template, ≤ 250 per
  source) from 33 sources: the nuc's containers and journal, the server's containers (Plex, the *arr stack, AdGuard,
  Dockhand, MariaDB, …), NetPI (`diag.logs`) and AiHub.
- **Teacher:** Kev-9B answered four questions per line (severity; subsystem, 15 classes; "a human needs to fix
  this"; "routine noise you can hide") — 29 min for 5003 lines, 0 errors (`log_questions.json`).
- **Gold:** 120 lines across all sources labelled by hand (Claude).
- **Student:** a sentence embedding + one logistic regression per question, trained on the teacher's labels (3906
  lines; 977 held out). "With corrections": 5-fold over the gold, 4/5 of it added with weight 10.

| | subsystem | severity | needs a human | routine | speed |
|---|---|---|---|---|---|
| Kev-9B (teacher) vs gold | 0.54 | 0.87 | 0.82 | 0.90 | ~0.35 s/line (4 questions) |
| all-MiniLM-L6-v2 (22M) + LR: agrees with teacher | 0.89 | 0.94 | 0.96 | 0.92 | 16 ms/line on CPU, 0.26 ms batched on the 4070 |
| same, vs gold | 0.50 | 0.84 | 0.83 | 0.86 | |
| same, vs gold with corrections | 0.58 | 0.87 | 0.83 | 0.90 | |
| bge-small (33M), vs gold / with corrections | 0.48 / 0.54 | 0.84 / 0.83 | 0.81 / 0.83 | 0.89 / 0.91 | 27 ms/line on CPU |
| Qwen3-Embedding-0.6B, vs gold / with corrections | 0.52 / 0.58 | 0.83 / 0.88 | 0.80 / 0.83 | 0.87 / 0.88 | 2.7 ms/line batched on GPU |

The 22M student matches its teacher on every question at ~1/1000 of the cost and runs on a CPU: no model switch on
the nuc. The subsystem question is the weak one for teacher and student alike, and it is the taxonomy, not the model:
most disagreements are overlaps (network vs request 8, media vs storage 7, job vs other 6). "Needs a human" misses
are judgement calls a single line cannot settle (a lone stack frame, a transient connection reset). A few of the
user's corrections help where the taxonomy is the problem (+5–8 points on subsystem from ~96 examples).

### 2. Browser model — Mind2Web element ranker

- **Data:** Mind2Web train shards 0–3 and 10: 2890 steps (409 tasks, 70 websites; CLICK 84 %, TYPE 12 %, SELECT 4 %),
  each with ~400 candidate elements rendered as `[tag] text attr="…"` from the page's cleaned HTML. Split **by website**:
  2415 training steps, 475 test steps on 14 websites never seen in training.
- **Ranker:** `cross-encoder/ms-marco-MiniLM-L6-v2` (22M) fine-tuned on (task + previous actions, element) pairs — every
  positive and 31 sampled negatives per step, 77536 pairs, 2 epochs: **1.6 min** on the 4070.

| ranker (unseen websites) | R@1 | R@5 | R@10 | R@20 | R@50 | ms/step (GPU) |
|---|---|---|---|---|---|---|
| bge-small, cosine (zero-shot) | 0.02 | 0.07 | 0.12 | 0.19 | 0.36 | 104 |
| bge-base, cosine (zero-shot) | 0.05 | 0.15 | 0.19 | 0.28 | 0.44 | 288 |
| MiniLM cross-encoder (zero-shot) | 0.03 | 0.10 | 0.16 | 0.24 | 0.42 | 117 |
| **MiniLM cross-encoder, fine-tuned** | **0.19** | **0.51** | **0.70** | **0.84** | **0.93** | 65 |

**Two stages — the fine-tuned ranker's top-k, then Kev-9B picks one** (zero-shot, the same 475 steps):

| Kev-9B picks from | top-1 (right element first) | Kev time per step |
|---|---|---|
| ranker top-5 | 0.297 | 0.09 s |
| ranker top-10 | 0.318 | 0.18 s |
| ranker top-20 | **0.335** | 0.39 s |

The chooser adds +76 % over the ranker's own first pick (0.19 → 0.34). Published fine-tuned Mind2Web systems report
roughly 40 % element accuracy on its cross-website test (a different split, so not directly comparable).

### 3. Computer-use model — Windows UI Automation

Kev-9B picks the right element zero-shot in 39 of 44 cases (table above). Two of its five "misses" are valid
alternatives (the Settings search box for "turn on dark mode" and "which apps can use my camera"); the other three
need app knowledge or step order (after "Seven" it picks "Eight", not "Plus"; Calculator's modes are behind "Open
Navigation"; Notepad's font is under Settings). The Mind2Web ranker does not transfer (top-1 0.57, top-10 0.80 — no
better than zero-shot embeddings): web pages and native apps look different.

## Round 2: learning curves, larger rankers, per-task Laya fine-tunes

The first round was one configuration per model. The second measured where the returns flatten.

**Log student learning curve** (MiniLM + logistic heads; agreement with Kev-9B on held-out / accuracy on gold):

| teacher-labelled lines | subsystem | severity | needs a human | routine |
|---|---|---|---|---|
| 100 | 0.72 / 0.31 | 0.84 / 0.75 | 0.93 / 0.83 | 0.85 / 0.82 |
| 500 | 0.81 / 0.42 | 0.89 / 0.80 | 0.94 / 0.82 | 0.89 / 0.86 |
| 2000 | 0.87 / 0.47 | 0.93 / 0.85 | 0.96 / 0.82 | 0.92 / 0.86 |
| 3906 | 0.89 / 0.50 | 0.94 / 0.85 | 0.96 / 0.83 | 0.92 / 0.86 |

"Needs a human" is flat from 100 lines, "routine" from ~2000; subsystem keeps rising. An MLP head changes nothing:
the frozen embedder is the limit.

**Browser ranker sweep** (same 14 unseen websites; ±2 points run-to-run noise):

| run | R@1 | R@20 | R@50 |
|---|---|---|---|
| MiniLM-L6, 25 % / 50 % / 100 % of 5 shards | 0.13 / 0.15 / 0.17 | 0.69 / 0.76 / 0.84 | 0.86 / 0.89 / 0.93 |
| 1 / 2 / 4 epochs | 0.14 / 0.17 / 0.21 | 0.79 / 0.84 / 0.84 | 0.90 / 0.93 / 0.93 |
| MiniLM-L12 (33M) | 0.20 | 0.84 | 0.92 |
| all 11 shards (2.4× data) | 0.20 | 0.82 | 0.92 |
| bge-reranker-base (278M) | 0.23 | 0.84 | 0.93 |

Top-20 recall plateaus at ~84 % whatever the data, epochs or model size: the element descriptions extracted from the
HTML (31748 candidates had no match in the cleaned HTML, many elements have no text) are the ceiling.

**Laya per-task fine-tunes** (the laya repo's typed-decisions recipe: full 421M encoder, RLCD + soft cross-entropy
against the teacher's distributions; `laya_ft.py`, `laya_picker.py`):

- **Log model** (`out/laya/logs-laya-4ep`, 4 epochs, 33 min on the 4070, soft labels from Kev-9B): on gold, subsystem
  0.53, severity 0.89, needs a human 0.84, routine 0.92 — at or above its 9B teacher on three of four questions; flat
  after epoch 2–3. Served by `serve_tasks.py` (laya's own `/v1/systemone` server with local checkpoints registered by
  name; container `laya-tasks`, `:8010`, model `logs`): the same scores over HTTP, **37 ms per request** (4 questions)
  on the 4070 with ~1.9 GB VRAM; 2.5 s on CPU (fp32 PyTorch — an ONNX export would be the CPU path). The temperatures
  are not refit yet (the notebook's last step), so its confidences are uncalibrated.
- **Browser picker** (choose among the ranker's top-20, options shuffled, trained only on the 56 training websites):
  2415 steps → top-1 0.185 after 3 epochs (2.9 min on the 5090), flat from epoch 2; zero-shot Laya 0.09;
  **Kev-9B zero-shot 0.335**. With all 11 shards (5900 training steps; the test set stays the same 475 steps):
  0.221 → 0.246 → 0.278 → **0.284** over 4 epochs (8.7 min on the 5090). Data was the lever (2.4× data: 0.185 → 0.284);
  every Mind2Web training site is now used.

**Combining classifiers** (the user's idea):

- Logs (Kev-9B, Laya-logs, MiniLM student, zero-shot laya:typed-decisions on the 120 gold lines): averaging never beats
  Laya-logs alone, and agreement between two models adds about one point. The students were all distilled from Kev-9B
  and share its mistakes, so they cannot correct each other.
- Browser (trained Laya picker + zero-shot Kev-9B, same 475 steps and option order): Laya 0.286, Kev 0.307, average
  0.345, **product 0.392** (+28 % over Kev, no weights to tune), oracle 0.451. When the two agree (24 % of steps) they are
  right 60 % of the time — a usable act-or-ask signal. The ranker's order as a third voter or as a hint did not help
  (0.34–0.38): its own first pick is only 0.175. Ensembles pay off when the models learned differently.

**The nuc's Intel side** (Core Ultra 5 125H: Arc iGPU with ~48 GB of shared memory, NPU "AI Boost", OpenVINO 2026.4;
Frigate already runs object detection on both the NPU and the iGPU):

| model | NPU | iGPU (Arc) | CPU | 4070 (CUDA) |
|---|---|---|---|---|
| Laya-logs 421M, 4 questions per line | 1192 ms, one head wrong (routine 0.31) | **424 ms**, exact | 4343 ms (OpenVINO CPU: wrong answers) / 4796 ms PyTorch | 37 ms |
| MiniLM embedder, 1 line | **5.3 ms** | 3.1 ms | 10.1 ms | |
| MiniLM embedder, per line in a batch of 32 | 6.1 ms | **1.4 ms** | 10.9 ms | |
| MiniLM ranker, one 400-candidate page | 2.3 s | 0.48 s | 4.3 s | 65 ms |
| Kev-4B (llama.cpp Vulkan build), 4 questions | – | 4.9 s | – | ~0.2 s |

The NPU suits tiny always-on models (the MiniLM log student at ~5 ms/line); the iGPU is the better Intel device for
anything larger; Kev is too slow on either for interactive use (a decision model is all prefill, the iGPU's weak
spot). The fork's OpenVINO build of llama.cpp crashes with "illegal instruction" on this CPU (built for AVX-512).
While our models ran, Frigate's NPU detector went from 27 to 35 ms and its iGPU detector from 18 to 30 ms (Kev-4B);
it recovered as soon as they stopped.

## Other people's experience (2026)

- Laya fine-tuned on all of Mind2Web (8613 decisions, 4 epochs, 33 min on 2×T4): element accuracy 0.18 → 0.50;
  8 epochs overfit, LoRA was too small; "candidate recall is the ceiling" (21–27 % lost before the picker).
- Kev's fine-tune guide: ~1000 examples for a measurable gain (Kev-4B 67.7 → 73.6 %); 400 was inside the noise; start
  from the released checkpoint (0.84 vs 0.33 from the base).
- Small models on journalctl severity: Qwen3-4B 96 % and 0.6B 88 %, both only with retrieved examples (RAG).
- SetFit (contrastive fine-tune of the embedder, then logistic regression) reaches near full-data accuracy with 8–64
  examples per class — the fix for a frozen embedder.
- Computer use: OS-Genesis (explore apps, record before/after accessibility states, let a large model write the task
  afterwards), WinDOM (distil a large model into ~2B for Windows grounding), LUMOS (UIA-grounded agents).

## What this suggests

- **Log model:** ship the distilled student (MiniLM + logistic heads, a few hundred KB of weights on top of a 22M
  embedder) for always-on log watching on a CPU; use Kev-9B as the teacher when the taxonomy or the questions change,
  and let the user's corrections retrain the heads in seconds. Tighten the subsystem classes first.
- **Browser model:** a small fine-tuned ranker finds the candidates (top-20 holds the target 84 % of the time on
  unseen sites) and a decision model or Qwen picks among them. More Mind2Web shards (7 unused), a bigger reranker and
  NetPI's own browser traces are the next levers.
- **Computer-use model:** start with Kev-9B over the UIA list (it works now); record every step (element list, choice,
  outcome) to train a Windows ranker/chooser later — public data doesn't transfer to native apps.
