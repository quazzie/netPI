# File tool reliability: ordered batches, replace, and newline handling

Status: planned; investigation and implementor handoff, 2026-09-22.

The user requests multiple calls in one assistant response to edit the same
file successfully in sequence, a new `replace` tool, and CRLF/LF-tolerant
matching in both `edit` and `replace`. The user confirmed that `replace`
means replacing multiple literal occurrences, with a match-count guard.

This plan updates the behavior described in PLAN-v1 §§11, 18–21. It does
not implement the change. No runtime files were changed during investigation.

## 1. Findings in the current source

| Location | Finding and consequence |
|---|---|
| `plugins/NetPI.Agent/AgentRuntime.cs`, tool batch around lines 463–510 | Every prepared call starts before `Task.WhenAll`. Returning results in call order does not order file operations. Two edits can read the same contents and overwrite each other's changes; a dependent second edit can fail before the first lands. |
| `plugins/NetPI.Tools/ToolsPlugin.cs`, `EditTool` | Reads the entire file, uses ordinal string matching, and writes the entire result. There is no coordination between file calls. |
| Same file, `ReadTool` | Normalizes CRLF and lone CR to LF before producing its line-numbered display. The displayed text hides the very distinction `edit` currently requires. |
| Same file, `EditTool.CountOccurrences` | No newline normalization. Matches are non-overlapping; empty search returns zero. Missing `newText` silently becomes an empty string through `Args.Str`, so malformed calls can delete text. |
| Same file, `Args.Schema` | Schemas have no `required` list. Runtime preflight only validates tool presence and that arguments are a JSON object. Tool-side validation remains essential. |
| Same file, `EditTool` file I/O | `ReadAllTextAsync` / default `WriteAllTextAsync` does not preserve the original encoding/BOM. The preservation comment is stronger than the implementation. |
| `src/NetPI.Abstractions/Tools.cs` | `IAgentTool` has no scheduling metadata. `ToolContext.ResolvePath` returns rooted paths unchanged, so it alone is not a canonical grouping key. |
| `plugins/NetPI.Provider.AiProxy/AiProxyProvider.cs`, `RunChatCompletionsAsync` / `RunResponsesAsync` | Completed calls are assembled from `tools.Values` / `state.Tools.Values`; semantic wire ordering is not explicitly enforced. Chat tracks integer indices, while Responses state is keyed by item identity. |
| `plugins/NetPI.Agent/AgentPlugin.cs` | One `AgentRuntime` instance serves concurrent runs. A runtime-owned file coordinator can therefore protect overlapping calls across sessions as well as within a batch. |
| `web/netpi-web/src/components/conversation/AssistantMessage.svelte` | Artifact pills recognize only `write` and `edit`. |
| `web/netpi-web/src/components/conversation/ToolCallBlock.svelte` | File path display is generic, but edit glyph/name handling needs `replace`. |
| `plugins/NetPI.Context.Pi/ContextPiPlugin.cs` | Built-in prompt lists current tools explicitly and should mention `replace`. |

Existing coverage includes three simple edit tests in `ToolTests.cs` and
`ParallelToolCalls_AllExecuteInOneBatch` in `AgentScenarioTests.cs`. These do
not cover newline mismatch or same-file ordering. This investigation was
source-based; it did not run or claim a passing test baseline.

At the start of investigation, the working tree contained unrelated runtime,
runner, orchestration, storage, and test changes; its status changed during
this investigation through other work. Inspect the current diff before
implementation; preserve others' changes and reconcile edits locally. Do not
reset or replace `AgentRuntime.cs` with an older version.

## 2. Required behavior

### Ordered file calls

- In one assistant response, calls to `read`, `write`, `edit`, and `replace`
  targeting the same canonical file execute in their original model call
  order. Each call reads the result of the preceding successful call.
- Different files and tools without a declared file target remain concurrent.
  Do not serialize the whole batch or disable parallel tool calls at the provider.
- Include `write` to prevent overwrite races and `read` so edit-then-read sees
  the new contents. Serializing two reads of one file is acceptable for v1.
- Across sessions in the same host, file groups targeting the same path must
  not overlap. Hold the path gate for the entire batch's group, so another
  session cannot slip between two dependent edits. Order between competing
  sessions is unspecified; order within each group is guaranteed.
- If any call in a file group fails, skip its remaining calls with explicit
  error results identifying the failed predecessor. Other groups continue.
  This is a deliberate new failure rule, including a failed `read`; document
  that read-missing-then-write-create should be separate turns. Successful
  earlier edits stay applied: a batch is not a filesystem transaction.
- Cancellation while waiting must prevent later calls from starting and must
  release coordination state. Do not leave detached mutation tasks running.
- Keep one result per call in original overall order, the existing batch
  events, persistence/checkpoint boundaries, and service leases.

Scope: these guarantees cover declared file tools invoked through this
runtime. Shell scripts, grep/directory traversal, external editors, other host
processes, and hard-link/symlink aliases are not coordinated by this feature.
Do not infer shell dependencies by parsing command text. Agents must put a
dependent build/shell command in a later batch. Case-insensitive Windows keys
may conservatively serialize distinct files in case-sensitive directories.

### Tool contracts

Keep `edit(path, oldText, newText)` compatible with existing callers, except
that CRLF and LF compare equivalently. It still requires exactly one literal
match; zero or multiple matches fail without writing.

Add:

```json
{
  "path": "src/Example.cs",
  "oldText": "OldName",
  "newText": "NewName",
  "expectedCount": 3
}
```

`replace` replaces **all** non-overlapping literal occurrences in one file.
Require `expectedCount` as a positive integer and require the actual count
to equal it before writing anything. This gives the new tool one clear mode,
with no regex, occurrence selector, recursive search, or implicit replace-all
fallback. `expectedCount: 1` is valid, though `edit` is the usual unique-span tool.

For both tools:

- Require nonblank string `path`, nonempty string `oldText`, and a present
  string `newText`. An explicit empty replacement is valid deletion. Whitespace
  search strings are valid; do not trim search or replacement text.
- Reject null, missing, and wrong-type values. Reject fractional, zero,
  negative, and out-of-range `expectedCount`; do not coerce them.
- Declare required fields and integer/minimum constraints in the schema,
  and validate them at execution. Do not globally break unrelated schemas.
- Match case-sensitively and ordinally after newline normalization only.
  Tabs, spaces, indentation, punctuation, and Unicode remain exact. No fuzzy
  matching and no conversion of the literal characters backslash+n to a newline.
- Return concise errors with the path, actual/expected match count where
  relevant, and a recovery hint: reread the file or widen `oldText`. Report
  success with replacement count. Do not return an entire large file.
- Treat no-op output as successful without rewriting the file.

### Newline and file preservation

Normalize CRLF and LF to LF in a **comparison view**, not in the stored file.
Leave lone CR literal in this first version; legacy CR-only file handling is
not necessary to solve the reported issue. Say this explicitly in guidance.

Build a normalized-index-to-original-offset map. Use normalized matching for
both counting and locating spans, then splice those spans out of the original
text. Never try raw matching first: a raw unique match can hide another match
whose only difference is line endings. Apply matches from the original
snapshot, without rescanning inserted replacement text.

For each replaced span, convert CRLF/LF in `newText` to a deterministic target:

1. The first newline sequence inside the matched original span, if any.
2. Otherwise the file's most frequent CRLF/LF sequence; ties use the first
   sequence encountered in the file.
3. Otherwise LF, independent of the host OS.

This keeps homogeneous files homogeneous and preserves mixed-line-ending
content outside edited spans. Separate occurrences in `replace` may use
different styles because their original spans differ. Do not silently append
or remove an EOF newline outside the requested replacement span.

Use one shared text-editing implementation for `edit` and `replace`. Preserve
encoding and BOM for supported files: strict UTF-8 with or without BOM, and
BOM-marked UTF-16/UTF-32 (recognize longer BOMs first). Reject undecodable or
unsupported data instead of silently substituting characters. Keep binary
detection encoding-aware so valid UTF-16 is not rejected just for zero bytes.
Unchanged prefix/suffix bytes and the BOM must survive; verify with byte tests,
not only decoded-string assertions. Do not broaden this into a new encoding
framework for all tools; `write` retains its full-file UTF-8 contract.

Compute and validate everything before writing. Use a sibling temporary file
and a tested same-volume replacement operation for edit/replace, preserving
target permissions/attributes as appropriate. Clean up the temp file on
failure/cancellation; do not delete the original before moving the temp file.
Do not claim a transaction spanning multiple tool calls or protection against
external writers. Avoid unsafe replacement of reparse-point targets: either
resolve them consistently for both locking and mutation or return a clear
unsupported-target error in this version. Leave the original intact on all
validation failures and pre-commit I/O failures.

## 3. Implementation sequence

### A. Fix call order at the provider boundary

In `AiProxyProvider.cs`, assemble completed Chat calls by their numeric tool
index, not dictionary enumeration. For Responses, retain the semantic
`output_index` with each function-call item and assemble by that index.
Preserve item IDs versus call IDs and existing argument accumulation/dedupe.
Handle servers/tests that omit indices with a deterministic encounter-order
fallback; define ordering for mixed indexed/unindexed items explicitly, with
indexed items ordered first and unindexed items in encounter order after them.
Do not order by completion-event arrival or lexical call ID.

Add SSE fixtures with interleaved argument deltas and reverse arrival of
indexed calls. Verify the final assistant call list for both wires. Do not
alter model request retry or response-chain ownership behavior.

### B. Add scheduling metadata and the file-group executor

Add an optional capability in `src/NetPI.Abstractions/Tools.cs`, for example:

```csharp
public interface IFileTargetTool
{
    string GetTargetPath(ToolContext context);
}
```

Implement it on `read`, `write`, `edit`, and `replace`. It declares one target
path without reading or changing the file; tools validate path arguments
before returning it. Keep unrelated `IAgentTool` implementations unchanged.
The runtime must not reference `NetPI.Tools` or switch on tool names.

Construct a prepared context once, using the exact execution workspace and
trusted identity fields already supplied by the runtime. Resolve the target
against that workspace and always apply `Path.GetFullPath`, including rooted
inputs. Use `OrdinalIgnoreCase` on Windows and `Ordinal` elsewhere. Relative,
absolute, dot-segment, and ordinary slash variants must group identically.
Metadata/path exceptions become that call's preflight error, not a crashed
batch. If a failing call still has a valid target key, keep it in its group
so later calls are skipped consistently. Calls with no valid key cannot be
associated with a file and simply return their own preflight error.

Replace the unconditional start-all loop with parallel execution of groups:

```text
prepare every call, recording original index and optional canonical path
allocate results indexed by original call order
for each file group, concurrently with the other groups:
    acquire runtime-wide path gate (cancellable)
    for each call in the group, in original order:
        if predecessor failed: record explicit skipped error
        else: check cancellation; execute; store result
    release gate in finally
execute calls without a file target concurrently as before
await all work; publish/persist results in original overall order
```

The coordinator belongs to the shared `AgentRuntime` instance, not a run-local
dictionary. Group sequencing is explicit; a semaphore alone does not promise
model call order. Use reference-counted gate entries with atomic lookup,
reference increment, and removal under one synchronization mechanism, or an
equivalent proven design. Include waiters in ownership counts. Never remove a
gate while another caller can still use it and create two gates for one path.
Remove idle entries so a long-running host does not retain every visited path.

Hold the existing tools lease through waiting and execution. Preserve run
`AsyncLocal` context when dispatching groups; avoid `Task.Run` unless needed.
Cancellation must not be turned into an ordinary failure and then start later
mutations. Audit existing broad exception catches in the touched execution
path and handle `OperationCanceledException` deliberately.

Keep `BeforeToolCall` as the existing batch announcement, which does not mean
the call has acquired its file gate. Avoid a protocol redesign. Existing UI
duration may include the wait; document that instead of implying new timing
precision. Preserve `AfterToolBatch` / assistant completion semantics.

### C. Implement shared matching, persistence, and replace

Extract focused helpers from the monolithic `ToolsPlugin.cs`, such as
`TextFileEditor.cs` and `ReplaceTool.cs`; file names are suggestions. Both
mutation tools must share validation/matching/newline/write behavior.

Register `ReplaceTool` in `ToolsPlugin.LoadAsync`. Update descriptions,
schemas, and guidelines together. Explain the distinction between unique
`edit`, guarded multi-occurrence `replace`, and whole-file `write`; explain
same-file call order and the fact that a failed group stops remaining calls.

Also validate required path/content arguments on `write` while touching its
file-target metadata, so missing content cannot become an accidental empty
overwrite. Do not change its explicit overwrite semantics.

### D. Integrate the tool and update behavioral docs

- Add `replace` to the built-in Context.Pi tool list.
- Add `replace` as a modified-file artifact in `AssistantMessage.svelte` and
  give it the edit glyph/appropriate name in `ToolCallBlock.svelte`.
- Update the AGENTS hub's docs map, tool inventory, and concurrent-batch
  invariant when implementation lands. Link this plan as the detailed source.
- Add pointers from PLAN-v1 §§11/21 to the implemented behavior rather than
  silently leaving contradictory authoritative rules. Update protocol/plugin
  docs only where scheduling/lease descriptions actually need correction.
- No new WebSocket envelope or provider-specific `replace` special case is
  needed; tools already travel through the registry and provider schemas.

## 4. Acceptance tests

Use deterministic completion gates/barriers for scheduling tests, with bounded
test timeouts. Do not rely on arbitrary sleeps or repeated stress luck.

| Area | Required assertions |
|---|---|
| Same-file dependent edits | One batch changes `alpha` to `beta`, then `beta` to `gamma`; both succeed and final content is `gamma`. |
| Same-file independent edits | Changes to two different spans both survive; force the first call to wait and prove the second has not entered. |
| Mixed file tools | `write → edit → replace → read` on the same path sees each intermediate result. |
| Canonical path | Relative/absolute, `.`/`..`, Windows slash and case variants share the gate; different workspaces' relative `a.txt` do not. |
| Concurrency retained | A blocked group for file A does not prevent a call for B or a non-file fake tool from executing. Existing parallel-batch tests stay valid. |
| Concurrent runs | Two sessions using one runtime cannot overlap same-path groups; a group's two calls cannot be interleaved by the other run. |
| Failure | First file call fails; successors report skipped errors, originals remain as appropriate, and a different file's group completes. Include keyed preflight errors. |
| Cancellation and cleanup | Cancel a waiting group and an in-progress group; no later mutations run; subsequent runs can acquire the path; idle gate entries are reclaimed. |
| Event/result/lease behavior | Original call IDs and result order survive out-of-order group completion; expected events occur once; leases cover queued work and release on all terminal paths. |
| CRLF/LF | Test all four file/search newline combinations for both tools, including multiline needles and replacements. No stray CR, doubled CR, or unintended whole-file normalization. |
| Mixed endings and ambiguity | A CRLF occurrence and LF occurrence count as two equivalent matches: edit fails unchanged; replace with count two succeeds. Untouched mixed suffix/prefix bytes stay identical. |
| Guarded replace | Zero, one, many, mismatched expected count; non-overlapping matching (`aaa` searching `aa` is one); replacement containing oldText does not cascade. |
| Validation | Missing/wrong-type/null path/oldText/newText; empty needle; blank path; invalid expectedCount. Explicit empty replacement works. Malformed calls never truncate files. |
| Exactness | Spaces/tabs/case differences still fail, literal backslash+n stays literal, lone CR remains literal, non-ASCII and supplementary Unicode survive. |
| Preservation | UTF-8 BOM/no BOM, BOM-marked UTF-16/32, no final newline, trailing newline, homogeneous and mixed EOL; compare bytes outside replacements and BOM. |
| Safe write | Match/encoding/count failures do not touch the original; no-op avoids rewrite; simulated temp-write/commit failure leaves original and cleans temp files. |
| Provider order | Both wires' interleaved/reordered indexed SSE fixtures produce semantic call order; missing-index fallback is stable. |
| Registration/UI | Registry exposes replace with required schema and guidelines; successful replace renders a modified artifact and failed/skipped replace does not. |

Extend `ToolTests.cs`, `AgentRuntimeTests.cs` / `AgentScenarioTests.cs`, and
provider SSE tests; add focused helper/coordinator tests where necessary.
Include at least one runtime test using real file tools in a temp workspace,
not only mocked executors. Add the red regression tests before implementing
the fix. Preserve all unrelated persistence/recovery and orchestration tests.

## 5. Verification and deployment handoff

Run focused tests first, then the repo-required final checks:

```powershell
dotnet build NetPI.sln
dotnet test NetPI.sln
```

From `web/netpi-web`:

```powershell
npx svelte-check --tsconfig ./tsconfig.app.json
npx vite build
```

Report pre-existing warnings/failures separately from regressions; do not
assume the dirty working tree has a green baseline. Review the final diff for
accidental source-wide line-ending changes and staged/generated payloads.

**Deployment requires an updated host, not just hot-reloaded plugins.** Adding
`IFileTargetTool` changes the shared Abstractions assembly; `PluginLoadContext`
always resolves that assembly from the host's default ALC. A host already
running the old assembly cannot gain the new type by reloading Tools/Agent.

After verification, publish coherent plugin artifacts with
`pwsh tools/publish-plugins.ps1 -Configuration Debug`, arrange a clean idle
host stop, and relaunch through the appropriate staging launcher (desktop:
`tools/launch-desktop.ps1`; headless: `tools/keep-alive-host.ps1`). Do not blindly
use `-Reload` against an old host or terminate active user runs. Verify the
new `/identity` build and loaded plugin builds, then smoke-test a two-edit
batch on a temporary CRLF file and a guarded replace. A later frontend-only
iteration may use the normal Web plugin reload workflow.

Done means: the acceptance tests pass, the UI recognizes replace, the docs
describe the actual guarantees, and verification/deployment status is stated
accurately. Do not push without the user's instruction. Commit only completed,
verified work under the repository's existing discipline.
