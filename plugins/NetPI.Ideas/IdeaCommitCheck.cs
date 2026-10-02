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
/// Otherwise one pick-one decision links only a clear winner above <c>ideas.linkThreshold</c> with a margin over
/// other ideas and none. Named ids can link several ideas. Commit entries are stored beside the idea's text.</item>
/// <item><b>is the idea done?</b> Asked with the idea's full text (its plan and what is left) <em>and all its linked
/// commits</em>, not from one commit and a summary: 4/5 finished ideas offered, no offer on a commit that only advanced
/// its idea. A clear decision is independently verified against bounded complete patches and unchanged revision/activity.</item>
/// </list>
/// Verified unchanged completion applies when ideas.applyVerifiedUpdates is enabled; otherwise one verified card is
/// offered. A partial, uncertain or unverifiable proposal never closes an idea.
/// </summary>
public sealed class IdeaCommitCheck(IPluginContext ctx, IdeasRepository repo, IdeaSaveCheck save) : IDisposable
{
    public const string DefaultModel = "qwen3.8-27b";
    public const double DefaultLinkThreshold = 0.7;
    public const double DefaultDoneThreshold = 0.8;
    /// <summary>Commits read per page. A burst (a rebase, a merge train) is read oldest first, in bounded pages.</summary>
    public const int MaxCommits = 20;
    /// <summary>Pages one sweep reads, so 45 unseen commits are read in three pages and none of them is skipped.</summary>
    public const int MaxPages = 5;
    /// <summary>
    /// How many failed checks in a row a commit may cost before it is recorded unread and the cursor moves past it
    /// (idea-kooctc). A commit that cannot be decided must not pin the sweep — and every later commit of the
    /// repository — forever: the record stays in ideas_unread, and the later commits are read.
    /// </summary>
    public const int MaxTries = 5;
    /// <summary>The first backoff after a failed check; the wait doubles with every failed attempt and caps at an hour.</summary>
    public const int DefaultRetrySeconds = 120;
    private const int MaxBackoffSeconds = 3600;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Rescan = TimeSpan.FromMinutes(2);
    /// <summary>How long a repository's last-seen commit is remembered; an untouched repository is dropped from the file.</summary>
    private const int RepoKeepDays = 60;
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private readonly IdeaAdmission _admission = new(ctx);
    private readonly IdeasRepository _repo = repo;
    private readonly Lock _watchLock = new();
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.OrdinalIgnoreCase); // repo path → its watcher
    private Timer? _timer;
    private IDisposable? _runEnds;
    private volatile bool _stopped;
    private int _rescanRunning;
    private volatile bool _rescanQueued;
    private bool _disposed;

    private sealed class Watch(string repo, string path, string? projectId, string? projectName) : IDisposable
    {

        public string Repo { get; } = repo;
        public string Path { get; } = path;
        public string? ProjectId { get; } = projectId;
        public string? ProjectName { get; } = projectName;
        /// <summary>The git directories being watched (one in a plain repository, two in a worktree).</summary>
        public List<FileSystemWatcher> Watchers { get; } = [];
        /// <summary>One sweep at a time, and the last one is not lost while it runs.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);
        /// <summary>The one-shot debounce timer, reused by every event (a burst re-arms it, not allocates it).</summary>
        public Timer? Timer { get; set; }
        public bool Again { get; set; }
        public void Dispose()
        {
            foreach (var w in Watchers) { try { w.Dispose(); } catch { } }
            Watchers.Clear();
            Gate.Dispose();
            Timer?.Dispose();
        }
    }

    // ------------------------------------------------------------------ watching

    /// <summary>Start watching every project that has a repository. Safe to call again: it re-reads the project list.</summary>
    public async Task StartAsync()
    {
        if (_stopped) return;
        _runEnds ??= ctx.Events.Subscribe(EventTypes.AgentStatus, e =>
        {
            var status = IdeaOps.Str(NetPiJson.ToNode(e.Data)?["agent"]?["status"]);
            if (status is "idle" or "completed" or "failed" or "cancelled") _ = RescanAsync();
        });
        _timer ??= new Timer(_ => _ = RescanAsync(), null, Rescan, Rescan);
        await RescanAsync().ConfigureAwait(false);
    }

    public void Stop()
    {
        _stopped = true;
        _timer?.Dispose();
        _timer = null;
        _runEnds?.Dispose();
        _runEnds = null;
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

    /// <summary>
    /// Every repository is watched *and* swept on a timer. The watcher is the fast path; the sweep is the floor, because
    /// a <see cref="FileSystemWatcher"/> can miss events (an internal-buffer overflow during a rebase, a watcher that
    /// goes quiet after its first burst) and a commit that is never noticed is worse than one noticed two minutes late.
    /// It also picks up a project that was added while NetPI was running.
    /// </summary>
    private async Task RescanAsync()
    {
        if (_stopped) return;
        // Single-flight: one rescan at a time, at most one queued behind it. A trigger landing while the previous rescan
        // is still sweeping would otherwise run a second full re-sweep in parallel — a duplicate git spawn per repository
        // and a duplicate pass over the pending file, for nothing.
        if (Interlocked.Exchange(ref _rescanRunning, 1) == 1)
        {
            _rescanQueued = true;
            return;
        }
        try
        {
            do
            {
                _rescanQueued = false;
                if (_stopped) break;
                if (!Setting("ideas.closeOnCommit", true)) break;
                if (!(ctx.Services.Get<IGitHistory>() is not null || ctx.Rpc.Exists("files.commits"))) break;   // no Files plugin: nothing can read the commits
                var projects = ctx.Sessions.ListProjects();
                foreach (var project in projects)
                {
                    if (_stopped) break;
                    try { await WatchProjectAsync(project).ConfigureAwait(false); }
                    catch (Exception ex) { ctx.Logger.LogWarning(ex, "Ideas: cannot watch the repository of project {Project}", project.Name); }
                }
                DropStale(projects);
                ForgetStale();
                foreach (var watch in Watches())
                {
                    if (_stopped) break;
                    await watch.Gate.WaitAsync(ctx.Stopping).ConfigureAwait(false);
                    try { await SweepAsync(watch).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ctx.Stopping.IsCancellationRequested) { }
                    catch (Exception ex) { ctx.Logger.LogWarning("Ideas: the commit check on {Repo} failed: {Message}", watch.Repo, ex.Message); }
                    finally { watch.Gate.Release(); }
                }
            }
            while (_rescanQueued);
        }
        finally
        {
            Interlocked.Exchange(ref _rescanRunning, 0);
            // A trigger that landed between the last while-check and the clear above saw the rescan as running, parked
            // itself in _rescanQueued, and would otherwise have to wait for the next two-minute tick.
            if (_rescanQueued) { _rescanQueued = false; _ = RescanAsync(); }
        }
    }

    /// <summary>
    /// Forget a repository whose project is gone (deleted, renamed or moved). It keeps a watcher and a file handle open
    /// for a repository nothing in this NetPI is working on any more.
    /// </summary>
    private void DropStale(IReadOnlyList<ProjectInfo> projects)
    {
        lock (_watchLock)
        {
            foreach (var (repo, watch) in _watches.ToArray())
                if (projects.All(p => !string.Equals(p.Path, watch.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    _watches.Remove(repo);
                    watch.Dispose();
                    ctx.Logger.LogDebug("Ideas: no longer watching {Repo} (its project is gone)", repo);
                }
        }
    }

    /// <summary>Read every watched repository once, now (the periodic sweep does this; the tests drive it directly).</summary>
    internal async Task SweepNowAsync()
    {
        // The same lifecycle a scheduled sweep applies: pick up projects that are not watched yet, forget the ones
        // that are gone, then read every repository once.
        var projects = ctx.Sessions.ListProjects();
        foreach (var project in projects)
        {
            try { await WatchProjectAsync(project).ConfigureAwait(false); }
            catch (Exception ex) { ctx.Logger.LogWarning("Ideas: cannot watch the repository of project {Project}: {Message}", project.Name, ex.Message); }
        }
        DropStale(projects);
        foreach (var watch in Watches())
        {
            await watch.Gate.WaitAsync(ctx.Stopping).ConfigureAwait(false);
            try { await SweepAsync(watch).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Logger.LogWarning("Ideas: the commit check on {Repo} failed: {Message}", watch.Repo, ex.Message); }
            finally { watch.Gate.Release(); }
        }
    }

    private Watch[] Watches()
    {
        lock (_watchLock) return [.. _watches.Values];
    }

    private async Task WatchProjectAsync(ProjectInfo project)
    {
        if (!Directory.Exists(project.Path)) return;
        // A project that already has a live watcher has had its repository asked about once: do not spawn git per rescan
        // to re-ask (the two-minute timer would pay a git log per watched project for nothing).
        if (Watches().Any(w => string.Equals(w.Path, project.Path, StringComparison.OrdinalIgnoreCase))) return;
        var found = await AskCommitsAsync(project.Path, null, null, 1).ConfigureAwait(false);
        // Through ToNode: an RPC answers with the handler's own object in-process and with its JSON over HTTP, and the
        // Files plugin answers with a record (like decide.decision's readers here, and for the same reason).
        var o = NetPiJson.ToNode(found) as JsonObject;
        if (IdeaOps.Str(o?["repo"]) is not { Length: > 0 } repo)
        {
            ctx.Logger.LogDebug("Ideas: {Path} is not in a git repository (or files.commits is unavailable)", project.Path);
            return;
        }
        var newest = (o?["commits"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        // Only a repository this plugin has never read is anchored at HEAD. A stored cursor is the progress made before
        // the restart, and overwriting it would skip every commit that was made while NetPI was closed.
        _repo.RememberIfAbsent(repo, IdeaOps.Str(newest?["hash"]), project.Id, project.Name);

        lock (_watchLock)
        {
            if (_stopped || _watches.ContainsKey(repo)) return;
            var watch = new Watch(repo, project.Path, project.Id, project.Name);
            // The watch is registered before, and even without, a file watcher: the periodic sweep is the floor, so a
            // repository whose git directory cannot be watched (a worktree, where .git is a file) is still read.
            ctx.Logger.LogInformation("Ideas: watching {Repo} for commits (project {Project})", repo, project.Name);

            // The whole git directory, subdirectories included: the reflog (.git/logs/HEAD) only exists once a
            // repository has a first commit, so watching *it* would miss every commit of a fresh one. Any write here
            // (an index, a ref, the reflog) wakes the sweep, which only acts on commits it has not seen. A worktree has
            // two of them: its own (HEAD) and the common one where the refs live.
            foreach (var dir in new[] { IdeaOps.Str(o?["gitDir"]), IdeaOps.Str(o?["commonDir"]) }
                .OfType<string>()
                .Where(d => d.Length > 0 && Directory.Exists(d))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var fs = new FileSystemWatcher(dir)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        IncludeSubdirectories = true,
                    };
                    fs.Changed += (_, _) => Schedule(watch);
                    fs.Created += (_, _) => Schedule(watch);
                    fs.Renamed += (_, _) => Schedule(watch);
                    fs.Deleted += (_, _) => Schedule(watch);
                    fs.EnableRaisingEvents = true;
                    watch.Watchers.Add(fs);
                }
                catch (Exception ex)
                {
                    // A warning, not a debug line: the sweep still runs, so this costs speed, not commits.
                    ctx.Logger.LogWarning(ex, "Ideas: cannot watch the git directory {Git} of project {Project} (its commits are still read every {Minutes} min)", dir, project.Name, Rescan.TotalMinutes);
                }
            }
            _watches[repo] = watch;
        }
    }

    /// <summary><c>files.commits</c> as the Files plugin answers it (a record, read through JSON).</summary>
    private async Task<object?> AskCommitsAsync(string path, string? since, string? until, int limit)
    {
        var request = new JsonObject { ["cwd"] = path, ["limit"] = limit };
        if (since is { Length: > 0 }) request["since"] = since;
        if (until is { Length: > 0 }) request["until"] = until;
        if (ctx.Services.Get<IGitHistory>() is { } history)
            return await history.ReadAsync(request, ctx.Stopping).ConfigureAwait(false);
        return await ctx.Rpc.InvokeAsync("files.commits", request, ctx.Stopping).ConfigureAwait(false);
    }

    /// <summary>
    /// Debounced: a commit writes the reflog a few times, and a burst is one sweep. One timer per repository, re-armed by
    /// every event (reusing it would queue one sweep per write; allocating one per event just adds garbage to a burst
    /// that already coalesces into the single sweep below).
    /// </summary>
    private void Schedule(Watch watch)
    {
        if (_stopped) return;
        watch.Timer ??= new Timer(_ => SweepDue(watch), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        watch.Timer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    private void SweepDue(Watch watch)
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
    }

    /// <summary>
    /// Read everything this repository has committed since the cursor, oldest first, in bounded pages.
    /// <para>
    /// The pages are fetched from HEAD downwards (<c>since..until</c>) and then read in the opposite order, because git
    /// answers a range with its <em>newest</em> commits: asking for one page of the newest and remembering the newest
    /// of that page silently drops everything older in the range, which is what lost commits in a burst of more than
    /// <see cref="MaxCommits"/>. Reading them oldest first also means the decisions see a burst in the order it happened.
    /// </para>
    /// <para>
    /// The cursor only moves past a commit that was handled. A decision that failed (the model is down, a timeout)
    /// stops the sweep with the cursor where it was — but it does not re-pay on every trigger that wakes the sweep
    /// (idea-kooctc): the failure is recorded on the repository's cursor, the attempts back off exponentially, and a
    /// commit that cannot be decided after <see cref="MaxTries"/> attempts is recorded unread and the cursor moves
    /// past it, so the later commits are read instead of being pinned behind it forever.
    /// </para>
    /// </summary>
    private async Task SweepAsync(Watch watch)
    {
        if (_stopped || !Setting("ideas.closeOnCommit", true) || IdeaRuns.ProjectBusy(ctx, watch.ProjectId)) return;
        // A check that just failed is not retried on every trigger that wakes the sweep (the two-minute timer, an
        // agent-status change, a burst of git file events): the attempts back off, and the setting names the first
        // interval (0 disables the backoff, so sweeps can be driven back to back).
        if (_repo.RepoFailure(watch.Repo) is { Tries: > 0, At: { } failedAt } failure)
        {
            var retryAt = BackoffUntil(failure.Tries, failedAt);
            if (retryAt is { } until && DateTimeOffset.UtcNow < until)
            {
                ctx.Logger.LogDebug("Ideas: {Repo} is not swept again until about {Until:HH:mm:ss} ({Tries} failed check(s) back it off)", watch.Repo, until, failure.Tries);
                return;
            }
        }
        var since = _repo.LastSeen(watch.Repo);
        var pages = new List<List<JsonObject>>();
        string? upper = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var found = await AskCommitsAsync(watch.Path, since, upper, MaxCommits).ConfigureAwait(false);
            var o = NetPiJson.ToNode(found) as JsonObject;
            var batch = (o?["commits"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (o is null || batch.Count == 0)
            {
                // The cursor is not in this history any more (a rebase, a branch switch, a replaced repository): the only
                // way forward is to start again at what is here now, and to say that the older history was not read.
                if (o?["reachable"] is JsonValue r && r.TryGetValue<bool>(out var reachable) && !reachable)
                {
                    var newest = await HeadAsync(watch).ConfigureAwait(false);
                    ctx.Logger.LogWarning("Ideas: the history of {Repo} was rewritten or the branch changed: the cursor is re-anchored at {Hash} and the older history is not read.",
                        watch.Repo, newest ?? "HEAD");
                    _repo.Remember(watch.Repo, newest, watch.ProjectId, watch.ProjectName);
                }
                break;
            }
            pages.Add(batch);
            if (batch.Count < MaxCommits) break;                 // that was everything below
            upper = IdeaOps.Str(batch[^1]["hash"]);              // the next page is what came before this one
        }

        // Oldest first: page by page from the bottom, each page in the order it was committed.
        var all = pages.SelectMany(p => p).Reverse().ToList();
        if (all.Count == 0)
        {
            ctx.Logger.LogDebug("Ideas: no new commit in {Repo}", watch.Repo);
            return;
        }
        ctx.Logger.LogInformation("Ideas: {Count} new commit(s) in {Repo} since {Since}", all.Count, watch.Repo, since ?? "(the start)");
        ctx.Logger.LogDebug("Ideas: the watcher is the fast path; a missed event is caught by the {Minutes}-minute sweep", Rescan.TotalMinutes);

        foreach (var commit in all)
        {
            if (_stopped || IdeaRuns.ProjectBusy(ctx, watch.ProjectId)) return;
            // The agent or another window may close/update an idea between commits or while a decision runs.
            var open = OpenAsync(watch.ProjectId, ctx.Stopping);
            var (handled, why) = await HandleAsync(watch, open, commit, ctx.Stopping).ConfigureAwait(false);
            if (handled)
                _repo.Remember(watch.Repo, IdeaOps.Str(commit["hash"]), watch.ProjectId, watch.ProjectName);
            else
            {
                // The check could not run (a drop, a timeout, an answer that did not come): the commit stays unread —
                // but it is not re-paid on every trigger (idea-kooctc). The failure is recorded on the cursor, the
                // attempts back off, and a commit that cannot be decided after its bound is recorded unread, so the
                // cursor moves past it and the later commits are read. (A skip is not a failure: it advanced the
                // cursor above, and only a drop lands here. — idea-np3a5g)
                var shortHash = IdeaOps.Str(commit["short"]) ?? Clip(IdeaOps.Str(commit["hash"]) ?? "?", 7);
                var reason = why ?? "a check failed";
                var tries = _repo.RecordCommitFailure(watch.Repo, reason);
                if (tries >= MaxTries)
                {
                    _repo.Remember(watch.Repo, IdeaOps.Str(commit["hash"]), watch.ProjectId, watch.ProjectName);
                    _repo.RecordUnread(watch.Repo, IdeaOps.Str(commit["hash"]) ?? "", IdeaOps.Str(commit["subject"]), tries, reason);
                    ctx.Logger.LogWarning("Ideas: {Short} in {Repo} is not read yet ({Reason}, and {Tries} checks in a row did not answer); it is recorded unread and the cursor moves past it, so the later commits are read. It is listed in ideas.unread.",
                        shortHash, watch.Repo, reason, tries);
                    return;
                }
                var retryIn = BackoffSeconds(tries);
                ctx.Logger.LogWarning("Ideas: {Short} in {Repo} is not read yet ({Reason}); attempt {Tries} of {Max}, and the next sweep tries it again in about {Seconds:0} s",
                    shortHash, watch.Repo, reason, tries, MaxTries, retryIn);
                return; // the cursor stays: this commit, and every one after it, are still unseen
            }
        }
    }

    /// <summary>
    /// The backoff after a failed check: the setting names the first interval, it doubles with every failed attempt
    /// and caps at an hour — so a check that fails every sweep costs one attempt per backoff, not one per trigger.
    /// 0 (the setting) means no backoff at all.
    /// </summary>
    private int BackoffSeconds(long tries)
    {
        var baseSeconds = Setting("ideas.commitRetrySeconds", DefaultRetrySeconds);
        if (baseSeconds <= 0) return 0;
        var shift = (int)Math.Min(tries - 1, 20);
        return Math.Min(baseSeconds * (1 << shift), MaxBackoffSeconds);
    }

    /// <summary>When the next attempt at a repository that just failed <paramref name="tries"/> checks is due (null: now).</summary>
    private DateTimeOffset? BackoffUntil(long tries, string? at)
    {
        var seconds = BackoffSeconds(tries);
        if (seconds <= 0) return null;
        if (DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            return when.AddSeconds(seconds);
        return null; // an unreadable stamp: retry now rather than guess
    }

    // ------------------------------------------------------------------ one commit

    /// <summary>
    /// One commit: which idea it is about, recorded on it, and whether the idea looks finished. <c>(false, why)</c>
    /// when the work could not be carried out (a drop, a timeout), so the caller leaves the cursor where it is and
    /// tries this commit again; <c>true</c> when it is handled, including "it is about nothing here" and a skip — a
    /// skip is a decision the configuration will make again on every retry, so it is not one (idea-np3a5g).
    /// </summary>
    private async Task<(bool Handled, string? Why)> HandleAsync(Watch watch, List<JsonObject> open, JsonObject commit, CancellationToken ct)
    {
        var hash = IdeaOps.Str(commit["hash"]);
        if (hash is not { Length: > 0 }) return (true, null);  // nothing to remember it by, and nothing to do about it
        var subject = IdeaOps.Str(commit["subject"]) ?? "";
        if (open.Count == 0) return (true, null);

        // 1. which idea is this commit about
        var linked = new List<JsonObject>();
        foreach (var idea in NamedIn(subject, open)) linked.Add(idea);   // deterministic: the message names the id
        if (linked.Count == 0)
        {
            var decision = await LinkAsync(open, commit, watch.ProjectId, ctx.Stopping).ConfigureAwait(false);
            if (decision is { Skipped: { } skipped })
            {
                // (idea-np3a5g) A skip (no model, or a paid one the user did not allow) will not change on a retry,
                // so the commit is read as "not linked" and the cursor moves past it. The same skip at the done
                // question already counted as processed: a skip is a decision on both questions, and only a drop
                // is retried. The log line says what was skipped and why, so it is never silent.
                ctx.Logger.LogWarning("Ideas: {Short} in {Repo} is not linked: {Reason} — the cursor moves past it, and the skip is not retried.",
                    IdeaOps.Str(commit["short"]) ?? Clip(IdeaOps.Str(commit["hash"]) ?? "?", 7), watch.Repo, skipped);
                return (true, null);
            }
            if (decision is { Failed: { } failed }) return (false, failed);   // the decision did not answer: this commit is still unseen
            linked.AddRange(decision.Picks ?? []);
        }
        if (linked.Count == 0) return (true, null);

        var entry = new JsonObject
        {
            ["hash"] = hash,
            ["short"] = IdeaOps.Str(commit["short"]) ?? hash[..Math.Min(7, hash.Length)],
            ["subject"] = Clip(subject, 200),
            ["at"] = commit["at"]?.DeepClone() ?? IdeaOps.Now(),
        };
        var titles = new List<string>();
        foreach (var idea in linked) titles.Add(IdeaOps.Str(idea["title"]) ?? "?");

        // Every commit entry in one transaction (one statement per idea), and the fresh copies the "is it done"
        // question below needs come out of it: a re-read per idea would be a second round trip for what the write
        // already knows.
        if (linked.Any(i => IdeaOps.Str(i["id"]) is { Length: > 0 }))
        {
            var fresh = _repo.AddCommitEntries(linked.Select(i => IdeaOps.Str(i["id"])), entry);

            // 2. is any of them done: the idea's full text and every commit linked to it, not this one alone
            foreach (var idea in fresh)
            {
                if (_stopped) return (true, null);
                if (IdeaOps.Str(idea["status"]) is not ("open" or "planned" or "in-progress" or "parked")) continue;
                var offered = await OfferDoneAsync(watch, idea, entry, titles, ct).ConfigureAwait(false);
                if (offered is null) return (false, "the done question could not run yet"); // changed or busy: leave this commit for the next sweep
                if (offered == true) break;
            }
        }
        return (true, null);
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

    /// <summary>
    /// The link question's outcome: the picks, or why there are none. <c>Skipped</c> is a decision the current
    /// configuration will make again on every retry (no model, or a paid one the user did not allow), so the caller
    /// moves past the commit instead of retrying it; <c>Failed</c> is a transient "no answer" (a drop, a timeout) and
    /// the caller leaves the cursor where it is (idea-np3a5g). Neither field set is the decision with no pick.
    /// </summary>
    private sealed record Decision(List<JsonObject>? Picks, string? Skipped, string? Failed)
    {
        internal static Decision None => new([], null, null);
    }

    /// <summary>
    /// Which open idea the commit is about. A commit message that names one or more idea ids links exactly those
    /// (deterministic, no model). Otherwise one pick-one decision, and <b>only its best option</b> is linked: the answer
    /// is a distribution over mutually exclusive options, so "everything above 0.7" would claim several ideas for one
    /// commit on the strength of probabilities that were computed to compete with each other. A commit that really
    /// finishes two ideas names both ids, or gets linked to the one it names best.
    /// </summary>
    private async Task<Decision> LinkAsync(List<JsonObject> open, JsonObject commit, string? projectId, CancellationToken ct)
    {
        if (!DecisionCapabilities.Available(ctx.Services, ctx.Rpc, "decide.decision")) return Decision.None;
        var subject = IdeaOps.Str(commit["subject"]) ?? "";
        var author = IdeaOps.Str(commit["author"]) ?? "";
        var threshold = Math.Clamp(Setting("ideas.linkThreshold", DefaultLinkThreshold), 0.3, 0.99);
        List<JsonObject>? best = null;
        foreach (var window in IdeaMatch.Windows(open, subject + " " + author))
        {
            var list = IdeaMatch.Options(window, "none of these: this commit is about something else", out var none, out var labels);
            var decision = await DecideAsync(new JsonObject
            {
                ["role"] = "system",
                ["content"] = "You decide which of the user's backlog of open ideas a git commit was about, so the commit can be " +
                              "recorded on it. Answer with the letter of the best option only.\n\n" +
                              $"Commit by {author}:\n{subject}\n\nOpen ideas:\n" + list,
            }, $"Which open idea is this commit about? Pick it when the commit works on it (implements it, or a step of it, " +
               $"or fixes it), pick it even when it only advances the idea. Pick {none} when it is about something else.",
                labels, "the link question", projectId, ct).ConfigureAwait(false);
            if (decision is { Skipped: not null } or { Failed: not null }) return new Decision(null, decision.Skipped, decision.Failed); // one commit, one question
            var pick = IdeaMatch.Pick(decision.Probs!, labels.OfType<JsonValue>().Select(v => IdeaOps.Str(v) ?? "").ToList());
            if (!pick.Clear(threshold)) break;   // no clear winner
            best = [window[pick.Index]];
            break;                                                          // one commit, one idea
        }
        return new(best ?? [], null, null);
    }

    /// <summary>
    /// "Is this idea finished?" — asked with the idea's full text and every commit linked to it, because asking from one
    /// commit and the summary alone offered only 5/12 (docs/DECISION-MODELS.md). On a clear margin a card is added; one per
    /// idea, and never for an idea that is already closed.
    /// </summary>
    private async Task<bool?> OfferDoneAsync(Watch watch, JsonObject idea, JsonObject commit, List<string> committed, CancellationToken ct)
    {
        if (!DecisionCapabilities.Available(ctx.Services, ctx.Rpc, "decide.decision")) return false;
        var id = IdeaOps.Str(idea["id"]);
        if (id is not { Length: > 0 }) return false;
        if (IdeaRuns.ProjectBusy(ctx, watch.ProjectId)) return null;
        var revision = idea["revision"]?.GetValue<long>();
        if (revision is null) return false;
        var text = IdeaOps.RenderMarkdown(idea);
        if (text.Length < 40) return false;
        var linked = (idea["commits"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => $"{IdeaOps.Str(c["short"])} {IdeaOps.Str(c["subject"])}").ToList();
        if (linked.Count == 0) return false;

        var decision = await DecideAsync(new JsonObject
        {
            ["role"] = "system",
            ["content"] = "You decide whether a piece of work is finished, given the plan it belongs to and the commits " +
                          "that were made for it. Answer DONE only when everything the idea asked for is built and nothing " +
                          "of it is left to do. Answer MORE when the commits advance the idea but part of it is still to do, " +
                          "or when it is not clear.\n\n" +
                          $"Idea: {text}\n\nCommits linked to it:\n" + string.Join('\n', linked.Select((s, k) => $"{k + 1}. {s}")),
        }, $"Is the idea finished now? These commits were just made for it ({string.Join(", ", committed)}).",
            new JsonArray("DONE", "MORE"), "the done question", watch.ProjectId, ct).ConfigureAwait(false);
        // The done question is the one-card courtesy, not the commit's reading: a skip (a decision the configuration
        // will make again — idea-np3a5g) and a drop alike mean "no offer now", and the cursor moves either way.
        if (decision.Probs is not { } probs) return false;
        var threshold = Math.Clamp(Setting("ideas.doneThreshold", DefaultDoneThreshold), 0.3, 0.99);
        if (!DecisionConfidence.Clear(probs.GetValueOrDefault("DONE"), probs.GetValueOrDefault("MORE"), threshold)) return false;
        // The result judges this exact snapshot. A later revision must be judged again, never stamped onto old text.
        var current = _repo.Find(id);
        if (current is null || IdeaOps.Str(current.Doc["status"]) is not ("open" or "planned" or "in-progress" or "parked")) return false;
        if (IdeaRuns.ProjectBusy(ctx, watch.ProjectId) || current.Revision != revision) return null;

        var patches = new StringBuilder();
        var hashes = (idea["commits"] as JsonArray ?? []).OfType<JsonObject>().Select(c => IdeaOps.Str(c["hash"])).OfType<string>().Distinct().ToList();
        if (hashes.Count is 0 or > 10)
        {
            var work = ctx.Services.Get<IBackgroundWork>();
            var skipped = work?.Begin("Completion verification", null, null, watch.ProjectId);
            if (skipped is not null) work!.Set(skipped, "skipped", "Complete evidence exceeds the automatic verification bound; review this idea manually");
            return false;
        }
        foreach (var hash in hashes)
        {
            var request = new JsonObject { ["cwd"] = watch.Path, ["hash"] = hash };
            var raw = ctx.Services.Get<IGitHistory>() is { } history ? await history.ReadAsync(request, ct).ConfigureAwait(false)
                : NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("files.commits", request, ct).ConfigureAwait(false)) as JsonObject;
            if (raw?["patch"]?.GetValue<string>() is not { Length: > 0 } patch) return null;
            if (raw["truncated"]?.GetValue<bool>() == true) { ReportEvidenceBound(); return false; }
            patches.AppendLine(patch);
            if (patches.Length > 64000) { ReportEvidenceBound(); return false; }
        }
        var verdict = await new IdeaVerifier(ctx).VerifyAsync("Mark the following idea done:\n" + text,
            "Linked commits:\n" + string.Join('\n', linked) + "\n\nCommit patches:\n" + patches, null, watch.ProjectId, ct).ConfigureAwait(false);
        if (!verdict.Verified) return verdict.Retryable ? null : false;
        current = _repo.Find(id);
        if (current is null || IdeaRuns.ProjectBusy(ctx, watch.ProjectId) || current.Revision != revision) return null;
        if (Setting("ideas.applyVerifiedUpdates", true))
        {
            _repo.Update(id, new JsonObject { ["status"] = "done", ["addSections"] = new JsonArray(new JsonObject
                { ["kind"] = "research", ["title"] = "Completion verification", ["content"] = verdict.Reason + "\n\n" + string.Join('\n', linked) }) },
                fromUi: true, expectedRevision: revision);
            return true;
        }

        var suggestion = new JsonObject
        {
            ["id"] = "sg_" + Guid.NewGuid().ToString("N")[..10],
            ["kind"] = "done",
            ["ideaId"] = id,
            ["ideaRevision"] = revision.Value,
            ["title"] = IdeaOps.Str(idea["title"]) ?? "",
            ["commits"] = new JsonArray(linked.Select(c => (JsonNode)c!).ToArray()),
            ["at"] = IdeaOps.Now(),
            ["seen"] = false,
            ["verified"] = true,
            ["verification"] = verdict.Reason,
            ["project"] = watch.ProjectId is { Length: > 0 } pid
                ? new JsonObject { ["id"] = pid, ["name"] = watch.ProjectName }
                : null,
        };
        // One offer per idea: a second commit that also finishes it changes nothing the user has not answered.
        return save.Offer(suggestion);

        void ReportEvidenceBound()
        {
            var work = ctx.Services.Get<IBackgroundWork>();
            var skipped = work?.Begin("Completion verification", null, null, watch.ProjectId);
            if (skipped is not null) work!.Set(skipped, "skipped", "Commit patches exceed the automatic verification bound; review this idea manually");
        }
    }

    // ------------------------------------------------------------------ the decisions

    /// <summary>The newest commit of a repository (what a re-anchored cursor is set to).</summary>
    private async Task<string?> HeadAsync(Watch watch)
    {
        var o = NetPiJson.ToNode(await AskCommitsAsync(watch.Path, null, null, 1).ConfigureAwait(false)) as JsonObject;
        var newest = (o?["commits"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault();
        return IdeaOps.Str(newest?["hash"]);
    }

    /// <summary>
    /// The outcome of one pick-one decision: the probabilities, or why there are none. A <b>skip</b> is a decision the
    /// current configuration will make again on every retry (no model, or a paid one the user did not allow); a
    /// <b>fail</b> is a transient "no answer" (a drop, a timeout, an answer that did not come back shaped)
    /// (idea-np3a5g).
    /// </summary>
    private sealed record Answer(Dictionary<string, double>? Probs, string? Skipped, string? Failed)
    {
        public static Answer Skip(string reason) => new(null, reason, null);
        public static Answer Fail(string reason) => new(null, null, reason);
    }

    /// <summary>One pick-one decision, or why it did not answer.</summary>
    private async Task<Answer> DecideAsync(JsonObject system, string question, JsonArray labels, string purpose, string? project, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
        cts.CancelAfter(IdeaSaveCheck.Timeout);
        try
        {
            var name = Setting("ideas.model", DefaultModel) is { Length: > 0 } m ? m.Trim() : DefaultModel;
            // The same admission as a chat's: the decision shares the backend, so it waits its turn like one.
            var model = await ctx.Models.FindAsync(name, ct).ConfigureAwait(false);
            // The same admission as a chat's: the decision shares the backend, so it waits its turn like one. A drop is
            // the queue, and the same question can be asked again. A skip (no model, or a paid one the user did not
            // allow) will not change on a retry, so it is a decision, not a failure (idea-np3a5g).
            var admission = await _admission.EnterAsync(model, purpose, sessionId: null, projectId: project, ct).ConfigureAwait(false);
            if (!admission.Admitted)
                return admission.Retryable
                    ? Answer.Fail(admission.Reason ?? "the queue was full")
                    : Answer.Skip(admission.Reason ?? "the model is not available");
            using var slot = admission.Lease!;
            var raw = await DecisionCapabilities.InvokeAsync(ctx.Services, ctx.Rpc, "decide.decision", new JsonObject
            {
                ["model"] = name,
                ["messages"] = new JsonArray(system),
                ["branches"] = new JsonArray(new JsonObject
                {
                    ["id"] = "pick",
                    ["content"] = question,
                    ["labels"] = labels,
                }),
            }, cts.Token, admission.Slot, model?.Ref, IdeaAdmission.Priority).ConfigureAwait(false);
            var answer = raw as JsonObject ?? JsonSerializer.SerializeToNode(raw) as JsonObject;
            if (answer?["branches"] is not JsonArray { Count: > 0 } branches || branches[0]?["probabilities"] is not JsonObject probs)
                return Answer.Fail("the decision did not come back as a branch of probabilities");
            var outp = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (label, node) in probs)
                if (node is JsonValue v && v.TryGetValue<double>(out var d)) outp[label] = d;
            return outp.Count == labels.Count ? new Answer(outp, null, null) : Answer.Fail("a partial answer is not an answer");
        }
        catch (OperationCanceledException) when (!ctx.Stopping.IsCancellationRequested)
        {
            ctx.Logger.LogWarning("Ideas: the commit check got no answer within {Seconds:0} s.", IdeaSaveCheck.Timeout.TotalSeconds);
            return Answer.Fail($"no answer within {IdeaSaveCheck.Timeout.TotalSeconds:0} s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogWarning("Ideas: the commit check failed: {Message}", ex.Message);
            return Answer.Fail(ex.Message);
        }
    }

    /// <summary>The open ideas a commit is linked against: the project's and the unbound ones, in backlog order.</summary>
    private List<JsonObject> OpenAsync(string? projectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _repo.OpenIdeas(projectId);
    }

    // ------------------------------------------------------------------ the cursors (shared with the save check)

    /// <summary>How long a repository's last-seen commit is remembered.</summary>
    private void ForgetStale() => _repo.ForgetStaleRepos(RepoKeepDays);

    // ------------------------------------------------------------------ helpers

    private T Setting<T>(string key, T fallback)
    {
        try { return ctx.Settings.Get(key, fallback) ?? fallback; }
        catch { return fallback; }
    }

    private static string OneLine(string? s) => string.Join(' ', (s ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
