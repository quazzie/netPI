using System.Diagnostics;
using System.Text;

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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    /// <summary>The empty tree's hash, for a repository without commits (git's own well-known constant).</summary>
    public const string EmptyTreeHash = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

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
    public string? BranchOf(string path) => Ask(path, "rev-parse", "--abbrev-ref", "HEAD");
    public string? HeadOf(string path) => Ask(path, "rev-parse", "HEAD");

    /// <summary>The repository's main checkout (the worktree that holds <c>.git</c>), or the directory itself when it is one.</summary>
    public string? MainCheckoutOf(string path)
    {
        var top = TopLevelOf(path);
        if (top is null) return null;
        var gitDir = Run(top, "rev-parse", "--absolute-git-dir");
        var common = CommonDirOf(top);
        // In the main checkout the common dir is <root>/.git; in a linked worktree it is <main>/.git as well.
        return common is null ? top : Path.GetDirectoryName(WorkspacePaths.Canonical(common));
    }

    /// <summary>The top level of the working tree at a directory, or null when it is not in a repository.</summary>
    public string? TopLevelOf(string path) => Ask(path, "rev-parse", "--show-toplevel");

    /// <summary>Whether the directory is inside a git repository at all.</summary>
    public bool IsRepository(string path) => TopLevelOf(path) is not null;

    /// <summary>The working tree is clean (no staged, unstaged or untracked changes). False when git cannot answer.</summary>
    public bool IsClean(string path)
    {
        var status = Run(path, "status", "--porcelain");
        return status is not null && status.Trim().Length == 0;
    }

    /// <summary>Uncommitted changes, a short summary for the cleanup refusal and the UI.</summary>
    public string DescribeChanges(string path)
    {
        var status = Run(path, "status", "--porcelain", "--untracked-files=normal");
        if (status is null) return "";
        var lines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("; ", lines.Take(10).Select(l => l.Length > 3 ? l[3..].Trim() : l.Trim())) +
               (lines.Length > 10 ? $" (+{lines.Length - 10} more)" : "");
    }

    /// <summary>Commits on a branch that are not on <paramref name="into"/>: what a merge would bring.</summary>
    public IReadOnlyList<string> CommitsAhead(string path, string branch, string into)
    {
        var log = Run(path, "log", "--format=%h %s", $"{into}..{branch}", "--no-color");
        if (string.IsNullOrWhiteSpace(log)) return [];
        return [.. log.Split('\n', StringSplitOptions.TrimEntries)];
    }

    public async Task<string?> RunAsync(string cwd, CancellationToken ct, params string[] args)
    {
        var psi = NewPsi(cwd);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return await StartAsync(psi, ct).ConfigureAwait(false);
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
        var psi = NewPsi(cwd);
        psi.RedirectStandardInput = true;
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process process;
        try { process = Process.Start(psi)!; }
        catch (Exception ex) { return (127, ex.Message); }
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                try { process.StandardInput.Close(); } catch { }
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
                return (process.ExitCode, output);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                return (124, "git timed out");
            }
        }
    }

    private static ProcessStartInfo NewPsi(string cwd)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return psi;
    }

    private static async Task<string?> StartAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        Process process;
        try { process = Process.Start(psi)!; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                _ = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? (await stdout.ConfigureAwait(false)).Trim() : null;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                return null;
            }
        }
    }

    private string? Ask(string path, params string[] args)
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
                var (code, output) = ExecAsync(dir, CancellationToken.None, args).GetAwaiter().GetResult();
                if (code == 0) value = output.Trim();
                else if (!NotARepository(output)) RememberProblem(key, output);
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
    /// Whether git's refusal is the definitive "this is not in a repository" — the message git gives for a directory
    /// with no repository above it. A broken <c>.git</c> ("not a git repository: (NULL)") is the same words with that
    /// clause missing: git ran and failed, which is a probe problem, not an answer.
    /// </summary>
    private static bool NotARepository(string output) =>
        output.Contains("not a git repository (or any of the parent directories)", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Whether the repository has a commit (so a diff can be taken against it).</summary>
    public bool HasCommit(string path, string commit)
    {
        if (string.IsNullOrWhiteSpace(commit)) return false;
        var (code, _) = ExecAsync(path, CancellationToken.None, "cat-file", "-e", commit + "^{commit}").GetAwaiter().GetResult();
        return code == 0;
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