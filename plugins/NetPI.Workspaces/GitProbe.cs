namespace NetPI.Workspaces;

/// <summary>
/// The git questions workspace provisioning and attribution ask, answered by running git in a directory. Every answer is
/// cached for a moment: a hook that asks about the same checkout twice in one turn should not spawn four processes.
/// <para>
/// Repository identity is always the <em>common</em> git directory (<c>git rev-parse --git-common-dir</c>), which a
/// linked worktree reports as the main checkout's <c>.git</c>. Comparing top levels instead would make a worktree and
/// its main checkout look like different repositories, and a worktree is precisely a different top level.
/// </para>
/// </summary>
public sealed class GitProbe(TimeSpan? cacheFor = null) : IWorkspaceRepoProbe
{
    public static readonly TimeSpan DefaultCache = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _cacheFor = cacheFor ?? DefaultCache;
    private readonly Dictionary<string, CacheEntry> _cache = new(WorkspacePaths.Comparer);
    /// <summary>
    /// What a failed probe said, for as long as the cache is fresh. A failure is never cached as an answer — a broken
    /// git cannot keep answering "outside" for a while — so an isolated workspace can tell the two apart: what git said
    /// here is what <see cref="IWorkspaceRepoProbe.ProbeProblem"/> reports, and it is what a refusal quotes.
    /// </summary>
    private readonly Dictionary<string, ProblemEntry> _problems = new(WorkspacePaths.Comparer);
    private readonly object _gate = new();
    private static readonly string[] CommonDirArgs = ["rev-parse", "--path-format=absolute", "--git-common-dir"];

    private sealed record CacheEntry(DateTime At, string Value);
    private sealed record ProblemEntry(DateTime At, string Problem);

    /// <summary>Whether git answers at all on this machine (one probe, then cached forever).</summary>
    public static bool Available { get; set; } = true;

    public string? CommonDirOf(string path) => Ask(path, CommonDirArgs);
    public Task<string?> CommonDirOfAsync(string path, CancellationToken ct) => AskAsync(path, ct, CommonDirArgs);
    public string? BranchOf(string path) => Ask(path, "rev-parse", "--abbrev-ref", "HEAD");
    public string? HeadOf(string path) => Ask(path, "rev-parse", "HEAD");

    /// <summary>The top level of the working tree at a directory, or null when it is not in a repository.</summary>
    public string? TopLevelOf(string path) => Ask(path, "rev-parse", "--show-toplevel");

    /// <summary>Whether the directory is inside a git repository at all.</summary>
    public bool IsRepository(string path) => TopLevelOf(path) is not null;

    /// <summary>Uncommitted changes, a short summary for the cleanup refusal and the UI.</summary>
    public string DescribeChanges(string path)
    {
        var status = Run(path, "status", "--porcelain", "--untracked-files=normal");
        if (status is null) return "";
        var lines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("; ", lines.Take(10).Select(l => l.Length > 3 ? l[3..].Trim() : l.Trim())) +
               (lines.Length > 10 ? $" (+{lines.Length - 10} more)" : "");
    }

    public async Task<string?> RunAsync(string cwd, CancellationToken ct, params string[] args)
    {
        var r = await GitRunner.RunAsync(cwd, ct, 0, args).ConfigureAwait(false);
        if (r.Aborted) throw new OperationCanceledException(ct);
        if (r.StartError is not null || r.TimedOut) return null;
        return r.ExitCode == 0 ? r.Stdout.Trim() : null;
    }

    /// <summary>Run git and return stdout, or null when it failed, timed out or git is missing.</summary>
    public string? Run(string cwd, params string[] args)
    {
        try { return RunAsync(cwd, CancellationToken.None, args).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Run git and return (exit code, combined output), for the operations whose failure has to be reported.</summary>
    public async Task<(int Code, string Output)> ExecAsync(string cwd, CancellationToken ct, params string[] args)
    {
        var (code, stdout, stderr) = await ExecSplitAsync(cwd, ct, args).ConfigureAwait(false);
        return (code, stdout + stderr);
    }

    /// <summary>Run git and return (exit code, stdout, stderr) apart: an answer is stdout, a warning on stderr is not part of it.</summary>
    public async Task<(int Code, string Stdout, string Stderr)> ExecSplitAsync(string cwd, CancellationToken ct, params string[] args)
    {
        var r = await GitRunner.RunAsync(cwd, ct, 0, args).ConfigureAwait(false);
        if (r.Aborted) throw new OperationCanceledException(ct);
        if (r.StartError is not null) return (127, "", r.StartError);
        if (r.TimedOut) return (124, "", "git timed out");
        return (r.ExitCode, r.Stdout, r.Stderr);
    }

    /// <summary>The synchronous question, for the callers whose own contract is synchronous (the resolver, provisioning).
    /// The hooks and the tools ask <see cref="AskAsync"/>, so they park no thread on git.</summary>
    private string? Ask(string path, params string[] args) => AskAsync(path, CancellationToken.None, args).GetAwaiter().GetResult();

    private async Task<string?> AskAsync(string path, CancellationToken ct, params string[] args)
    {
        var dir = DirectoryFor(path);
        if (dir is null) return null;    // no existing directory: the answer is "not in a repository", not a failure
        var key = string.Join(' ', args) + " " + dir;
        lock (_gate)
            if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < _cacheFor) return hit.Value;
        string? value = null;
        try
        {
            if (Directory.Exists(dir) || File.Exists(dir))
            {
                var (code, stdout, stderr) = await ExecSplitAsync(dir, ct, args).ConfigureAwait(false);
                if (code == 0) value = stdout.Trim();   // the answer is stdout: a warning git prints on stderr must not become a commit id
                else if (!NotARepository(dir, code)) RememberProblem(key, stderr + stdout);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            RememberProblem(key, ex.Message);
        }
        lock (_gate)
        {
            if (value is not null)
            {
                _cache[key] = new CacheEntry(DateTime.UtcNow, value);
                _problems.Remove(key);
                if (_cache.Count > 512) _cache.Clear();
            }
        }
        return value;
    }

    /// <summary>
    /// Whether git's refusal is the definitive "this is not in a repository": git exited 128 and there is no <c>.git</c>
    /// (a directory, or the file of a worktree) in the directory or any above it. Judged from the file system, not from
    /// git's words, which change with the locale and with the platform ("or any parent up to mount point /tmp"). A broken
    /// <c>.git</c> is there and git still fails: git ran and failed, which is a probe problem, not an answer.
    /// </summary>
    private static bool NotARepository(string dir, int code)
    {
        if (code != 128) return false;
        try
        {
            for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            {
                var entry = Path.Combine(d.FullName, ".git");
                if (Directory.Exists(entry) || File.Exists(entry)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Remember what a failed probe said, so <see cref="ProbeProblem"/> can report it while it is fresh.</summary>
    private void RememberProblem(string key, string output)
    {
        var problem = output.Trim();
        if (problem.Length == 0) problem = "git could not answer";
        if (problem.Length > 300) problem = problem[..300] + "…";
        lock (_gate)
        {
            _problems[key] = new ProblemEntry(DateTime.UtcNow, problem);
            if (_problems.Count > 512) _problems.Clear();
        }
    }

    /// <summary>
    /// What this probe itself said when it could not answer for a path (git missing, timed out, or git's error): git's
    /// words, fresh for the cache window. An answer — including "not a repository" — and a path never asked have nothing
    /// to say, so the guard can decide; only a failure leaves an isolated workspace refusing.
    /// </summary>
    public string? ProbeProblem(string path)
    {
        var dir = DirectoryFor(path);
        if (dir is null) return null;
        var key = string.Join(' ', CommonDirArgs) + " " + dir;
        lock (_gate)
            return _problems.TryGetValue(key, out var p) && DateTime.UtcNow - p.At < _cacheFor ? p.Problem : null;
    }

    /// <summary>
    /// The directory to run git in for a path a caller named: the path itself when it is a directory, its folder when it
    /// is a file, and the nearest existing ancestor otherwise. Without this a file target answered nothing, and "is this
    /// file in another checkout of the repository" would have said "outside" for every file.
    /// </summary>
    private static string? DirectoryFor(string path)
    {
        try
        {
            if (Directory.Exists(path)) return WorkspacePaths.Canonical(path);
            if (File.Exists(path)) return WorkspacePaths.Canonical(Path.GetDirectoryName(path) ?? path);
            for (var dir = new DirectoryInfo(WorkspacePaths.Canonical(path)); dir is not null; dir = dir.Parent)
                if (dir.Exists) return WorkspacePaths.Canonical(dir.FullName);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Forget what is cached about a directory (after creating or removing a worktree there).</summary>
    public void Forget(string path)
    {
        var dir = WorkspacePaths.Canonical(path);
        lock (_gate)
        {
            foreach (var key in _cache.Keys.Where(k => k.EndsWith(" " + dir, StringComparison.OrdinalIgnoreCase)).ToList())
                _cache.Remove(key);
            foreach (var key in _problems.Keys.Where(k => k.EndsWith(" " + dir, StringComparison.OrdinalIgnoreCase)).ToList())
                _problems.Remove(key);
        }
    }

    /// <summary>Every ref that contains a commit: the branches a worker already landed its work on.</summary>
    public IReadOnlyList<string> BranchesContaining(string path, string commit)
    {
        var outp = Run(path, "branch", "--format=%(refname:short)", "--contains", commit);
        return string.IsNullOrWhiteSpace(outp) ? [] : [.. outp.Split('\n', StringSplitOptions.TrimEntries)];
    }

    /// <summary>Whether a branch exists locally.</summary>
    public bool BranchExists(string path, string branch) => Run(path, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch) is not null;
}