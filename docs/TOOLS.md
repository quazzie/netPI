# NetPI tool plugins

Two plugins provide the built-in agent tools:

| plugin | id | tools | RPC |
|---|---|---|---|
| `plugins/NetPI.Tools.Files` | `netpi.tools.files` | `read` `write` `edit` `grep` `find` `ls` | `files.search`, `files.list` |
| `plugins/NetPI.Tools.Shell` | `netpi.tools.shell` | `bash` `pwsh` `process_list` `process_output` `process_kill` | `processes.list`, `processes.output`, `processes.kill` |

Both start at `Order = 20`. Tests live in `tests/NetPI.Tools.Tests`, a console app with no test framework:

```sh
dotnet build plugins/NetPI.Tools.Files/NetPI.Tools.Files.csproj -p:BuildProjectReferences=false
dotnet build plugins/NetPI.Tools.Shell/NetPI.Tools.Shell.csproj -p:BuildProjectReferences=false
dotnet build tests/NetPI.Tools.Tests/NetPI.Tools.Tests.csproj -p:BuildProjectReferences=false
dotnet tests/NetPI.Tools.Tests/bin/Debug/NetPI.Tools.Tests.dll [name-filter…]   # exit code 0 = all passed
```

## Conventions

- **Lenient arguments.** Names are matched ignoring case, `_` and `-` (`file_path` = `filePath` = `FilePath`), and common
  aliases are accepted (`file_path`/`file`/`filename` for `path`, `old_string`/`new_string` for `oldText`/`newText`, and so on).
  Numbers and booleans may be strings (`"30"`, `"true"`). An arguments object sent as a JSON string is unwrapped.
- **Errors are results, never exceptions.** `isError: true` with a message that says what to do next (“Use ls…”,
  “Did you mean…”, “Include more context…”). Cancellation is the one exception: when the call's token is cancelled, file
  tools rethrow `OperationCanceledException`. Shell tools kill the process tree and return an `[aborted]` result.
- **Paths** go through `ToolContext.ResolvePath`: relative to the session cwd, `~`, `/c/...` Git Bash paths on Windows, and both
  `/` and `\`. Output paths are relative to the cwd with `/` separators, or absolute when outside the cwd.
- **Line endings.** Every text tool works on LF-normalized text. On write, a file keeps its original style: CRLF when a strict
  majority of its line breaks are CRLF, else LF. Its UTF-8/UTF-16 BOM and encoding are kept too. A file that is not valid
  UTF-8 is read as Latin-1, so unchanged bytes round-trip exactly. A file whose first 8KB contain a NUL byte is binary: it is
  never text-edited and grep skips it.
- **Ignore rules** (used by grep, find, ls, `files.*`):
  - Built-in skipped directories: `.git node_modules bin obj .vs .idea dist build artifacts __pycache__ .venv .hg .svn`.
  - `.gitignore` and `.ignore` files: from the enclosing git repository root down to each directory, including nested ones.
    Supported syntax: `#` comments, `!` negation, trailing-`/` (directory only), anchored (`/x`, `a/b`) and basename
    patterns, `*`, `**`, `?` and `[…]`.
  - The directory you explicitly target is never skipped itself. Hidden files (dotfiles) are included, and directory
    symlinks/junctions are listed but not followed.
- **Globs** (`find` pattern, grep `glob`): `*`, `**`, `?`, `{a,b}` (nestable), `[abc]`/`[!a-z]`, and a leading `!` to
  negate. A pattern without `/` matches the file name at any depth. Globs are case-insensitive on Windows and macOS.

## Settings

| key | default | meaning |
|---|---|---|
| `files.newFileEol` | `"lf"` | EOL for **new** files: `"lf"`, `"crlf"` or `"auto"` (CRLF on Windows) |
| `shell.bashPath` | – | Explicit bash executable. Otherwise, on Windows (Git Bash only, **never** `System32\bash.exe`/WSL): `%ProgramFiles%\Git\bin\bash.exe`, `%ProgramFiles(x86)%\Git\bin\bash.exe`, `%LocalAppData%\Programs\Git\bin\bash.exe`, derived from `git.exe` on PATH (`…\Git\cmd\git.exe` → `…\Git\bin\bash.exe`), Scoop. Elsewhere: `/bin/bash`, PATH, `/bin/sh` |
| `shell.pwshPath` | – | Explicit PowerShell executable. Otherwise: `pwsh` on PATH, `%ProgramFiles%\PowerShell\7\pwsh.exe` (plus the usual Unix locations), and on Windows as a last resort Windows PowerShell 5.1 (`powershell.exe`) |
| `shell.timeoutSeconds` | `120` | Default foreground timeout (max 1800) |

`files.useRipgrep` is **not implemented**: grep always uses the built-in parallel C# engine.

---

## File tools (`category: "files"`)

### `read` (read-only, summary arg `path`)

`{ path, offset?, limit? }`

- Returns the text LF-normalized, **without** line-number prefixes. At most **2000 lines / 50KB** are returned per call
  (`limit` is capped at 2000). `offset` is 1-based; a negative offset counts from the end (`-100` = last 100 lines).
- When the output is cut, it ends with a footer: `[Showing lines 1-2000 of 5230. Use offset=2001 to continue.]`.
- If a single line is longer than 50KB (minified files), the start of that line is shown with a note.
- Images (`png jpg jpeg gif webp`, ≤ 20MB) come back as an `ImagePart` when `context.Model.SupportsImages`. Otherwise the result
  is a text note. A directory gives an error that suggests `ls`, and a missing file gives an error with “Did you mean” names.
  Files over 32MB are streamed instead of loaded.

```ts
details: { path: string /* absolute */, startLine: number, endLine: number, totalLines: number,
           truncated: boolean, eol: 'lf'|'crlf', bom: boolean, encoding?: 'latin1' }
// image:  { path, image: true, mediaType, bytes }
```

### `write` (summary arg `path`)

`{ path, content }`

- Creates parent directories.
- **Existing file:** keeps its EOL style, BOM and encoding. The content's line endings are converted to match.
- **New file:** uses the `files.newFileEol` setting.
- Writes atomically: a temp file in the same directory, then a rename. Symlinks are written through, the Unix file mode
  (for example `+x`) is preserved, and a read-only file is an error. When the rename is refused (the file is locked, or is
  hidden/system on Windows), the file is written in place.
- Content identical to the file gives `No changes: …`.

```ts
details: { path, created: boolean, bytes: number, lines: number, eol: 'lf'|'crlf', bom: boolean,
           diff?: string /* unified diff vs. previous content, existing text files only, ≤ 2000 lines */,
           added?: number, removed?: number }
```

### `edit` (summary arg `path`)

`{ path, edits: [{ oldText, newText, replaceAll? }] }`, or the single-edit shorthand `{ path, oldText, newText, replaceAll? }`
(aliases: `old_string`/`new_string`, `replace_all`; `edits` may also be a JSON string).

- Edits run in order in memory (a later edit sees the result of earlier ones) and are **all-or-nothing**. If any edit fails,
  nothing is written, and the error names the edit that failed: `Edit 2 of 3 failed in src/x.cs: …`.
- Matching runs on LF-normalized text: CRLF/LF differences between the file and oldText/newText never matter.
  1. Exact substring match.
  2. Fallback ladder, comparing whole lines:
     1. **trailing-whitespace**: trailing whitespace is ignored.
     2. **unicode**: additionally, smart quotes become ASCII, en/em dashes and minus become `-`, NBSP and other special spaces
        become a space, `…` becomes `...`, and zero-width characters are dropped.
     3. **indentation**: additionally, leading indentation is ignored. newText is re-indented to the file's indentation:
        indent levels are learned from the matched lines, and spaces and tabs are converted to the file's style.
- If oldText matches more than once and `replaceAll` is not set, the result is an error with the match count and the line
  numbers: `oldText matches 2 locations at lines 12, 48. Include more surrounding context…`.
- An empty oldText is an error, except on an empty file, where the edit acts like `write`.
- oldText equal to newText is an error. A binary file, a missing file (the error suggests `write`) or a file over 64MB is
  an error.
- When nothing is found, the error points at the most similar line.
- Model-facing content: `Applied 2 edits to src/x.cs (+5 −3)`, followed by notes about fuzzy matches or mixed line endings,
  then a compact unified diff (hunks only, 3 lines of context, at most 80 lines).

```ts
details: { path, diff: string /* full unified diff with ---/+++ header, ≤ 2000 lines */, added: number, removed: number,
           edits: number, firstChangedLine: number, eol: 'lf'|'crlf', bom: boolean,
           fuzzy?: { edit: number /* 1-based */, strategy: 'trailing-whitespace'|'unicode'|'indentation', line: number }[] }
// failure: { path, failedEdit: number, edits: number }
```

Diffs are standard unified diffs (`@@ -a,b +c,d @@`, ` `/`-`/`+` lines, `\ No newline at end of file`). The UI diff viewer can
parse `details.diff` for both `edit` and `write`.

### `grep` (read-only, summary arg `pattern`)

`{ pattern, path?, glob?, ignoreCase?, literal?, context?, maxResults? (200), outputMode?: 'content'|'files'|'count', multiline? }`

- Uses .NET regular expressions with a 2s timeout per file, and searches files in parallel. It matches against LF-normalized
  text, so `$` matches at every line end in CRLF files too.
- Without `multiline`, each line is matched separately. With `multiline`, the pattern can span lines (`\n`), and `(?s)` makes
  `.` match newlines.
- An invalid regex is searched literally, with a note. `path` may be a file, a directory, or a glob such as
  `src/**/*.cs`. `glob` takes one or more globs, separated by spaces, or an array; `!` excludes.
- `context` (aliases `-C`, `-A`, `-B`) is capped at 50.
- Output modes:
  - **content**: `rel/path:12: text` for matches and `rel/path-13- text` for context lines. `--` separates groups when
    `context > 0`. Lines longer than 500 characters are clipped around the match.
  - **files**: one path per line.
  - **count**: `rel/path: N`.
- The output is capped at `maxResults` (matching lines in content mode, files in the other modes) and 50KB, with the notice
  `[Results truncated at 200 matches. …]`.
- Binary files and files over 32MB are skipped. That is only reported when it could explain a missing result.

```ts
details: { pattern, path /* absolute search root */, outputMode, matches: number, files: number,
           filesSearched: number, truncated: boolean }
```

### `find` (read-only, summary arg `pattern`)

`{ pattern, path?, maxResults? (1000) }`

- Glob search. Results are sorted by path (depth-first) and relative to the cwd, and directories end with `/`.
- A literal prefix of the pattern (`src/**/*.cs`) or an absolute pattern (`C:/proj/**/*.cs`) becomes the walk root.

```ts
details: { pattern, path, count: number, truncated: boolean }
```

### `ls` (read-only, summary arg `path`)

`{ path?, all? }`

- Directories come first, with a trailing `/`, then files with their size.
- Ignored entries (gitignored files and built-in skipped directories) are hidden and counted, unless `all=true`.
- At most 1000 entries are listed.

```ts
details: { path, entries: number, dirs: number, files: number, hidden: number, truncated: boolean }
```

### RPC

| method | params | result |
|---|---|---|
| `files.search` | `{ sessionId?, cwd?, query, limit? (50, max 500) }` | `{ path /* absolute */, rel, isDir }[]`: fuzzy file-name ranking for `@` mentions. Substring in the file name beats substring in the path, which beats a subsequence; shorter paths rank first. An empty query returns shallow entries. The file list per root is cached for 10s (up to 50k entries) |
| `files.list` | `{ sessionId?, cwd?, dir? /* relative to root */ }` | `{ root, dir /* '' for root */, entries: { name, rel, isDir, size?, mtime? /* ISO */, ignored?: true }[] }`: one directory, directories first. `.git` is omitted, and ignored entries are included with `ignored: true` so the tree can dim them |

The root is chosen in this order: `cwd`, then the session's cwd (`ISessionStore.GetCwd`), then `Paths.DefaultWorkspace`.

---

## Shell tools (`category: "shell"`)

### `bash` / `pwsh` (summary arg `command`)

`{ command, timeout? (seconds), background?: boolean, cwd? }`

- **bash**
  - Windows: Git Bash, never WSL. The command is passed in the `NETPI_COMMAND` environment variable and run with
    `bash -c 'exec 2>&1; …; eval "$__netpi_cmd"'`. The MSYS runtime re-parses Windows command lines (it collapses `\\`
    inside quotes and globs), so the command cannot safely go on the command line.
  - Unix: `bash -c 'exec 2>&1; <command>'`. The prefix sits on the same line as the command, so line numbers in error
    messages do not change.
  - Commands over 30,000 characters go through a temp script.
  - stderr is merged into stdout at the source, so the order is exact.
  - Environment: `CHERE_INVOKING=1`, `TERM=dumb`, `NO_COLOR=1`, `GIT_PAGER=cat`, `PAGER=cat`, `GIT_TERMINAL_PROMPT=0` and
    `NETPI=1`. `LANG`/`LC_ALL` are set to `C.UTF-8` (or `en_US.UTF-8` on macOS), and `PYTHONIOENCODING=utf-8` and
    `PYTHONUNBUFFERED=1` are set, but only when they are not already set.
- **pwsh**
  - Runs `pwsh -NoLogo -NoProfile -NonInteractive [-ExecutionPolicy Bypass] -EncodedCommand <base64 UTF-16LE>`. Long
    scripts go through a temp `.ps1` file.
  - The script is prefixed with `$ProgressPreference='SilentlyContinue'`, UTF-8 input/output encoding and
    `$PSStyle.OutputRendering='PlainText'`. A suffix turns a failing last statement into a non-zero exit code (the native
    `$LASTEXITCODE`, or 1).
  - If PowerShell is not installed, the tool is still registered and returns a clear error.
- stdin is closed. Output is decoded as UTF-8, ANSI/VT escape codes are stripped, and live output goes to
  `ToolContext.Output` in batches of about 50ms.
- Timeout: `timeout` is in seconds. Values over 3600 are read as milliseconds, and `timeout_ms` is also accepted. The default
  comes from `shell.timeoutSeconds`, and the maximum is 1800. On timeout or cancellation the **whole process tree** is killed
  (`Process.Kill(entireProcessTree: true)`). If an orphaned grandchild keeps the pipes open, the call still returns about
  0.75s after the shell exits.
- Model-facing output:
  - Carriage-return progress lines are collapsed.
  - Only the **last 2000 lines / 30KB** are returned. When the output is cut, it starts with
    `[Output truncated: showing the last N lines of M. Full output saved to <tmp>/netpi/bash-proc_….log …]`.
  - Output beyond 1MB is streamed to that file while the command runs.
- Notes appended after the output:
  - `[exit code N]` for a non-zero exit. This is **not** `isError`.
  - `[timed out after Ns; the process tree was killed…]` or `[aborted…]`. Both set `isError: true`.
  - A spawn failure is also `isError: true`.
- **Background** (`background: true`): starts and returns within about 0.5s, with the id `proc_…`. If the command exits
  within that window, its output and exit code are reported instead. Background processes have no timeout unless
  `timeout` is given.

```ts
details: { command, shell: 'bash'|'pwsh', cwd, exitCode?: number /* absent while running */, durationMs?: number,
           truncated: boolean, fullOutputPath?: string, background: boolean, processId: string, pid: number,
           status: 'running'|'exited'|'killed'|'timeout', timedOut?: true, aborted?: true }
// spawn / "shell not found" errors: { command, shell, cwd, background }
```

### Process registry

The registry records every run, foreground and background. It keeps the running processes plus the last 50 finished ones.
Each process has a ring buffer holding the most recent ~1MB of output; finished foreground runs keep 256KB. All running
processes are killed when the plugin stops.

| tool | args | details |
|---|---|---|
| `process_list` (read-only) | `{}` | `{ processes: ProcessInfo[] }` |
| `process_output` (read-only) | `{ id, tail? (200, max 2000) }` (the id may also be a pid) | `{ process: ProcessInfo, tail, truncated }` |
| `process_kill` | `{ id }` | `{ process: ProcessInfo, killed: boolean }` |

```ts
interface ProcessInfo { id; pid; shell: 'bash'|'pwsh'; command; cwd; sessionId?; agentId?; background: boolean;
  status: 'running'|'exited'|'killed'|'timeout'; exitCode?; startedAt; endedAt?; outputBytes }
```

RPC: `processes.list` → `ProcessInfo[]` (running first, newest first) · `processes.output { id, tail? (500) }` → `string` ·
`processes.kill { id }` → `bool` (false if it was not running). An unknown id gives `not_found`.

Events (broadcast):

| type | data |
|---|---|
| `process.started` | `{ process: ProcessInfo }` (every run) |
| `process.exited` | `{ process: ProcessInfo }` (every run) |
| `process.output` | `{ id, chunk }`: live output of **background** processes only, batched about every 250ms. Foreground output streams through `tool.output` |
