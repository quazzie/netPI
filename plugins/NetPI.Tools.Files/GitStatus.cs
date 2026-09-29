using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NetPI.Tools.Files;

/// <summary>
/// The Files tab's git line (<c>files.git</c>): the branch, and what changed since the last commit, staged or not, new
/// files included: each file with its status and the lines added and deleted, and the totals. Null outside a git
/// repository or without git. Runs <c>git status</c> and <c>git diff --numstat</c> without taking the index lock
/// (<c>GIT_OPTIONAL_LOCKS=0</c>), so an agent's git commands never wait for it.
/// </summary>
internal static class GitStatus
{
    /// <summary>New files are counted up to this size and number (a binary file has no lines).</summary>
    public const int MaxNewFileBytes = 1024 * 1024;
    public const int MaxNewFiles = 500;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"; // a repository without commits

    /// <summary>status: modified | added | deleted | renamed | copied | conflict | new (untracked).</summary>
    public sealed record Change(string Path, string Rel, string Status, int? Added, int? Deleted);

    /// <summary>One commit: the full and short hash, its subject, who wrote it and when.</summary>
    public sealed record Commit(string Hash, string Short, string Subject, string Author, string At);

    /// <summary>What <c>files.commits</c> answers: the repository and its commits, newest first (null outside one).</summary>
    public sealed record CommitsResult(string Repo, IReadOnlyList<Commit> Commits);

    public sealed record Result(string Repo, string? Branch, int Ahead, int Behind, IReadOnlyList<Change> Files, int Added, int Deleted);

    /// <summary>Between the fields of one <c>git log</c> line: a subject line can hold anything else.</summary>
    private const char Field = '\u001f';

    public static async Task<Result?> ReadAsync(string root, CancellationToken ct)
    {
        var top = (await GitAsync(root, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(top)) return null;
        var repo = Path.GetFullPath(top); // git prints C:/x on Windows
        var status = await GitAsync(repo, ct, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all").ConfigureAwait(false);
        if (status is null) return null;
        var hasHead = !string.IsNullOrWhiteSpace(await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", "HEAD").ConfigureAwait(false));
        var numstat = await GitAsync(repo, ct, "diff", "--numstat", "-z", hasHead ? "HEAD" : EmptyTree).ConfigureAwait(false) ?? "";
        return Build(repo, root, status, numstat);
    }

    /// <summary>
    /// The commits of the repository, newest first: everything down to <paramref name="since"/> (a hash the caller last
    /// saw) or the newest <paramref name="limit"/>. Null outside a git repository, or when the hash is unknown to it
    /// (a rewritten history, a repository that was replaced) — the caller then starts again from the newest commit.
    /// </summary>
    public static async Task<CommitsResult?> CommitsAsync(string root, string? since, int limit, CancellationToken ct)
    {
        var top = (await GitAsync(root, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(top)) return null;
        var repo = Path.GetFullPath(top);
        var format = $"--format=%H{Field}%h{Field}%an{Field}%aI{Field}%s";
        var log = since is { Length: > 0 } s && LooksLikeHash(s)
            ? await GitAsync(repo, ct, "log", $"{s}..HEAD", $"-{limit}", format, "--no-color").ConfigureAwait(false)
            : await GitAsync(repo, ct, "log", $"-{limit}", format, "--no-color").ConfigureAwait(false);
        if (log is null) return null;
        var commits = new List<Commit>();
        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(Field);
            if (f.Length < 5) continue;
            commits.Add(new Commit(f[0].Trim(), f[1].Trim(), f[4].Trim(), f[2].Trim(), f[3].Trim()));
        }
        return new CommitsResult(repo, commits);

        static bool LooksLikeHash(string s) => s.Length is >= 7 and <= 64 && s.All(char.IsLetterOrDigit);
    }

    /// <summary>Parses <c>git status --porcelain=v2 --branch -z</c> and <c>git diff --numstat -z</c> (paths relative to the repository).</summary>
    internal static Result Build(string repo, string root, string status, string numstat)
    {
        var lines = ParseNumstat(numstat);
        string? branch = null;
        int ahead = 0, behind = 0;
        var files = new List<Change>();
        var fields = status.Split('\0');
        var newFiles = 0;
        for (var i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            if (f.Length == 0) continue;
            if (f.StartsWith("# branch.head ", StringComparison.Ordinal)) branch = f["# branch.head ".Length..] is var b && b != "(detached)" ? b : "detached";
            else if (f.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var ab = f["# branch.ab ".Length..].Split(' ');
                if (ab.Length == 2) { int.TryParse(ab[0].TrimStart('+'), out ahead); int.TryParse(ab[1].TrimStart('-'), out behind); }
            }
            else if (f[0] == '1' && f.Length > 2) Add(f.Split(' ', 9), 8, Kind(f[2..4]));
            else if (f[0] == '2' && f.Length > 2) { Add(f.Split(' ', 10), 9, f[2] == 'C' || f[3] == 'C' ? "copied" : "renamed"); i++; } // the next field is the old path
            else if (f[0] == 'u') Add(f.Split(' ', 11), 10, "conflict");
            else if (f[0] == '?' && f.Length > 2)
            {
                var path = f[2..];
                var count = newFiles++ < MaxNewFiles ? CountLines(Path.Combine(repo, path)) : null;
                files.Add(Make(path, "new", count, count is null ? null : 0));
            }
        }
        return new Result(repo, branch, ahead, behind, files, files.Sum(c => c.Added ?? 0), files.Sum(c => c.Deleted ?? 0));

        void Add(string[] parts, int pathIndex, string kind)
        {
            if (parts.Length <= pathIndex) return;
            var path = parts[pathIndex];
            lines.TryGetValue(path, out var n);
            files.Add(Make(path, kind, n.Added, n.Deleted));
        }

        Change Make(string gitPath, string kind, int? added, int? deleted)
        {
            var full = Path.GetFullPath(Path.Combine(repo, gitPath));
            return new Change(full, Path.GetRelativePath(root, full).Replace('\\', '/'), kind, added, deleted);
        }
    }

    /// <summary>XY of an ordinary entry: the index (X) and the worktree (Y) side.</summary>
    private static string Kind(string xy) =>
        xy.Contains('D') ? "deleted" : xy[0] == 'A' ? "added" : "modified";

    /// <summary>path → (added, deleted); null for binary files. Renames come as an empty path, then the old and the new one.</summary>
    internal static Dictionary<string, (int? Added, int? Deleted)> ParseNumstat(string numstat)
    {
        var map = new Dictionary<string, (int? Added, int? Deleted)>(StringComparer.Ordinal);
        var fields = numstat.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var parts = fields[i].Split('\t', 3);
            if (parts.Length < 3) continue;
            var path = parts[2];
            if (path.Length == 0 && i + 2 < fields.Length) { path = fields[i + 2]; i += 2; }
            map[path] = (int.TryParse(parts[0], out var a) ? a : null, int.TryParse(parts[1], out var d) ? d : null);
        }
        return map;
    }

    /// <summary>Lines of a new text file; null for a binary, a large or an unreadable one.</summary>
    private static int? CountLines(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxNewFileBytes) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0) return null;
            var n = bytes.AsSpan().Count((byte)'\n');
            return bytes.Length > 0 && bytes[^1] != '\n' ? n + 1 : n;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>stdout of a git command in <paramref name="dir"/>, or null when it fails, times out or git is missing.</summary>
    private static async Task<string?> GitAsync(string dir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=off");
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        Process process;
        try { process = Process.Start(psi)!; }
        catch (Win32Exception) { return null; } // no git on PATH
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                _ = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                ct.ThrowIfCancellationRequested();
                return null;
            }
        }
    }
}
