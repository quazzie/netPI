# Skills plugin (`netpi.skills`)

`plugins/NetPI.Skills` implements [Agent Skills](https://agentskills.io/specification): a skill is a folder with a
`SKILL.md` (YAML frontmatter with `name` and `description`, then instructions in Markdown) and whatever files it bundles
(`scripts/`, `references/`, `assets/`, …). Agents see which skills exist and load one when a task matches its
description (progressive disclosure: the catalog costs ~50–100 tokens per skill, the instructions only when used).

## Where skills are found

For a working directory, first found wins:

| Scope | Folders |
|---|---|
| project | `.netpi/skills` and `.agents/skills` in the working directory and every parent up to the git root (without a git repository, up to the file system root); your home folder is skipped, its `.agents/skills` is the global one |
| extra | `skills.paths`: folders of skills, or one skill's own folder (`~` is your home; relative paths resolve against the working directory) |
| global | `skills` in the NetPI home (`~/.netpi/skills`), `~/.agents/skills` |

`.agents/skills` is the cross-client convention, so skills installed for other agents work here too. With
`skills.claudeCode` also `.claude/skills` in the project and `~/.claude/skills` (off by default: those skills may refer
to Claude Code's own tools).

Inside a skills folder every subfolder with a `SKILL.md` is a skill (its own subfolders are its resources); folders
without one are grouping folders and are searched too (5 levels, 2000 folders at most; names starting with `.` and
`node_modules` are skipped). When two skills have the same name the first one found is used and the other is reported.
Project skills load like project AGENTS.md files: a repository you open can give its agents instructions.

## SKILL.md

Read leniently, as the standard's client guide advises, and without a YAML library: plain, quoted and block (`|`, `>`)
values, lists, and one nested map (`metadata`). A plain value may contain colons (`description: Use when: …`), which
strict YAML rejects. Problems are reported (`skills.list`, the Diagnostics Context view, the project dialog, the log):

- **skipped** (error): no frontmatter, or no `description`
- **loaded anyway** (warning): no `name` (the folder name is used), a name that isn't lowercase letters, digits and
  single hyphens, longer than 64 characters or different from its folder; a description over 1024 characters,
  `compatibility` over 500; a name another skill already has (this one is not used)

Fields used: `name`, `description`, `disable-model-invocation: true` (a skill only you load, with `/skill:name`; agents
don't see it), and `license`, `compatibility`, `allowed-tools` (shown in `skills.list`). `allowed-tools` is the
standard's experimental list of tools a skill may use without asking (e.g. `Bash(git:*) Read`); NetPI has no tool
approvals to skip, so it does nothing here, and it doesn't limit the tools either. Other fields are ignored. Parsed files
are cached by path, time and size.

## What agents get

The `skill` tool is the switch: it is a tool like any other (the chat's tools button, `tools.disabled`, a profile's or a
subagent's tool list), and a chat without it gets no catalog.

- **The catalog** in a `skills` notice before the first model call that has the tool (never in the system prompt, which
  is frozen per session; see [PLUGINS.md](PLUGINS.md), "Never rewrite what was sent"):
  ```
  Skills: instructions for specific tasks. When a task matches a skill's description, load it with the skill tool before
  you start and follow it.

  <available_skills>
    <skill>
      <name>release-notes</name>
      <description>Write release notes from the git log since the last tag. Use when preparing a release.</description>
    </skill>
  </available_skills>
  ```
  No locations: the tool returns the skill's folder. Afterwards only changes are appended ("The skills changed. New or
  changed: … No longer available: …"), e.g. after a project switch or when a skill is added, renamed or its description
  edited. What the model has is read from the notices still in the context, so after compaction the catalog is announced
  again, naming the skills it had loaded before ("Load a skill again if you still need its instructions"). No skills: no
  notice. A tool switched on later brings the catalog with it; switched off, the tools notice says so. Meta:
  `skills: [{ name, hash, path }]`, `removed: [name]`.
- **The `skill` tool** loads one (see [TOOLS.md](TOOLS.md)). Its guideline in the "# Tools" section: when a task matches
  a skill's description, load it before you start and follow it. Like every tool definition it is part of every request
  (about 165 tokens with the guideline); the backend's prompt cache keeps it, so it is processed once per chat.
- **`/skill:name …`** at the start of your message: the message is sent as you typed it and a `skill` notice with that
  skill's instructions follows it ("The user loaded the skill "x" for their message: follow its instructions."), also
  for a message that steers a running agent. This is you asking, so it works with the skill tool switched off, and for
  user-only skills. An unknown or switched-off (`skills.disabled`) name gets a notice
  saying so, with the skills there are. Meta: `skill`, `hash`, `path`, `for` (the message id), `missing`.

A loaded skill, from the tool or from `/skill:`, reads:

```
<skill_content name="release-notes">
…the SKILL.md body, without its frontmatter…

Skill directory: C:\Users\me\.agents\skills\release-notes
Relative paths in this skill resolve against it.
<skill_resources>
  <file>scripts/changelog.sh</file>
</skill_resources>
</skill_content>
```

The bundled files are listed (at most 50), not read. A body over 16000 characters is cut at a line with where to read on
("SKILL.md continues: read … from line N"), which keeps the result under the runtime's 20000-character limit for a tool
result.

## Settings

| Key | Default | |
|---|---|---|
| `skills.paths` | `[]` | more skill folders, or one skill's folder |
| `skills.claudeCode` | `false` | also `.claude/skills` (project) and `~/.claude/skills` |
| `skills.disabled` | `[]` | skill names switched off: agents don't see them, the tool and `/skill:` refuse them |

They apply from the next model call (the catalog notice announces the difference).

## RPC

`skills.list { sessionId } | { projectId }` → `{ skills: [{ name, description, path, scope, listed, userOnly, disabled,
license?, compatibility?, allowedTools? }], problems: [{ path, level, message }] }`, in precedence order. `listed`: agents
see it (not user-only, not switched off).

## UI

- The composer's `/` popup lists the session's skills as `/skill:name` (from `skills.list`, cached for 10 s); choosing
  one inserts `/skill:name ` for you to add the task.
- The Diagnostics tab's Context view and the project dialog list the skills that apply, with their problems.
- `skills` and `skill` notices are collapsed rows in the chat ("Skills", "Skill: name"); the tool row shows the name.
