#Requires -Version 7.0
<#
.SYNOPSIS
  One check on the checkout a build runs from, shared by build.ps1 and scripts\test.ps1.

.DESCRIPTION
  Dot-source this and call Write-StaleCheckoutWarning before writing anything. It is silent when there is nothing
  to say, and it only speaks up in the one place where a commit is dangerous: the main checkout, the tree that has
  the repository's own .git and therefore has a branch checked out that every worktree merges into.
#>

function Write-StaleCheckoutWarning([string] $RepoRoot) {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return }   # no git: nothing to check
    if (-not $RepoRoot) { $RepoRoot = (Get-Location).Path }

    # No param block: the automatic $Args carries every argument (a declared [string[]] $Args swallows the first one,
    # which made every rev-parse below come back empty and the check a no-op).
    $git = { & git -C $RepoRoot $Args 2>$null }
    try {
        # A linked worktree's git dir is <main>/.git/worktrees/<name>; the main checkout's is the .git itself, whose
        # parent is the toplevel. git prints forward slashes, so normalize (and compare case-insensitively) before testing.
        $gitDir = (& $git rev-parse --absolute-git-dir)
        $top = (& $git rev-parse --show-toplevel)
        if (-not $gitDir -or -not $top) { return }                      # not a repository
        $gitDirParent = (Split-Path -Parent $gitDir) -replace '\\', '/'
        if ($gitDirParent -ine ($top -replace '\\', '/')) { return }    # a worktree: staging is how you commit
        # --quiet prints nothing: the staged-changes test is the exit code (0 = the index is HEAD, 1 = something is staged).
        & $git diff --cached --quiet
        if ($LASTEXITCODE -ne 0) {
            $staged = @(& $git diff --cached --name-only).Count
            Write-Host ''
            Write-Host "This checkout's index is not HEAD: $staged file(s) are staged here." -ForegroundColor Yellow
            Write-Host "  A 'git commit' here would commit those, not HEAD - they are usually the reverse of commits" -ForegroundColor Yellow
            Write-Host "  that landed since (a branch moved from another worktree does not update this tree)." -ForegroundColor Yellow
            Write-Host "  Move the branch with:  git -C `"$RepoRoot`" merge --ff-only <branch>" -ForegroundColor Yellow
            Write-Host "  (it moves ref, index and tree together, and refuses while the tree has changes)." -ForegroundColor Yellow
            Write-Host ''
        }
    } catch {
        # A repository in an odd state must not stop a build; the warning is advice, not a gate.
    }
}