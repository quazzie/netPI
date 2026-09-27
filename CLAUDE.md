@AGENTS.md

## Current state

Read `docs/HANDOFF.md` first: where things are, what has been verified, and the next steps. Open issues are in `docs/STATUS.md`.

## Working with the user

- The Responses transport stays standard and stateless. Don't add workarounds that hide backend bugs; surface errors with their ids instead.
- Side panels are narrow (about 230–320px): design every tab for that width.
- Screenshots the user shares are weak references, not specs.
- Ask before stopping the user's running NetPI, AiSwitcher or nInfer, and before touching `%USERPROFILE%\.netpi`.
- Keep dependencies minimal. Ask before adding a NuGet or npm package.
- When a discussion ends in a plan or feature that isn't built yet, offer to save it as an idea
  (`node scripts/netpi.mjs ideas.add '<json>' --write`, or add to a matching open idea with `ideas.update`).
  Offer, don't add: the user keeps the backlog small and drops weak ideas. Short, specific titles.
