# Plan: profiles (a chat's prompt, tools and model as one named choice), 2026-09-24

Status: proposed; waiting for the user's answers to the questions at the end.

## What the user asked for

"Profiles" that change the system prompt, the tools and perhaps more; a default profile per project, so a new chat in
that project starts with it; changing the profile of a chat is allowed but pays for it with a re-read of the chat
(free before the first message).

## Design

A profile is a named template that is **applied to a chat**: its instructions go into the chat's system prompt, its
tool switches become the chat's tool switches, and it can set the model and reasoning effort. The chat remembers which
profile it has (`meta.profile`); the tools button and the model picker still adjust the chat afterwards.

```jsonc
"profiles": {
  "defaultProfile": "coder",            // new chats without a project default
  "coder":  { "name": "Coder", "description": "Writes and runs code." },
  "review": { "name": "Reviewer", "description": "Reads and reports; changes nothing.",
              "instructions": "Review the change you are given. Do not edit files; report findings with file:line.",
              "toolsOff": ["write", "edit", "bash", "pwsh", "ssh_*"], "reasoning": "high" },
  "chat":   { "name": "Plain chat", "identity": "You are a helpful assistant.", "toolsOff": ["*"],
              "model": "openrouter/stealth/space-bunny-alpha" }
}
```

- **Fields:**
  - `name` and `description`: shown in the pickers, and to agents if they may choose profiles for subagents.
  - `instructions`: a "# Profile" section near the end of the system prompt (before `context.appendPrompt`, which
    still applies to every chat).
  - `identity` (optional): replaces the opening section, like `context.customPrompt` does globally.
  - `toolsOff`: tool names or globs (`ssh_*`, `*`).
  - `model`, `reasoning` (optional).
- **Which profile a new chat gets:** the one chosen on the start screen, else its project's default
  (`project.meta.profile`), else `profiles.defaultProfile`, else none (today's behaviour). It is applied when the chat
  is created, and at the latest before its first model call, so a quick first message cannot miss it.
- **Changing it:**
  - Before the first message: free. The chat's tools, model and effort are replaced by the new profile's.
  - In a started chat: the same, plus the system prompt is rendered again from the new profile at the next model call.
    That is the one full re-read, and the picker says so with the chat's size, as the tools menu does. A `profile`
    notice tells the model ("The user switched this chat to the profile Reviewer."), and the tools baseline starts
    over, so no separate tools notice.
  - This becomes the third exception to "never rewrite what was sent", after compaction and tool-call repair, because
    the user asks for it.
- **Subagents:** they start with their parent chat's profile and tool switches, as now. `agent_spawn { profile }`
  starts one with another profile, for example a reviewer. The lane still decides the model when both are given.
- **A project's default profile:** a `meta` object on projects (additive: a column, `ProjectInfo.Meta`,
  `projects.update { meta }` merged key by key), set in the Projects dialog. The same place can later hold other
  per-project defaults.
- **Where it lives:** a small `NetPI.Profiles` plugin. It reads `profiles.*`, applies a profile to a session, provides
  the prompt section, and registers `profiles.list`, `profiles.apply { sessionId, profile }` and the settings section.
  The context plugin only learns to forget a chat's frozen prompt when asked (`profile.changed`), so it is rendered
  again.

## UI

- **Settings → Profiles:** one card per profile, like the lanes editor:
  - the name and description;
  - the instructions (and, under "Replace the identity", the identity text);
  - the tool switches as chips;
  - the model and the effort;
  - "Default for new chats".
  - "Add profile" starts from the current global prompt settings.
- **Start screen:** a profile chip next to the project chip.
- **Chat:** a profile picker next to the model picker. In a started chat it shows the re-read warning before
  switching.
- **Projects dialog:** "Default profile" per project.

## Tests

- **Agent and context suites:**
  - which profile a new chat gets (chosen > project > default > none);
  - the prompt sections;
  - the tools off by name and glob;
  - model and effort;
  - a switch before the first message (no re-read, no notice) and after it (the prompt rendered again, a `profile`
    notice, no stray tools notice);
  - `agent_spawn { profile }` and inheritance.
- **Host suite:** project meta (migration, merge).
- **UI mock:** the Profiles page, the start-screen chip, the chat picker and its warning, the Projects dialog select.

## Questions

1. Beyond prompt and tools: model and effort, and `agent_spawn { profile }` for subagents (both proposed)? Anything
   else now, such as AGENTS.md on/off or compaction and goal settings per profile?
2. The prompt: `instructions` added near the end plus an optional `identity` replacement (proposed), or should a
   profile be able to replace the whole system prompt?
3. A switch in a started chat: render the system prompt again (proposed: clean, one full re-read), or only append the
   new instructions as a notice (cheaper, but the old profile's instructions stay in the prompt)?
