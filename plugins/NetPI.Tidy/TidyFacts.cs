namespace NetPI.Tidy;

/// <summary>
/// What a run left behind, read from git and the file system only (no model): the deterministic first step of the tidy
/// check. Every item is one line the notice can show as it is.
/// <para>
/// What counts depends on whose checkout it is. In the agent's <b>own worktree</b> (a linked git worktree) everything in
/// it is the agent's: uncommitted changes, commits its branch has that the base branch lacks, and a branch that is
/// already merged while the worktree is still there (the <c>workspace retire</c> it never asked for). In a <b>shared
/// checkout</b> (the project's main folder) other agents and the owner work too, so only the files <i>this run</i> wrote
/// with the file tools count, never somebody else's uncommitted work. Scratch counts in both: an entry of
/// <c>scratch/</c> that appeared during this run and is still there.
/// </para>
/// <para>
/// A git that cannot answer (missing, failing, timing out) yields nothing rather than a guess: a notice built on a wrong
/// "you left this" is worse than no notice.
/// </para>
/// </summary>
public static class TidyFacts
{
    /// <summary>Names of scratch entries shown in one notice; the rest is counted.</summary>
    public const int MaxNamed = 5;

    public sealed record Repo(string Top, bool Linked, string? Branch, string? BaseBranch);

    /// <summary>The repository a directory is in, or null when it is not one (or git could not tell).</summary>
    public static async Task<Repo?> RepoOfAsync(string cwd, CancellationToken ct)
    {
        if (!Directory.Exists(cwd)) return null;
        var r = await GitRunner.RunAsync(cwd, ct, 0, "rev-parse", "--show-toplevel", "--absolute-git-dir", "--git-common-dir").ConfigureAwait(false);
        if (!Ok(r)) return null;
        var lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3) return null;
        var top = Norm(lines[0]);
        var gitDir = Norm(lines[1]);
        var common = Norm(Path.IsPathRooted(lines[2]) ? lines[2] : Path.Combine(cwd, lines[2]));
        var branch = await OutAsync(cwd, ct, "branch", "--show-current").ConfigureAwait(false);
        return new Repo(top, !Same(gitDir, common), string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(), await BaseBranchAsync(cwd, ct).ConfigureAwait(false));
    }

    /// <summary>The branch checked out in the main worktree (the first entry of <c>git worktree list</c>): the branch work is merged into.</summary>
    public static async Task<string?> BaseBranchAsync(string cwd, CancellationToken ct)
    {
        var list = await OutAsync(cwd, ct, "worktree", "list", "--porcelain").ConfigureAwait(false);
        if (list is null) return null;
        foreach (var line in list.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length == 0) break;                       // the first entry ends at the first blank line
            if (l.StartsWith("branch refs/heads/", StringComparison.Ordinal)) return l["branch refs/heads/".Length..];
        }
        return null;
    }

    /// <summary>
    /// The leftovers. <paramref name="written"/>: absolute paths this run wrote with the file tools.
    /// <paramref name="scratchBefore"/>: the scratch entries that existed when the run started (absolute paths), so only
    /// the ones it created count. <paramref name="retireRequested"/>: the agent already asked for its worktree to go.
    /// </summary>
    public static async Task<List<string>> GatherAsync(string cwd, IReadOnlyCollection<string> written,
        IReadOnlyCollection<string> scratchBefore, bool retireRequested, CancellationToken ct)
    {
        var items = new List<string>();
        var repo = await RepoOfAsync(cwd, ct).ConfigureAwait(false);
        if (repo is not null)
        {
            var status = await OutAsync(repo.Top, ct, "status", "--porcelain", "--untracked-files=all").ConfigureAwait(false);
            if (status is not null)
            {
                var dirty = StatusPaths(status).Where(p => !InScratch(p)).ToList();
                if (repo.Linked)
                {
                    if (dirty.Count > 0)
                        items.Add($"{Plural(dirty.Count, "uncommitted change")} in your worktree {repo.Top} ({Named(dirty)})");
                }
                else
                {
                    // a shared checkout: only what this run wrote is this run's business
                    var mine = dirty.Where(p => written.Any(w => Same(w, Path.Combine(repo.Top, p)))).ToList();
                    if (mine.Count > 0)
                        items.Add($"{Plural(mine.Count, "file")} you changed in {repo.Top} not committed ({Named(mine)})");
                }
            }

            if (repo.Linked && repo.Branch is { } branch && repo.BaseBranch is { } baseBranch && branch != baseBranch)
            {
                var ahead = await OutAsync(repo.Top, ct, "rev-list", "--count", $"{baseBranch}..{branch}").ConfigureAwait(false);
                if (int.TryParse(ahead?.Trim(), out var n))
                {
                    if (n > 0)
                        items.Add($"branch {branch} has {Plural(n, "commit")} not merged into {baseBranch}");
                    else if (!retireRequested && status is not null && StatusPaths(status).Count == 0)
                        items.Add($"branch {branch} is merged into {baseBranch} but its worktree {repo.Top} is still there " +
                                  "(`workspace retire`, or `git worktree remove` + `git branch -d` from the main checkout)");
                }
            }
        }

        var created = ScratchEntries(cwd, repo?.Top).Where(e => !scratchBefore.Any(b => Same(b, e))).ToList();
        if (created.Count > 0)
            items.Add($"scratch you created this run is still there: {Named(created.Select(c => Path.GetFileName(c)).ToList())}");
        return items;
    }

    /// <summary>The entries of <c>scratch/</c> under the run's folder and its repository root (absolute paths).</summary>
    public static List<string> ScratchEntries(string cwd, string? top)
    {
        var dirs = new[] { Path.Combine(cwd, "scratch"), top is null ? null : Path.Combine(top, "scratch") }
            .Where(d => d is not null && Directory.Exists(d)).Select(d => Norm(d!)).Distinct(StringComparer.OrdinalIgnoreCase);
        var entries = new List<string>();
        foreach (var d in dirs)
        {
            try { entries.AddRange(Directory.EnumerateFileSystemEntries(d).Select(Norm)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return entries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The paths of <c>git status --porcelain</c> lines (the target of a rename), relative to the repository root.</summary>
    public static List<string> StatusPaths(string porcelain) =>
        porcelain.Split('\n').Where(l => l.Length > 3).Select(l =>
        {
            var p = l[3..].TrimEnd('\r');
            var arrow = p.IndexOf(" -> ", StringComparison.Ordinal);
            return (arrow >= 0 ? p[(arrow + 4)..] : p).Trim('"');
        }).ToList();

    private static bool InScratch(string relative) =>
        relative.StartsWith("scratch/", StringComparison.OrdinalIgnoreCase) || relative.Equals("scratch", StringComparison.OrdinalIgnoreCase);

    private static string Named(List<string> names) =>
        string.Join(", ", names.Take(MaxNamed)) + (names.Count > MaxNamed ? $" and {names.Count - MaxNamed} more" : "");

    private static string Plural(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    private static bool Ok(GitRunner.Result r) => r.StartError is null && !r.TimedOut && !r.Aborted && r.ExitCode == 0;

    private static async Task<string?> OutAsync(string cwd, CancellationToken ct, params string[] args)
    {
        var r = await GitRunner.RunAsync(cwd, ct, 0, args).ConfigureAwait(false);
        return Ok(r) ? r.Stdout : null;
    }

    public static string Norm(string path)
    {
        try { return Path.GetFullPath(path.Trim()).Replace('\\', '/').TrimEnd('/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path.Trim(); }
    }

    public static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
}
