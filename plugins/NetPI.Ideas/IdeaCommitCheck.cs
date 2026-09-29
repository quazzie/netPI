using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// Close on commit (phase 3 of docs/plans/2026-09-27-ideas-follow-the-session.md). An idea is finished by a commit, and
/// nobody notices: the backlog keeps saying "open" about work that is already in the history. So each project with a git
/// repository is watched, and every new commit is read twice over — measured, in the order that measured best
/// (docs/DECISION-MODELS.md, "Ideas: … close on commit"):
/// <list type="number">
/// <item><b>which idea is this commit about?</b> A commit message that names an idea id is the match without a model.
/// Otherwise one pick-one decision over the ideas open at that moment, linking every option that clears
/// <c>ideas.linkThreshold</c> (0 wrong at 0.7, 2/187 unrelated commits) — a commit can finish two ideas, so it is not
/// forced to pick one. The commit is recorded on the idea as a <c>commits</c> entry, beside its text, never inside it.</item>
/// <item><b>is the idea done?</b> Asked with the idea's full text (its plan and what is left) <em>and all its linked
/// commits</em>, not from one commit and a summary: 4/5 finished ideas offered, no offer on a commit that only advanced
/// its idea. On a clear margin (<c>ideas.doneThreshold</c>, 0.8) a card asks the user. One offer per idea.</item>
/// </list>
/// The card is an offer, never an action: nothing is marked done without a click, and a commit that only advances an
/// idea never closes it.
/// </summary>
public sealed class IdeaCommitCheck(IPluginContext ctx, IdeasStore store, IdeasLocator locator, IdeaSaveCheck save) : IDisposable
{
    public const string DefaultModel = "qwen3.8-27b";
    public const double DefaultLinkThreshold = 0.7;
    public const double DefaultDoneThreshold = 0.8;
    /// <summary>Commits read per sweep. A burst (a rebase, a merge train) is read oldest first and capped here.</summary>
    public const int MaxCommits = 20;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Rescan = TimeSpan.FromMinutes(2);
    /// <summary>How long a repository's last-seen commit is remembered; an untouched repository is dropped from the file.</summary>
    private const int RepoKeepDays = 60;
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private readonly Lock _watchLock = new();
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.OrdinalIgnoreCase); // repo path → its watcher
    private Timer? _timer;
    private volatile bool _stopped;
    private bool _disposed;

    private sealed class Watch(string repo, string path, string? projectId, string? projectName) : IDisposable
    {
        public string Repo { get; } = repo;
        public string Path { get; } = path;
        public string? ProjectId { get; } = projectId;
        public string? ProjectName { get; } = projectName;
        public FileSystemWatcher? Fs { get; set; }
        /// <summary>One sweep at a time, and the last one is not lost while it runs.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Again { get; set; }
        public void Dispose() { Fs?.Dispose(); Gate.Dispose(); }
    }

    // ------------------------------------------------------------------ watching

    /// <summary>Start watching every project that has a repository. Safe to call again: it re-reads the project list.</summary>
    public async Task StartAsync()
    {
        if (_stopped) return;
        _timer ??= new Timer(_ => _ = RescanAsync(), null, Rescan, Rescan);
        await RescanAsync().ConfigureAwait(false);
    }

    public void Stop()
    {
        _stopped = true;
        _timer?.Dispose();
        _timer = null;
        lock (_watchLock)
        {
            foreach (var w in _watches.Values) w.Dispose();
            _watches.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>A new project may bring a repository; the list is re-read on a timer and on <c>project.created</c>.</summary>
    private async Task RescanAsync()
    {
        if (_stopped || !Setting("ideas.closeOnCommit", true)) return;
        if (!ctx.Rpc.Exists("files.commits")) return;   // no Files plugin: nothing can read the commits
        foreach (var project in ctx.Sessions.ListProjects())
        {
            if (_stopped) return;
            try { await WatchProjectAsync(project).ConfigureAwait(false); }
            catch (Exception ex) { ctx.Logger.LogDebug(ex, "Ideas: cannot watch the repository of {Project}", project.Name); }
        }
    }

    private async Task WatchProjectAsync(ProjectInfo project)
    {
        if (!Directory.Exists(project.Path)) return;
        var found = await ctx.Rpc.InvokeAsync("files.commits", new JsonObject { ["cwd"] = project.Path, ["limit"] = 1 }).ConfigureAwait(false);
        // Through ToNode: an RPC answers with the handler's own object in-process and with its JSON over HTTP, and the
        // Files plugin answers with a record (like decide.decision's readers here, and for the same reason).
        var o = NetPiJson.ToNode(found) as JsonObject;
        if (IdeaOps.Str(o?["repo"]) is not { Length: > 0 } repo) return;
        var newest = (o["commits"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();

        lock (_watchLock)
        {
            if (_stopped || _watches.ContainsKey(repo)) return;
            var watch = new Watch(repo, project.Path, project.Id, project.Name);
            // Start at the commit that is already there: the backlog is not swept through its own history.
            RememberAsync(repo, IdeaOps.Str(newest?["hash"])).GetAwaiter().GetResult();
            try
            {
                // The whole .git directory, subdirectories included: the reflog (.git/logs/HEAD) only exists once a
                // repository has a first commit, so watching *it* would miss every commit of a fresh one. Any write here
                // (an index, a ref, the reflog) wakes the sweep, which only acts on commits it has not seen.
                var fs = new FileSystemWatcher(Path.Combine(repo, ".git"))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    IncludeSubdirectories = true,
                };
                fs.Changed += (_, _) => Schedule(watch);
                fs.Created += (_, _) => Schedule(watch);
                fs.Renamed += (_, _) => Schedule(watch);
                fs.Deleted += (_, _) => Schedule(watch);
                fs.EnableRaisingEvents = true;
                watch.Fs = fs;
            }
            catch (Exception ex) { watch.Dispose(); ctx.Logger.LogDebug(ex, "Ideas: cannot watch {Git} of {Repo}", Path.Combine(repo, ".git"), repo); return; }
            _watches[repo] = watch;
            ctx.Logger.LogDebug("Ideas: watching {Repo} for commits (project {Project})", repo, project.Name);
        }
    }

    /// <summary>Debounced: a commit writes the reflog a few times, and a burst is one sweep.</summary>
    private void Schedule(Watch watch)
    {
        if (_stopped) return;
        var timer = new Timer(_ =>
        {
            if (_stopped) return;
            // A sweep is already running: let it finish and run once more, rather than queueing one per write.
            if (!watch.Gate.Wait(0)) { watch.Again = true; return; }
            _ = Task.Run(async () =>
            {
                try { await SweepAsync(watch).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stopped) { }
                catch (Exception ex) { ctx.Logger.LogWarning("Ideas: the commit check on {Repo} failed: {Message}", watch.Repo, ex.Message); }
                finally
                {
                    watch.Gate.Release();
                    if (watch.Again && !_stopped) { watch.Again = false; Schedule(watch); }
                }
            }, CancellationToken.None);
        }, null, Debounce, Timeout.InfiniteTimeSpan);
    }

    private async Task SweepAsync(Watch watch)
    {
        if (_stopped) return;
        var since = await LastSeenAsync(watch.Repo).ConfigureAwait(false);
        var found = await ctx.Rpc.InvokeAsync("files.commits", new JsonObject
        {
            ["cwd"] = watch.Path, ["since"] = since ?? "", ["limit"] = MaxCommits,
        }).ConfigureAwait(false);
        if (NetPiJson.ToNode(found) is not JsonObject { } o || o["commits"] is not JsonArray commits || commits.Count == 0) return;
        ctx.Logger.LogDebug("Ideas: {Count} new commit(s) in {Repo} since {Since}", commits.Count, watch.Repo, since ?? "(the start)");

        // Oldest first: a burst is read in the order it happened, and the last commit decides what is left.
        foreach (var c in commits.OfType<JsonObject>().Reverse().Take(MaxCommits))
        {
            if (_stopped) return;
            await HandleAsync(watch, c).ConfigureAwait(false);
            await RememberAsync(watch.Repo, IdeaOps.Str(c["hash"])).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ one commit

    private async Task HandleAsync(Watch watch, JsonObject commit)
    {
        var hash = IdeaOps.Str(commit["hash"]);
        if (hash is not { Length: > 0 }) return;
        var subject = IdeaOps.Str(commit["subject"]) ?? "";
        var open = await OpenAsync(watch.ProjectId).ConfigureAwait(false);
        if (open.Count == 0) return;

        // 1. which idea is this commit about
        var linked = new List<JsonObject>();
        foreach (var idea in NamedIn(subject, open)) linked.Add(idea);   // deterministic: the message names the id
        if (linked.Count == 0)
        {
            var picks = await LinkAsync(open, commit).ConfigureAwait(false);
            linked.AddRange(picks);
        }
        if (linked.Count == 0) return;

        var entry = new JsonObject
        {
            ["hash"] = hash,
            ["short"] = IdeaOps.Str(commit["short"]) ?? hash[..Math.Min(7, hash.Length)],
            ["subject"] = Clip(subject, 200),
            ["at"] = commit["at"]?.DeepClone() ?? IdeaOps.Now(),
        };
        var titles = new List<string>();
        foreach (var idea in linked)
        {
            titles.Add(IdeaOps.Str(idea["title"]) ?? "?");
            var id = IdeaOps.Str(idea["id"]);
            await store.UpdateAsync<object?>(locator.GlobalFile(), f =>
            {
                if (id is { Length: > 0 } && IdeaOps.Find(f.Ideas, id) is { } found) IdeaOps.AddCommitEntry(found, entry);
                return null;
            }).ConfigureAwait(false);
        }

        // 2. is any of them done: the idea's full text and every commit linked to it, not this one alone
        foreach (var idea in linked)
        {
            if (_stopped) return;
            var id = IdeaOps.Str(idea["id"]);
            if (id is not { Length: > 0 }) continue;
            var fresh = await store.ReadAsync(locator.GlobalFile(), f => IdeaOps.Find(f.Ideas, id) is { } i
                ? (JsonObject)i.DeepClone() : null).ConfigureAwait(false);
            if (fresh is null || IdeaOps.Str(fresh["status"]) is not ("open" or "planned" or "in-progress" or "parked")) continue;
            if (await OfferDoneAsync(watch, fresh, entry, titles).ConfigureAwait(false)) return;  // one offer per sweep
        }
    }

    /// <summary>A commit message that names an idea id (<c>… idea-c7xyem</c>) is the match, with no model at all.</summary>
    private static List<JsonObject> NamedIn(string subject, List<JsonObject> open)
    {
        var found = new List<JsonObject>();
        foreach (var idea in open)
        {
            var id = IdeaOps.Str(idea["id"]);
            if (id is { Length: > 0 } && subject.Contains(id, StringComparison.OrdinalIgnoreCase)) found.Add(idea);
        }
        return found;
    }

    /// <summary>Pick-one over the ideas open then; every option that clears the threshold is linked.</summary>
    private async Task<List<JsonObject>> LinkAsync(List<JsonObject> open, JsonObject commit)
    {
        if (!ctx.Rpc.Exists("decide.decision")) return [];
        var candidates = open.Take(Letters.Length - 1).ToList();
        if (candidates.Count == 0) return [];
        var none = Letters[candidates.Count].ToString();
        var list = new StringBuilder();
        for (var k = 0; k < candidates.Count; k++)
        {
            list.Append(Letters[k]).Append(") [").Append(IdeaOps.ProjectLabel(candidates[k])).Append("] ").Append(IdeaOps.Str(candidates[k]["title"]));
            if (OneLine(IdeaOps.Str(candidates[k]["summary"])) is { Length: > 0 } s) list.Append(" — ").Append(Clip(s, 240));
            list.Append('\n');
        }
        list.Append(none).Append(") none of these: this commit is about something else");

        var subject = IdeaOps.Str(commit["subject"]) ?? "";
        var author = IdeaOps.Str(commit["author"]) ?? "";
        var probs = await DecideAsync(new JsonObject
        {
            ["role"] = "system",
            ["content"] = "You decide which of the user's backlog of open ideas a git commit was about, so the commit can be " +
                          "recorded on it. Answer with the letter of the best option only.\n\n" +
                          $"Commit by {author}:\n{subject}\n\nOpen ideas:\n" + list,
        }, $"Which open idea is this commit about? Pick it when the commit works on it (implements it, or a step of it, " +
           "or fixes it), pick it even when it only advances the idea. Pick " + none + " when it is about something else.",
            Enumerable.Range(0, candidates.Count + 1).Select(k => Letters[k].ToString()).ToArray()).ConfigureAwait(false);
        if (probs is null) return [];
        var threshold = Math.Clamp(Setting("ideas.linkThreshold", DefaultLinkThreshold), 0.3, 0.99);
        double P(string label) => probs.TryGetValue(label, out var p) ? p : 0;
        if (P(none) >= threshold) return [];   // "none" wins: the commit is about something else
        return Enumerable.Range(0, candidates.Count).Where(k => P(Letters[k].ToString()) >= threshold).Select(k => candidates[k]).ToList();
    }

    /// <summary>
    /// "Is this idea finished?" — asked with the idea's full text and every commit linked to it, because asking from one
    /// commit and the summary alone offered only 5/12 (docs/DECISION-MODELS.md). On a clear margin a card is added; one per
    /// idea, and never for an idea that is already closed.
    /// </summary>
    private async Task<bool> OfferDoneAsync(Watch watch, JsonObject idea, JsonObject commit, List<string> committed)
    {
        if (!ctx.Rpc.Exists("decide.decision")) return false;
        var id = IdeaOps.Str(idea["id"]);
        if (id is not { Length: > 0 }) return false;
        var text = IdeaOps.RenderMarkdown(idea);
        if (text.Length < 40) return false;
        var linked = (idea["commits"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => $"{IdeaOps.Str(c["short"])} {IdeaOps.Str(c["subject"])}").ToList();
        if (linked.Count == 0) return false;

        var probs = await DecideAsync(new JsonObject
        {
            ["role"] = "system",
            ["content"] = "You decide whether a piece of work is finished, given the plan it belongs to and the commits " +
                          "that were made for it. Answer DONE only when everything the idea asked for is built and nothing " +
                          "of it is left to do. Answer MORE when the commits advance the idea but part of it is still to do, " +
                          "or when it is not clear.\n\n" +
                          $"Idea: {text}\n\nCommits linked to it:\n" + string.Join('\n', linked.Select((s, k) => $"{k + 1}. {s}")),
        }, $"Is the idea finished now? These commits were just made for it ({string.Join(", ", committed)}).",
            ["DONE", "MORE"]).ConfigureAwait(false);
        if (probs is null) return false;
        var threshold = Math.Clamp(Setting("ideas.doneThreshold", DefaultDoneThreshold), 0.3, 0.99);
        if ((probs.TryGetValue("DONE", out var done) ? done : 0) < threshold) return false;
        if ((probs.TryGetValue("DONE", out var d) ? d : 0) <= (probs.TryGetValue("MORE", out var m) ? m : 0)) return false;

        var suggestion = new JsonObject
        {
            ["id"] = "sg_" + Guid.NewGuid().ToString("N")[..10],
            ["kind"] = "done",
            ["ideaId"] = id,
            ["title"] = IdeaOps.Str(idea["title"]) ?? "",
            ["commits"] = new JsonArray(linked.Select(c => (JsonNode)c!).ToArray()),
            ["at"] = IdeaOps.Now(),
            ["seen"] = false,
            ["project"] = watch.ProjectId is { Length: > 0 } pid
                ? new JsonObject { ["id"] = pid, ["name"] = watch.ProjectName }
                : null,
        };
        // One offer per idea: a second commit that also finishes it changes nothing the user has not answered.
        var added = false;
        await save.MutatePendingAsync<int>(f =>
        {
            var list = f["suggestions"]!.AsArray();
            if (list.OfType<JsonObject>().Any(s => IdeaOps.Str(s["ideaId"]) == id)) return 0;
            list.Add(suggestion);
            added = true;
            return 1;
        }, ctx.Stopping).ConfigureAwait(false);
        if (added) ctx.Events.Publish(IdeaSaveCheck.SuggestedEvent, new JsonObject { ["suggestion"] = suggestion.DeepClone() });
        return added;
    }

    // ------------------------------------------------------------------ the decisions

    /// <summary>One pick-one decision: label → probability, or null when the Decide plugin cannot answer.</summary>
    private async Task<Dictionary<string, double>?> DecideAsync(JsonObject system, string question, string[] labels)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
        cts.CancelAfter(IdeaSaveCheck.Timeout);
        try
        {
            var raw = await ctx.Rpc.InvokeAsync("decide.decision", new JsonObject
            {
                ["model"] = Setting("ideas.model", DefaultModel) is { Length: > 0 } m ? m.Trim() : DefaultModel,
                ["messages"] = new JsonArray(system),
                ["branches"] = new JsonArray(new JsonObject
                {
                    ["id"] = "pick",
                    ["content"] = question,
                    ["labels"] = new JsonArray(labels.Select(l => (JsonNode)l!).ToArray()),
                }),
            }, cts.Token).ConfigureAwait(false);
            var answer = raw as JsonObject ?? JsonSerializer.SerializeToNode(raw) as JsonObject;
            if (answer?["branches"] is not JsonArray { Count: > 0 } branches || branches[0]?["probabilities"] is not JsonObject probs) return null;
            var outp = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (label, node) in probs)
                if (node is JsonValue v && v.TryGetValue<double>(out var d)) outp[label] = d;
            return outp.Count == labels.Length ? outp : null;
        }
        catch (OperationCanceledException) when (!ctx.Stopping.IsCancellationRequested)
        {
            ctx.Logger.LogWarning("Ideas: the commit check got no answer within {Seconds:0} s.", IdeaSaveCheck.Timeout.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogWarning("Ideas: the commit check failed: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<List<JsonObject>> OpenAsync(string? projectId)
    {
        try
        {
            return await store.ReadAsync(locator.GlobalFile(), f => IdeaOps.All(f.Ideas)
                .Where(i => IdeaOps.Str(i["status"]) is not ("done" or "rejected"))
                .Where(i => IdeaOps.MatchesProject(i, projectId, includeUnbound: true))
                .Select(i => (JsonObject)i.DeepClone())
                .ToList()).ConfigureAwait(false);
        }
        catch (IdeasFileException ex) { throw new RpcException("invalid_file", ex.Message); }
    }

    // ------------------------------------------------------------------ the pending file (shared with the save check)

    /// <summary>The newest commit this plugin has read for a repository, remembered so a restart does not re-read history.</summary>
    private async Task<string?> LastSeenAsync(string repo)
    {
        if (_stopped) return null;
        return await save.MutatePendingAsync<string?>(f =>
            f["repos"] is JsonObject repos && repos[repo] is JsonObject r ? IdeaOps.Str(r["hash"]) : null, ctx.Stopping).ConfigureAwait(false);
    }

    private async Task RememberAsync(string repo, string? hash)
    {
        if (hash is not { Length: > 0 } || _stopped) return;
        await save.MutatePendingAsync<int>(f =>
        {
            if (f["repos"] is not JsonObject repos) f["repos"] = repos = new JsonObject();
            repos[repo] = new JsonObject { ["hash"] = hash, ["at"] = IdeaOps.Now() };
            var cutoff = DateTimeOffset.UtcNow.AddDays(-RepoKeepDays);
            foreach (var key in repos.Select(p => p.Key).ToList())
                if (repos[key] is JsonObject r && r["at"] is JsonValue v && v.TryGetValue<string>(out var at)
                    && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
                    && when < cutoff)
                    repos.Remove(key);
            return 0;
        }, ctx.Stopping).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    private T Setting<T>(string key, T fallback)
    {
        try { return ctx.Settings.Get(key, fallback) ?? fallback; }
        catch { return fallback; }
    }

    private static string OneLine(string? s) => string.Join(' ', (s ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
