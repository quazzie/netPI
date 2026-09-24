# Windows bring-up and prompt work (done 2026-09-24)

This record covers steps 1 and 2 of the original handoff plan (commit `6c205e3`) and the follow-up work requested after them. It is
kept for reference. The current state is in `docs/HANDOFF.md` and `docs/STATUS.md`.

## Starting point

- NetPI was built in a Linux cloud sandbox. Five unit suites passed there (Providers 35, Tools 52, Agent 53, Aux 74, Host 38),
  and so did the E2E suite: 55 tests / 687 checks against the scripted mock model server, including a Playwright UI smoke test.
- On Windows it had only been built and started.
- One real run had exposed an nInfer bug, `response.failed: capture owner has no planning ID`. It was fixed in nInfer (live-session
  commit 2fb27c35), which now also keeps stateless (`store:false`) agent chains as lineage heads. Report:
  `C:\AI\src\ninfer-windows\.local\stateless-agents-20260924\report.md`.

## 1. Windows test run

First run: Providers 35/35, Tools 51/52, Agent 53/53, Aux 74/74 and Host 34/38; E2E 52/55. Every failure was in the tests or
the test tooling, and each fix was committed on its own:

| fix | commit |
|---|---|
| Tools tests assert the bash launch production uses on each OS (Git Bash mangles the Linux-style launch) | `4975f28` |
| Host tests build SamplePlugin in the runner's configuration (it was always Debug) | `28761b8` |
| E2E expects Git Bash's `/tmp` spelling for folders under `%TEMP%` | `bf268fc` |
| E2E UI smoke falls back to an installed Edge/Chrome when Playwright's Chromium is missing | `f38544a` |
| E2E UI smoke waits for the retried run to start before waiting for idle | `db96192` |

Also confirmed on Windows: process-tree kill on timeout and on abort, CRLF preservation, and plugin hot reload while NetPI runs.

## 2. Real smoke test (AiProxy → nInfer, `qwen3.8-27b`, 2 lanes)

Two agents ran at once in a scratch project with a temporary home. A spawned a `docs-audit` subagent and waited for it. B was
steered after its first tool batch. The run took 28 s.

| agent | turn | prompt | cached | TTFT |
|---|---|---|---|---|
| A | 1 / 2 / 3 | 5,735 / 6,813 / 7,259 | 0 / 6,010 / 6,889 | 918 / 661 / 223 ms |
| docs-audit | 1 / 2 / 3 | 5,804 / 5,978 / 6,327 | 0 / 5,904 / 6,071 | 732 / 118 / 145 ms |
| B | 1 / 2 / 3 | 5,680 / 6,799 / 7,068 | 0 / 6,706 / 6,873 | 2,502 / 132 / 144 ms |
| B | 4 | 8,139 | 0 | 1,003 ms |
| B | 5 / 6 / 7 | 8,347 / 8,503 / 8,891 | 8,258 / 8,446 / 8,860 | 119 / 119 / 127 ms |

- **Lanes:** the subagent queued while A and B held both lanes. It took A's lane 28 ms after A started waiting, and A resumed
  43 ms after the subagent finished. There were never more than 2 requests in flight.
- **Steering:** it arrived as a `[steer]` message at B's next turn and did not break cache reuse.
- **Clean run:** no failed requests, no server warnings, and the Work tab was correct.
- **Cache miss:** B's turn 4 lost the whole cache, because the system prompt contained the time to the minute. Fixed in
  `701fedf`.

## 3. Follow-up work

| change | commit |
|---|---|
| No date or time in the system prompt; leaner identity and environment | `701fedf` |
| Bare base prompt from the context plugin; lanes and delegation guidance only from their own plugins | `70902cc` |
| No commit hash in assembly versions (unchanged plugins stay byte-identical and don't reload) | `e75e461` |
| `build.ps1` works while NetPI runs: everything except the host is built, and the plugins hot-reload | `3661d4c` |
| Tools resolve Git Bash's `/tmp` (and bare `/c`) paths on Windows | `59bbf93` |
| Work tab lane rows show a top-level agent's session title instead of "main" | `54b491a` |
| UI mock e2e falls back to an installed Edge/Chrome | `24399c3` |
| Desktop drops the WebView2 WPF reference (fixes the MSB3277 warning) | `9452da5` |
| OpenRouter provider plugin | `25041b6` |
| Nothing already sent is rewritten: the system prompt is frozen per session; project, working directory and AGENTS.md changes are appended as notices; tools are sorted by name | `010e3e1` |

**OpenRouter, live** (`stealth/space-bunny-alpha`, free), the same two-agent run with a subagent and a steer:
- 14 turns, with 3 agents in parallel on the `openrouter` pool.
- `reasoning_details` were replayed on later turns without errors.
- 86–99.6 % of each prompt was cached after the first turn; TTFT was about 0.9–1.2 s.
- No failed requests.

**Prefix check after `010e3e1`** (nInfer): one session in project alpha, then switched to project beta, then beta's AGENTS.md
edited.

| step | prompt | cached | previous prompt + output | notices |
|---|---|---|---|---|
| alpha, first call | 5,465 | 0 | – | project, instructions |
| alpha, after a tool | 5,553 | 5,535 | 5,536 | – |
| alpha, next message | 5,605 | 5,582 | 5,583 | – |
| beta, first call after the switch | 5,967 | 5,662 | 5,663 | project, instructions (added at the switch) |
| beta, after a tool | 6,036 | 6,018 | 6,019 | – |
| beta, AGENTS.md edited | 6,226 | 6,088 | 6,089 | instructions |

The model followed beta's rules, then the edited ones.

## Not done from that plan

- Live test of the Claude provider (no Anthropic API key yet).
- Per-turn cache reuse and TTFT in the UI.

Both remain in `docs/HANDOFF.md` under "Suggested next steps".
