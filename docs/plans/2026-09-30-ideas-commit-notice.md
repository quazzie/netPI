# Ideas: the agent closes the idea its own commit finished

Status: implemented (idea-lk4gm7). Date: 2026-09-30. Source baseline: d7f06c0.

## The gap

`IdeaCommitCheck` (phase 3 of `docs/plans/2026-09-27-ideas-follow-the-session.md`) watches every project's repository and,
for each new commit, asks two model questions: which open idea is it about, and is that idea finished. The answer is a
**card**, and "the card is an offer, never an action: nothing is marked done without a click". That is the right shape for
a commit made in a terminal — no conversation is watching it, so somebody has to be asked, and the user is the one who
decides.

A commit the agent made itself is a different case, and the expensive one: the run that wrote the commit is right there,
it knows what the commit was for, and it is usually still going. So the question is already answered in the transcript,
and the only thing missing is being asked to act on it. Until now, closing that idea meant the user reading a card and
clicking, minutes later, on work the agent had already done.

## What landed

`plugins/NetPI.Ideas/IdeaCommitNotice.cs` — an `IAgentHook` (order 260, after the nudge) that watches the run's own tool
calls. A `bash`/`pwsh` call whose command runs `commit` or `merge` and that succeeded, inside the session's project,
with at least one open idea in that project, sets a pending flag; the next `OnAfterModelCallAsync` turns it into one
`TurnDecision.Inject` notice (kind `git-commit`) naming the project's open ideas and asking the agent to mark the one this
commit finished — or to say in one line that none of them is about it.

It is advice, not an action, the same way a nudge is: the agent calls `ideas update` itself and the user sees the result.
The existing card flow is untouched and still needed — a commit the agent closed is no longer open, so it is not offered
twice, and a commit nobody made in a chat is exactly what the watcher exists for.

## Why these bounds

The notice costs one extra model call in the run that committed, on a model that has two slots, so every reason to stay
silent is taken:

| Bound | Why |
|---|---|
| the run has a project and the `ideas` tool | without either there is nothing to update, and ideas are stamped with a project |
| the working directory is inside the project | a commit in a scratch directory or another repository is not this project's work |
| exit code 0, not an error, not a JSON null | a failed commit landed nothing; a null exit code is a background command still running |
| no `--dry-run`, `--abort`, `--quit`, `--no-commit` | the same words without the effect |
| at least one open idea in the project | a project without a backlog never pays for the call |
| at most `ideas.commitNoticesPerRun` (2) per run | a run that commits in a loop is asked a bounded number of times |
| two commits in one model call are one notice | the model call is what costs, not the commit |

The notice names at most 8 titles, each clipped to 90 characters: the agent gets the backlog's shape without a tool call,
and the notice stays under ~1.4 kB.

## Detection, honestly

The commit is recognised from the tool call and its result, not from a git query: the run already ran git, and its result
is in the transcript. So a commit made by anything other than a shell tool call inside the run (a background process the
run did not wait for, or a commit made in a terminal) is not seen here — that is the watcher's job, and the two do not
overlap. `IsCommitCommand` reads the command as tokens, so `git -C dir commit` and `git add -A; git commit -m x` are
seen while `git log`, `git push` and `git commit --dry-run` are not; the cases are in
`tests/NetPI.Aux.Tests/IdeasNoticeTests.cs`.

## Not done

- A commit made in a terminal does not reach the conversation that would have closed the idea. Doing that well means
  noticing the commit (the watcher already does) and steering a *different*, possibly idle session, which is a larger
  decision than this idea asked for.
- The `commits` entry on the idea is still recorded by the sweep, not by the agent: the agent is asked to set the status
  and leave a short section, not to write history.
