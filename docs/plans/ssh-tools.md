# Plan: SSH tools (`plugins/NetPI.Tools.Ssh`), 2026-09-24

Status: proposed, waiting for answers to the open questions at the end.

## Problem

Agents do remote work through `bash`/`pwsh` with `ssh host "…"`. Scripts and file contents then travel as command-line
arguments through two shells (the local one and the remote one): quotes, `$`, backslashes, newlines and CRLF break,
long scripts hit argument limits, and heredocs through Git Bash are fragile. The user has passwordless keys in
`~/.ssh` and wants agents to use them.

## Approach

- **Drive the system OpenSSH client** (`ssh`, `scp`) as a subprocess. No NuGet (SSH.NET would need one), and the user's
  `~/.ssh/config`, keys, `known_hosts`, `ProxyJump` and agent apply as they are. Default client: Windows OpenSSH
  (`C:\Windows\System32\OpenSSH\ssh.exe`, 9.5); `ssh.path` can pick another (Git's OpenSSH 10.3 also supports connection
  multiplexing).
- **Payloads go through stdin, never through arguments.** The remote command line is fixed and tiny (`bash -s`,
  `cat > <path>`); the script or file content is written to ssh's stdin. User content is never quoted, and without a
  pty the channel is binary-safe.
- **Never prompt:** `BatchMode=yes`, `ConnectTimeout=10`, `ServerAliveInterval=15`. A missing key, an unknown host key or
  a password prompt fails fast with ssh's own message.

## Tools (phase 1)

| tool | args | does |
|---|---|---|
| `ssh_hosts` (read-only) | – | the hosts agents may use: aliases from `~/.ssh/config` filtered by `ssh.hosts`, with user and hostname; OS and shells once probed |
| `ssh_run` | `host, script, cwd?, timeout? (120, max 1800)` | runs the script with bash on the host, script on stdin (CRLF → LF). Live output in the UI, exit code, head + tail truncation like `bash`. The remote side runs in its own process group (`setsid` + `timeout`), so a timeout or an abort (Esc) ends the whole remote process tree, not only the local ssh |
| `ssh_write` | `host, path, content, append?` | writes a remote file: content on stdin, written to a temp file and moved into place, parent folders created |
| `ssh_read` | `host, path, offset?, limit?` | a remote text file with line numbers, like `read`; binary files are refused with their size |
| `ssh_edit` | `host, path, edits: [{ oldText, newText }]` | exact replacements like `edit` (each must match once): read, apply locally, write back; a diff for the UI |
| `ssh_copy` | `host, from, to, direction: upload \| download, recursive?` | `scp` for large or binary files and folders |

Relative remote paths resolve against `cwd`, else the remote home. Calls are stateless (one ssh connection each,
roughly 0.2–0.5 s on the LAN); connection reuse comes later if that turns out to matter.

## Hosts and safety

- **Hosts:** by default every concrete `Host` alias in `~/.ssh/config` (wildcards skipped): today `server`, `nuc`,
  `benchq`, `bencho`, `benchg`. `ssh.hosts` narrows the list (aliases or globs). The tools refuse other hosts. This is a
  guardrail, not a sandbox, since agents also have `bash`: the same light-touch stance as the web tools.
- **Host keys:** unknown host keys fail; `ssh.acceptNewHostKeys` (default false) adds `StrictHostKeyChecking=accept-new`.
- **Keys and passwords:** the plugin never reads key files and never uses password authentication.
- **Cache:** new tools reach running sessions with a "tools" notice, like any tool plugin.

## UI

- `ssh_run` renders like `bash`: host badge, script preview, live output, exit code.
- `ssh_write` renders like `write`; `ssh_edit` reuses the diff view (same `details` shape as `edit`).

## Tests

- **Unit:** a fake `ssh` executable (set through `ssh.path`) records its arguments and echoes stdin. Checks:
  - argument construction (BatchMode, host, the fixed remote command)
  - byte-exact payloads: quotes, `$`, backslashes, CRLF → LF, Unicode, a 1 MB script
  - exit codes, truncation, timeout and abort
  - host filtering and `~/.ssh/config` parsing
- **Live smoke on `nuc`** (with the user's OK), under `/tmp/netpi-ssh-test`, cleaned up afterwards:
  - a script full of quotes, heredocs and `$`
  - write, read and edit a file
  - copy a file both ways
  - a timeout that must kill the remote process tree

## Later

- Background remote jobs (`nohup` + a job log, with output and kill tools).
- Windows hosts (`pwsh -Command -` over ssh).
- Connection reuse.
- Remote workspaces: a project whose folder is `ssh://nuc/home/quazzie/proj`, so `read`/`write`/`edit`/`grep`/`bash`
  run remotely. That needs an ssh transport for the file and shell tools, a bigger change.

## Open questions

1. Which hosts: all five aliases, or only `server` and `nuc` (the `bench*` accounts look like benchmark users)?
2. Is the phase 1 tool set right, with background jobs later?
3. Should new host keys be accepted automatically? (Proposed: no.)
4. Is the live smoke test on `nuc` under `/tmp` OK?
