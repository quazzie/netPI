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
| RTX 4070 | 12 GB | Kev-9B (10.3 GB) or Kev-4B (6.5 GB), the fine-tuned Laya task models (~2 GB for the server), rankers |
| Arc iGPU | ~48 GB shared | small and mid encoders (Laya 421M: 0.4 s/line); Kev is too slow here (Kev-4B 4.9 s) |
| NPU | shared | tiny always-on encoders (MiniLM: 5 ms/line); larger models are slow or inexact |
| shared with | | Frigate runs object detection on the NPU and the iGPU, and video decoding on the iGPU |

## Recommended model per task (current best)

NInfer now has `/v1/decision` (fork, 2026-09-26) and AiGateway answers `/v1/systemone` for NInfer models through
it, so the already-loaded **qwen3.8-27b is the first choice for browser and anything low-volume** (`decide.model:
qwen3.8-27b`; 0.76 / 0.90 / 0.86 / 0.91 on logs, 0.425 on the browser, better than every zero-shot model below). It
costs no memory but shares NInfer's two slots with the agents, so high-volume work (a whole log file) belongs on the
nuc models:

| task | recommended | score | speed / where | runner-up | test set |
|---|---|---|---|---|---|
| **Log lines**: subsystem, severity, needs a human, routine | **Laya-logs-qwen** (421M, fine-tuned on Qwen3.8-27B's labels), served as `logs` by `laya-tasks` (nuc :8010, `/v1/systemone`) | **0.78 / 0.93 / 0.93 / 0.97** | 37 ms/line on the 4070 (all 4 questions, p50 over the LAN; 0.42 s iGPU) | Qwen3.8-27B itself 0.79 / 0.92 / 0.95 / 0.93 (0.7 s/line); Laya-logs on Kev-9B's labels (`logs-kev`) 0.53 / 0.89 / 0.84 / 0.91 | 120 real lines, hand-labelled |
| **Browser**: which element next | **MiniLM ranker (fine-tuned) → top-20 → Laya picker × Kev-9B** | top-1 **0.392** (target in top-20: 0.846) | ranker 65 ms + Laya + Kev ~0.2–0.4 s per step on the 4070 | Kev-9B alone 0.307–0.335; Laya picker alone 0.286 | Mind2Web, 475 steps on 14 websites never trained on |
| **Computer use (Windows)**: which control next | **Kev-9B**, zero-shot, over the UI Automation list | top-1 **0.89**, top-3 0.98 | ~1 s per step on the 4070 (189 controls) | small rankers 0.55 | 44 hand-made tasks in 5 Windows apps |
| **Dangerous command** (guardrail second opinion) | **Kev-9B** | 12/12 | ~0.1 s | decider:0.8b 12/12, Kev-4B 11/12, laya:en 11/12 (9 ms) | 12 hand-made commands — **too small, needs a real set** |
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
| Kev-9B | 9B | zero-shot (GGUF, fork) | 0.54 / 0.87 / 0.82 / 0.90 | 0.307–0.335 | **0.89** | **12** | **6** | **8** |
| Kev-4B | 4B | zero-shot | 0.55 / 0.83 / 0.79 / 0.79 | – | – | 11 | **6** | **8** |
| **Laya-logs-qwen** (served as `logs`) | 421M | fine-tuned (Qwen3.8-27B labels, 3 epochs) | **0.78 / 0.93 / 0.93 / 0.97** | – | – | – | – | – |
| Laya-logs (served as `logs-kev`) | 421M | fine-tuned (Kev-9B labels) | 0.53 / 0.89 / 0.84 / 0.91 | – | – | – | – | – |
| **Laya picker** | 421M | fine-tuned (Mind2Web) | – | 0.286 | – | – | – | – |
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

## Known limits and next experiments

- **The log teacher is the ceiling, and the student reaches it.** Retrained on Qwen3.8-27B's labels, Laya went from
  0.53 to 0.78 on the subsystem and matches its teacher on the rest (gold per epoch: 0.79 / 0.93 / 0.95 / 0.91 after
  1, 0.76 / 0.92 / 0.93 / 0.97 after 2, 0.78 / 0.93 / 0.93 / 0.97 after 3: flat after the first epoch, so more epochs
  do not help; a better teacher or more varied lines would). Checkpoint `out/laya/logs-laya-3ep-qwen` on the nuc,
  served by `laya-tasks` as `logs` since 2026-09-26 (the Kev-taught `logs-laya-4ep` stays as `logs-kev` for a while);
  the served models reproduce their gold scores exactly (`serve_check.mjs`). `laya-tasks` is a plain container that
  `/home/quazzie/train/serve.sh` starts (not a Dockhand stack); its last line lists the served `name=checkpoint`
  pairs. Next: the user's corrections and tighter subsystem categories.
- **Browser picker:** all Mind2Web training sites are used; next is a stronger base (fine-tune Kev-4B as the picker),
  better element descriptions (the ranker's recall ceiling), and NetPI's own browser traces.
- **Computer use:** Kev-9B works zero-shot; record every real step (control list, choice, outcome) to train a
  Windows-specific picker later (public data does not transfer).
- **Guard, stuck, triage:** the suites are 6–12 hand-made cases — enough to rank models roughly, not to trust a
  choice. They need real sets from NetPI's journal before a model is picked for them.
