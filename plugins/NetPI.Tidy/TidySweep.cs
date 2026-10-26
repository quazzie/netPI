using System.Globalization;

namespace NetPI.Tidy;

/// <summary>
/// The periodic sweep: what is left behind across the repositories, not only by the run that just ended. An agent that
/// died never reaches its turn end, so the run-end notice (<see cref="TidyHook"/>) cannot catch what it left; a weekly
/// NetPI schedule runs the <c>tidy</c> tool's <c>sweep</c> and acts on this list. Read-only: it reports, it never deletes.
/// <para>
/// Each item has a <see cref="Item.Kind"/> the acting agent keys on: <c>merged</c> (a worktree or branch fully merged
/// into the base branch and clean — garbage under the repository rules, safe to remove) and everything else (unmerged
/// work, uncommitted changes, stashes, old scratch), which is somebody's work and goes to the owner. Only what has seen
/// no activity for <c>staleDays</c> counts, so a worker in the middle of a task is not reported.
/// </para>
/// </summary>
public static class TidySweep
{
    public sealed record Item(string Repo, string Kind, string Text, string? Path = null, string? Branch = null);

    public static async Task<List<Item>> SweepAsync(IEnumerable<string> roots, IReadOnlyCollection<string> exclude, int staleDays,
        DateTimeOffset now, CancellationToken ct)
    {
        var items = new List<Item>();
        var cutoff = now.AddDays(-staleDays);
        foreach (var root in Expand(roots).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (Excluded(root, exclude)) continue;
            var repo = await TidyFacts.RepoOfAsync(root, ct).ConfigureAwait(false);
            if (repo is null || repo.Linked || !TidyFacts.Same(repo.Top, root)) continue;   // main checkouts only; their worktrees come with them
            var name = Path.GetFileName(root);
            var baseBranch = repo.BaseBranch;

            // worktrees
            var list = await Git(root, ct, "worktree", "list", "--porcelain").ConfigureAwait(false) ?? "";
            var withWorktree = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, branch, prunable) in Worktrees(list).Skip(1))
            {
                if (branch is not null) withWorktree.Add(branch);
                if (prunable) { items.Add(new Item(name, "prunable", $"worktree {path} is registered but its folder is gone (`git worktree prune`)", path, branch)); continue; }
                if (Excluded(path, exclude)) continue;
                var dirty = TidyFacts.StatusPaths(await Git(path, ct, "status", "--porcelain").ConfigureAwait(false) ?? "");
                var last = branch is null ? null : await LastCommitAsync(root, branch, ct).ConfigureAwait(false);
                var touched = Newest(path, dirty);
                if (Max(last, touched) is { } active && active > cutoff) continue;          // worked on recently: not stale
                var ahead = branch is null || baseBranch is null ? null : await AheadAsync(root, baseBranch, branch, ct).ConfigureAwait(false);
                var age = Age(Max(last, touched), now);
                if (dirty.Count > 0)
                    items.Add(new Item(name, "dirty", $"worktree {path} [{branch ?? "detached"}]: {dirty.Count} uncommitted change(s), untouched {age}", path, branch));
                else if (ahead == 0)
                    items.Add(new Item(name, "merged", $"worktree {path} [{branch}]: merged into {baseBranch} and clean, {age} — remove it and its branch", path, branch));
                else
                    items.Add(new Item(name, "unmerged", $"worktree {path} [{branch ?? "detached"}]: {ahead?.ToString(CultureInfo.InvariantCulture) ?? "?"} commit(s) not in {baseBranch}, last {age}", path, branch));
            }

            // branches without a worktree
            var branches = (await Git(root, ct, "for-each-ref", "--format=%(refname:short)", "refs/heads").ConfigureAwait(false) ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var b in branches)
            {
                if (b == baseBranch || b == repo.Branch || withWorktree.Contains(b) || baseBranch is null) continue;
                // a base or a mirror of upstream, never finished work: a fork's main kept beside the branch it runs
                // (unbiasedstrata's main, merged into nuc-q4k-down-gpu-vision, was swept as garbage on the first run)
                if (IsBaseName(b) || await TracksRemoteAsync(root, b, ct).ConfigureAwait(false)) continue;
                var last = await LastCommitAsync(root, b, ct).ConfigureAwait(false);
                if (last is { } l && l > cutoff) continue;
                var ahead = await AheadAsync(root, baseBranch, b, ct).ConfigureAwait(false);
                items.Add(ahead == 0
                    ? new Item(name, "merged", $"branch {b}: merged into {baseBranch}, {Age(last, now)} — delete it (`git branch -d`)", Branch: b)
                    : new Item(name, "unmerged", $"branch {b}: {ahead?.ToString(CultureInfo.InvariantCulture) ?? "?"} commit(s) not in {baseBranch}, last {Age(last, now)}", Branch: b));
            }

            // stashes
            var stashes = (await Git(root, ct, "stash", "list", "--format=%ct %gs").ConfigureAwait(false) ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => Epoch(s.Split(' ')[0]) is { } t && t < cutoff).ToList();
            if (stashes.Count > 0) items.Add(new Item(name, "stash", $"{stashes.Count} stash(es) older than {staleDays} days ({string.Join("; ", stashes.Take(3).Select(s => s[(s.IndexOf(' ') + 1)..]))})"));

            // uncommitted work in the main checkout that nobody touched for a while
            var mainDirty = TidyFacts.StatusPaths(await Git(root, ct, "status", "--porcelain").ConfigureAwait(false) ?? "")
                .Where(p => !p.StartsWith("scratch/", StringComparison.OrdinalIgnoreCase) && !p.StartsWith(".worktrees/", StringComparison.OrdinalIgnoreCase)).ToList();
            var oldDirty = mainDirty.Where(p => Modified(System.IO.Path.Combine(root, p)) is { } m && m < cutoff).ToList();
            if (oldDirty.Count > 0)
                items.Add(new Item(name, "dirty", $"main checkout: {oldDirty.Count} uncommitted change(s) untouched for {staleDays}+ days ({string.Join(", ", oldDirty.Take(TidyFacts.MaxNamed))})", root));

            // scratch
            foreach (var entry in TidyFacts.ScratchEntries(root, null))
                if (Modified(entry) is { } m && m < cutoff)
                    items.Add(new Item(name, "scratch", $"scratch {System.IO.Path.GetFileName(entry)}, last changed {Age(m, now)}", entry));
        }
        return items;
    }

    /// <summary>The report the tool returns: one block per repository, the safe removals first.</summary>
    public static string Render(IReadOnlyList<Item> items, int staleDays)
    {
        if (items.Count == 0) return $"Nothing left behind: no stale worktree, branch, stash, uncommitted change or scratch (older than {staleDays} days).";
        var lines = new List<string> { $"Left behind (no activity for {staleDays}+ days). `merged` items are safe to remove; the rest is someone's work — report it to the owner." };
        foreach (var repo in items.GroupBy(i => i.Repo))
        {
            lines.Add("");
            lines.Add(repo.Key + ":");
            lines.AddRange(repo.OrderBy(i => i.Kind == "merged" ? 0 : 1).Select(i => $"- [{i.Kind}] {i.Text}"));
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The roots as repositories: a folder that is a repository's top stays as it is; any other folder stands for the
    /// repositories directly inside it (<c>C:\AI\Projects</c> → every project there, NetPI project or not). Missing folders
    /// drop out, so a project that was moved or archived costs nothing.
    /// </summary>
    public static IEnumerable<string> Expand(IEnumerable<string> roots)
    {
        foreach (var raw in roots)
        {
            var root = TidyFacts.Norm(raw);
            if (!Directory.Exists(root)) continue;
            if (Path.Exists(System.IO.Path.Combine(root, ".git"))) { yield return root; continue; }
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(root).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
                if (Path.Exists(System.IO.Path.Combine(child, ".git"))) yield return TidyFacts.Norm(child);
        }
    }

    public static IEnumerable<(string Path, string? Branch, bool Prunable)> Worktrees(string porcelain)
    {
        string? path = null, branch = null; var prunable = false;
        foreach (var raw in porcelain.Split('\n').Append(""))
        {
            var l = raw.TrimEnd('\r');
            if (l.Length == 0)
            {
                if (path is not null) yield return (TidyFacts.Norm(path), branch, prunable);
                path = null; branch = null; prunable = false;
            }
            else if (l.StartsWith("worktree ", StringComparison.Ordinal)) path = l[9..];
            else if (l.StartsWith("branch refs/heads/", StringComparison.Ordinal)) branch = l[18..];
            else if (l.StartsWith("prunable", StringComparison.Ordinal)) prunable = true;
        }
    }

    /// <summary>Names a repository's base goes by: never an agent's branch, whatever it is merged into.</summary>
    public static bool IsBaseName(string branch) => branch is "main" or "master" or "develop" or "trunk";

    private static async Task<bool> TracksRemoteAsync(string root, string branch, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(await Git(root, ct, "config", "--get", $"branch.{branch}.remote").ConfigureAwait(false));

    private static bool Excluded(string path, IReadOnlyCollection<string> exclude) =>
        exclude.Any(e => !string.IsNullOrWhiteSpace(e) &&
            (TidyFacts.Same(path, e) || path.Replace('\\', '/').Split('/').Contains(e.Trim(), StringComparer.OrdinalIgnoreCase)));

    private static async Task<DateTimeOffset?> LastCommitAsync(string root, string branch, CancellationToken ct) =>
        Epoch((await Git(root, ct, "log", "-1", "--format=%ct", branch, "--").ConfigureAwait(false))?.Trim());

    private static async Task<int?> AheadAsync(string root, string baseBranch, string branch, CancellationToken ct) =>
        int.TryParse((await Git(root, ct, "rev-list", "--count", $"{baseBranch}..{branch}", "--").ConfigureAwait(false))?.Trim(), out var n) ? n : null;

    private static DateTimeOffset? Newest(string root, IEnumerable<string> relative) =>
        relative.Select(p => Modified(System.IO.Path.Combine(root, p))).Where(m => m is not null).Max();

    private static DateTimeOffset? Modified(string path)
    {
        try
        {
            if (File.Exists(path)) return File.GetLastWriteTimeUtc(path);
            if (Directory.Exists(path)) return Directory.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static DateTimeOffset? Epoch(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? DateTimeOffset.FromUnixTimeSeconds(t) : null;

    private static string Age(DateTimeOffset? at, DateTimeOffset now) =>
        at is not { } a ? "at an unknown time" : (int)(now - a).TotalDays is var d && d >= 1 ? $"{d} days ago" : "today";

    private static async Task<string?> Git(string cwd, CancellationToken ct, params string[] args)
    {
        var r = await GitRunner.RunAsync(cwd, ct, 0, args).ConfigureAwait(false);
        return r.StartError is null && !r.TimedOut && !r.Aborted && r.ExitCode == 0 ? r.Stdout : null;
    }
}
