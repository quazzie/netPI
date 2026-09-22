using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.BackgroundTasks;

/// <summary>
/// One live background job: the process, its captured (bounded) output, and
/// state (PLAN §28). Owned entirely by the BackgroundTasks plugin once started
/// — reloading the shell plugin that resolved it does not affect it.
/// </summary>
internal sealed class BackgroundJob
{
    /// <summary>Maximum characters of output retained per job (PLAN §28: bounded tail).</summary>
    internal const int MaxOutputChars = 256_000;

    public required string JobId { get; init; }
    public required string ShellId { get; init; }
    public required string Command { get; init; }
    /// <summary>astra-1 H: ownership + original workdir, FROZEN at start (a
    /// later project switch must not relabel an existing process).</summary>
    public JobOwnership? Ownership { get; set; }
    public string? WorkingDirectory { get; set; }
    public Process? Process { get; set; }
    public BackgroundJobState State { get; set; } = BackgroundJobState.Running;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// astra-1 P5: self-lease on the owning generation — while a job is RUNNING,
    /// the generation cannot be reloaded (the reload's drain waits for every
    /// job to exit; killing the job releases it). Disposed in <c>OnExited</c>.
    /// </summary>
    public IValueLease<object>? JobLease { get; set; }
    public DateTimeOffset? ExitedAt { get; set; }
    public int? ExitCode { get; set; }

    // Bounded output ring: oldest chars are discarded once the cap is hit.
    private readonly StringBuilder _output = new();
    private int _dropped; // chars discarded from the front

    public void AppendOutput(string text)
    {
        lock (_output)
        {
            _output.Append(text);
            if (_output.Length > MaxOutputChars)
            {
                _dropped += _output.Length - MaxOutputChars;
                _output.Remove(0, _output.Length - MaxOutputChars);
            }
        }
    }

    public (string Text, int NextOffset, bool Truncated) Slice(int offset)
    {
        lock (_output)
        {
            var total = _dropped + _output.Length;
            if (offset >= total) return (string.Empty, total, false);
            if (offset <= _dropped)
            {
                // requested start is (partly) lost: serve from the earliest kept char.
                // (also the from-front path GetTailAsync uses to learn the total)
                return (_output.ToString(), _dropped + _output.Length, offset < _dropped);
            }
            int local = offset - _dropped;
            int take = Math.Min(64_000, _output.Length - local);
            // astra-1 H: slice straight off the buffer (StringBuilder.ToString
            // builds only the slice — no full ToString() copy before slicing,
            // PLAN §10: remove that unnecessary allocation).
            return (_output.ToString(local, take), offset + take, false);
        }
    }

    public BackgroundJobInfo ToInfo() => new(
        JobId, ShellId, Command, State, StartedAt, ExitedAt, ExitCode, null,
        SessionId: Ownership?.SessionId,
        RunId: Ownership?.RunId,
        ProjectId: Ownership?.ProjectId,
        WorkingDirectory: WorkingDirectory);

    public void Kill()
    {
        try
        {
            if (Process is { } p && !p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                try { p.WaitForExit(3000); } catch { }
            }
        }
        catch { /* best-effort */ }
        if (State != BackgroundJobState.Killed)
        {
            State = BackgroundJobState.Killed;
            ExitedAt ??= DateTimeOffset.UtcNow;
        }
    }
}

/// <summary>
/// The background job manager (PLAN §27/§28). Starts processes via a briefly
/// leased shell resolver, captures output into a bounded tail, and owns the
/// process tree for its lifetime. Jobs are runtime-only (no restart survival).
/// </summary>
public sealed class BackgroundJobManager : IBackgroundJobManager
{
    private readonly IPluginContext _ctx;
    private readonly Dictionary<string, BackgroundJob> _jobs = new();
    private readonly object _gate = new();
    private int _counter;

    /// <summary>astra-1 H: bounded RECENT-COMPLETIONS retention — keep at most this
    /// many jobs total (running always kept); the oldest EXITED jobs are pruned first.
    /// Opening a job beyond the window fails "no such job" (output was on the bounded
    /// ring anyway, so nothing is retained past this without a live process).</summary>
    private int _maxRetainedJobs;

    public BackgroundJobManager(IPluginContext ctx) : this(ctx, 64) { }
    // astra-1 H: injectable cap (tests drive pruning deterministically).
    internal BackgroundJobManager(IPluginContext ctx, int maxRetainedJobs)
    {
        _ctx = ctx;
        _maxRetainedJobs = maxRetainedJobs;
    }

    public async ValueTask<BackgroundJobInfo> StartAsync(
        string shellId, string command, string workingDirectory,
        JsonElement? options, JobOwnership? ownership = null,
        CancellationToken cancellationToken = default)
    {
        // Briefly lease the shell resolver (PLAN §27), then own the process.
        // The lease is disposed at the end of the using block so that a reload
        // of the tools plugin is only deferred for the duration of the resolve.
        await using var lease = AcquireResolver(shellId);
        var resolver = lease.Value;
        ResolvedCommand rc = await resolver.ResolveAsync(command, workingDirectory, cancellationToken);

        var job = new BackgroundJob
        {
            JobId = NewId(),
            ShellId = shellId,
            Command = command,
            // astra-1 H: ownership FROZEN at start (a later project switch never
            // relabels this process); the working directory is the RESOLVED one —
            // the authoritative original directory, not the caller's relative hint.
            Ownership = ownership,
            WorkingDirectory = rc.WorkingDirectory,
        };

        var psi = new ProcessStartInfo(rc.FileName, rc.Arguments)
        {
            WorkingDirectory = rc.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (rc.Environment is not null)
            foreach (var kv in rc.Environment) psi.Environment[kv.Key] = kv.Value;

        var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) job.AppendOutput(e.Data + Environment.NewLine);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) job.AppendOutput("[stderr] " + e.Data + Environment.NewLine);
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        // Send EOF on stdin so interactive shells do not linger waiting for input.
        try { proc.StandardInput.Close(); } catch { }
        // Track exit reliably (with redirected streams the process is reported
        // exited only once output/error reads drain) and update job state.
        _ = proc.WaitForExitAsync().ContinueWith(_ => OnExited(job), TaskScheduler.Default);
        job.Process = proc;

        // astra-1 P5: keep the owning generation alive while the job runs —
        // a reload of BackgroundTasks is DEFERRED (drain) until this job
        // exits, instead of silently killing it. Unadmitted (the generation
        // is already draining) the lease is untracked and the job still runs.
        job.JobLease = _ctx.LeaseSelf();

        lock (_gate) _jobs[job.JobId] = job;
        _ctx.Log.Information($"background start {job.JobId} [{shellId}] pid={proc.Id}: {command}");
        return job.ToInfo();
    }


    public ValueTask<BackgroundJobInfo?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var job = _jobs.GetValueOrDefault(jobId);
            return ValueTask.FromResult<BackgroundJobInfo?>(job?.ToInfo());
        }
    }

    public ValueTask<IReadOnlyList<BackgroundJobInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<BackgroundJobInfo>>(_jobs.Values.Select(j => j.ToInfo()).ToList());
    }

    public ValueTask<bool> KillAsync(string jobId, CancellationToken cancellationToken = default)
    {
        BackgroundJob? job;
        lock (_gate) job = _jobs.GetValueOrDefault(jobId);
        if (job is null) return ValueTask.FromResult(false);
        job.Kill();
        _ctx.Log.Information($"background kill {jobId}");
        return ValueTask.FromResult(true);
    }

    public ValueTask<BackgroundJobOutput> GetOutputAsync(
        string jobId, int offset = 0, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var job = _jobs.GetValueOrDefault(jobId);
            if (job is null)
                return ValueTask.FromResult(new BackgroundJobOutput(jobId, BackgroundJobState.Failed, null, "no such job", offset, false));
            var (text, next, truncated) = job.Slice(offset);
            return ValueTask.FromResult(new BackgroundJobOutput(
                jobId, job.State, job.ExitCode, text, next, truncated));
        }
    }

    /// <summary>
    /// The last <paramref name="chars"/> characters of a job's captured output —
    /// the live tail the right-panel page renders. The bounded ring may have
    /// already dropped older chars; the returned text is always ≤ chars.
    /// </summary>
    internal ValueTask<BackgroundJobOutput> GetTailAsync(string jobId, int chars, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var job = _jobs.GetValueOrDefault(jobId);
            if (job is null)
                return ValueTask.FromResult(new BackgroundJobOutput(jobId, BackgroundJobState.Failed, null, "no such job", 0, false));
            var (_, total, _) = job.Slice(0);
            var (text, next, truncated) = job.Slice(Math.Max(0, total - chars));
            if (text.Length > chars) text = text.Substring(text.Length - chars);
            return ValueTask.FromResult(new BackgroundJobOutput(
                jobId, job.State, job.ExitCode, text, next, truncated || total > chars));
        }
    }

    /// <summary>On plugin stop/shutdown: kill all remaining jobs (PLAN §28).</summary>
    internal void StopAll()
    {
        List<BackgroundJob> jobs;
        lock (_gate) jobs = _jobs.Values.ToList();
        foreach (var j in jobs) j.Kill();
        _ctx.Log.Information($"background shutdown: killed {jobs.Count} job(s)");
    }

    private void OnExited(BackgroundJob job)
    {
        try
        {
            int code = job.Process?.ExitCode ?? -1;
            job.ExitCode = code;
            if (job.State != BackgroundJobState.Killed)
                job.State = code == 0 ? BackgroundJobState.Succeeded : BackgroundJobState.Failed;
            job.ExitedAt ??= DateTimeOffset.UtcNow;
        }
        catch { /* process may be gone */ }
        // astra-1 P5: the job no longer runs — release the generation lease so
        // a (deferred) reload can proceed.
        try { job.JobLease?.Dispose(); } catch { /* best-effort */ }
        job.JobLease = null;
        _ctx.Log.Information($"background {job.JobId} exited code={job.ExitCode}");
        PruneRetainedJobs();
    }

    /// <summary>astra-1 H: keep the table bounded. Running jobs are never pruned;
    /// once over the retention cap, drop the oldest EXITED jobs (by exit
    /// time, then start time) until back under the cap. Best-effort — a reload's drain
    /// only tracks RUNNING jobs, so pruning a completed job is always safe.</summary>
    private void PruneRetainedJobs()
    {
        lock (_gate)
        {
            if (_jobs.Count <= _maxRetainedJobs) return;
            var exited = _jobs.Values
                .Where(j => j.State != BackgroundJobState.Running)
                .OrderBy(j => j.ExitedAt ?? j.StartedAt)
                .ThenBy(j => j.StartedAt)
                .ToList();
            int toRemove = _jobs.Count - _maxRetainedJobs;
            foreach (var j in exited)
            {
                if (toRemove-- <= 0) break;
                if (_jobs.Remove(j.JobId))
                    _ctx.Log.Debug($"background pruned completed job {j.JobId} (bounded retention)");
            }
        }
    }

    private IValueLease<IShellCommandResolver> AcquireResolver(string shellId)
    {
        var id = "resolver:" + (shellId ?? string.Empty).ToLowerInvariant();
        try { return _ctx.Services.Acquire<IShellCommandResolver>(id); }
        catch (ServiceUnavailableException)
        {
            throw new InvalidOperationException(
                $"No shell resolver available for '{shellId}' (is the tools plugin loaded?); " +
                $"expected id '{id}'.");
        }
    }


    private string NewId()
    {
        lock (_gate) _counter++;
        return $"bg-{DateTimeOffset.UtcNow:HHmmss-fff}-{_counter:D3}";
    }
}

// ---------------------------------------------------------------------------
// Tools
// ---------------------------------------------------------------------------

/// <summary>background_start: launch a long-running process (PLAN §27).</summary>
public sealed class BackgroundStartTool : IAgentTool
{
    private readonly BackgroundJobManager _mgr;
    public BackgroundStartTool(BackgroundJobManager mgr) => _mgr = mgr;

    public string Name => "background_start";
    public string Description => "Start a long-running command as a background job (e.g. a dev server). Returns a job id; the job keeps running independently of the shell.";
    public IReadOnlyList<string> Guidelines => ["Use for long-running processes (servers, watchers); read output later with background_output."];
    public JsonElement Parameters => PluginArgs.Schema(
        ("shell", "string", "Shell to use: 'bash' or 'powershell'."),
        ("command", "string", "The command to run in the background."),
        ("workdir", "string", "Working directory. Optional."));

    public async ValueTask<ToolResult> ExecuteAsync(ToolContext ctx, CancellationToken ct)
    {
        // PLAN §18/§25: default workdir is the session workspace.
        var shell = PluginArgs.Str(ctx.Arguments, "shell", "bash");
        var command = PluginArgs.Str(ctx.Arguments, "command");
        var workdir = PluginArgs.OptStr(ctx.Arguments, "workdir") ?? ctx.Workspace;
        if (string.IsNullOrEmpty(command))
            return Error("command is required");
        try
        {
            // astra-1 H: capture ownership at start — the session that is
            // running (RunId/ProjectId are null: ToolContext carries only the
            // session, and they are not yet plumbed into tool execution).
            var info = await _mgr.StartAsync(
                shell, command, workdir, null,
                new JobOwnership(ctx.SessionId, null, null),
                ct);
            return new ToolResult(Name, Name, [new TextPart($"started job {info.JobId} (shell={info.ShellId}) command={command}")], false);
        }
        catch (Exception ex)
        {
            return Error($"background start failed: {ex.Message}");
        }
    }

    private ToolResult Error(string m) => new(Name, Name, [new TextPart(m)], IsError: true);
}

/// <summary>background_output: read captured output from a cursor (PLAN §27/§28).</summary>
public sealed class BackgroundOutputTool : IAgentTool
{
    private readonly BackgroundJobManager _mgr;
    public BackgroundOutputTool(BackgroundJobManager mgr) => _mgr = mgr;

    public string Name => "background_output";
    public string Description => "Read the output of a background job from a cursor offset (0 = from the start). Returns the new text and the offset to pass next time.";
    public IReadOnlyList<string> Guidelines => ["Pass the cursor returned last time to read only new output; 0 = from the start."];
    public JsonElement Parameters => PluginArgs.Schema(
        ("jobId", "string", "The background job id from background_start."),
        ("offset", "number", "Cursor offset in characters. Optional (default 0)."));

    public async ValueTask<ToolResult> ExecuteAsync(ToolContext ctx, CancellationToken ct)
    {
        var jobId = PluginArgs.Str(ctx.Arguments, "jobId");
        if (string.IsNullOrEmpty(jobId)) return Error("jobId is required");
        var offset = PluginArgs.Int(ctx.Arguments, "offset", 0);
        var outp = await _mgr.GetOutputAsync(jobId, offset, ct);
        if (outp.State == BackgroundJobState.Failed && outp.Text == "no such job")
            return Error($"no such job {jobId}");
        var body = string.IsNullOrWhiteSpace(outp.Text)
            ? "(no new output yet)"
            : outp.Text;
        return new ToolResult(Name, Name,
            [new TextPart($"{body}\n[state={outp.State} exit={outp.ExitCode?.ToString() ?? "-"} nextOffset={outp.NextOffset} truncated={outp.Truncated}]")], false);
    }

    private ToolResult Error(string m) => new(Name, Name, [new TextPart(m)], IsError: true);
}

/// <summary>background_list: enumerate background jobs (PLAN §27).</summary>
public sealed class BackgroundListTool : IAgentTool
{
    private readonly BackgroundJobManager _mgr;
    public BackgroundListTool(BackgroundJobManager mgr) => _mgr = mgr;

    public string Name => "background_list";
    public string Description => "List all background jobs with their ids, state, and exit code.";
    public IReadOnlyList<string> Guidelines => [];
    public JsonElement Parameters => PluginArgs.Schema();

    public async ValueTask<ToolResult> ExecuteAsync(ToolContext ctx, CancellationToken ct)
    {
        var jobs = await _mgr.ListAsync(ct);
        if (jobs.Count == 0) return new ToolResult(Name, Name, [new TextPart("no background jobs")], false);
        var sb = new StringBuilder();
        foreach (var j in jobs)
            sb.AppendLine($"{j.JobId}  {j.State,-10} exit={j.ExitCode?.ToString() ?? "-"}  [{j.ShellId}] {j.Command}");
        return new ToolResult(Name, Name, [new TextPart(sb.ToString().TrimEnd())], false);
    }
}

/// <summary>background_kill: stop a background job and its process tree (PLAN §27).</summary>
public sealed class BackgroundKillTool : IAgentTool
{
    private readonly BackgroundJobManager _mgr;
    public BackgroundKillTool(BackgroundJobManager mgr) => _mgr = mgr;

    public string Name => "background_kill";
    public string Description => "Kill a background job and its entire process tree. Returns true if a matching job was found.";
    public IReadOnlyList<string> Guidelines => [];
    public JsonElement Parameters => PluginArgs.Schema(
        ("jobId", "string", "The background job id to kill."));

    public async ValueTask<ToolResult> ExecuteAsync(ToolContext ctx, CancellationToken ct)
    {
        var jobId = PluginArgs.Str(ctx.Arguments, "jobId");
        if (string.IsNullOrEmpty(jobId)) return Error("jobId is required");
        var ok = await _mgr.KillAsync(jobId, ct);
        return ok
            ? new ToolResult(Name, Name, [new TextPart($"killed {jobId}")], false)
            : Error($"no such job {jobId}");
    }

    private ToolResult Error(string m) => new(Name, Name, [new TextPart(m)], IsError: true);
}

/// <summary>
/// The reloadable BackgroundTasks plugin (PLAN §27/§28). Registers the
/// <see cref="IBackgroundJobManager"/> service and the background_* tools into
/// the shared tool registry, and serves a Kestrel surface (default 5275)
/// with /api/bg/* endpoints for its own job page.
///
/// astra-2 §12.1: it STOPS registering its own right-panel tab — the combined
/// Work view is registered by NetPI.Activity (id "background") on Activity's
/// own port. This plugin keeps process ownership, the tools and the /api/bg/*
/// APIs; the panel registration is gone (docs/web-panels.md).
/// </summary>
public sealed class BackgroundTasksPlugin : INetPiPlugin
{
    private IPluginContext? _ctx;
    private IAgentTool[] _tools = [];
    private IDisposable[] _registrations = [];
    private BackgroundJobManager? _mgr;
    private BgWebApp? _app;
    private int _port;
    private IDisposable? _toolsWatch; // astra-1 P5: re-register when the tools registry instance is replaced
    private readonly object _reregisterGate = new(); // astra-1 P5: at most one re-registration in flight
    private IToolRegistry? _toolsRegistry; // astra-1 P5: which registry the live _registrations target

    public PluginInfo Info { get; } = new("netPI.BackgroundTasks", "Background Tasks", "0.2.0");

    public ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _ctx = context;
        _mgr = new BackgroundJobManager(context);
        context.Services.Register<IBackgroundJobManager>("background", _mgr);

        // astra-2 §12.1: this plugin no longer registers a right-panel — the
        // combined Work view is registered by NetPI.Activity (id "background").
        // The /api/bg/* endpoints below stay for this plugin's own page and for
        // tool/legacy access (docs/web-panels.md).
        var cfg = context.OwnConfig;
        int port = cfg.ValueKind == System.Text.Json.JsonValueKind.Object
                   && cfg.TryGetProperty("port", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number
            ? p.GetInt32() : 5275;
        _port = port;
        _app = new BgWebApp(_mgr, context.Log, port);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        // All plugins have been loaded by the time any plugin starts, so the
        // tools registry (owned by the Tools plugin) is available now.
        var ctx = _ctx ?? throw new InvalidOperationException("Loaded context not available.");
        RegisterTools(); // astra-1 P5: shared with the tools-replacement re-registration (resolves "tools" itself)
        ctx.Log.Information($"BackgroundTasks ready: {string.Join(", ", _tools.Select(t => t.Name))}");

        // astra-1 P5: reloading the Tools plugin REPLACES the registry instance
        // under "tools" — our registrations would be gone. The host notifies
        // us (service-replacement watch, fired after the swap commits) and we
        // re-register into the NEW registry WITHOUT restarting: the job
        // manager, running jobs and the Kestrel panel are all untouched.
        _toolsWatch?.Dispose();
        _toolsWatch = ctx.Services.WatchServiceReplacement("tools", id =>
        {
            // Re-registration is async + bounded; run it detached from the
            // registering thread (the tools reload does not wait on us).
            _ = ReRegisterToolsAsync();
        });

        var app = _app ?? throw new InvalidOperationException("Web app not initialised.");
        await app.StartAsync(cancellationToken);
        // astra-2: the panel moved to NetPI.Activity — nothing to re-register here.
        await ValueTask.CompletedTask;
    }

    /// <summary>
    /// astra-1 P5: register the background tools into the CURRENT tools registry.
    /// Handles for a DIFFERENT registry instance are retired but NOT disposed:
    /// after a Tools reload the old <c>ToolRegistryImpl</c> lives in an unloading
    /// ALC — touching it during a re-registration could deadlock against the
    /// drain. The stale handles simply leak with the old generation (their
    /// <c>Unregister</c> was already skipped there via the removed entry).
    /// </summary>
    private void RegisterTools(IToolRegistry? into = null)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("Loaded context not available.");
        var registry = into ?? ctx.Services.Resolve<IToolRegistry>("tools");
        var mgr = _mgr ?? throw new InvalidOperationException("Job manager not initialised.");
        var sameRegistry = _toolsRegistry is not null && ReferenceEquals(_toolsRegistry, registry);
        if (sameRegistry)
            foreach (var r in _registrations) r.Dispose(); // replace-in-place on a live registry
        // else: stale handles belong to a reloaded (or being unloaded) registry — retire without disposing
        _registrations = [];
        _tools = [
            new BackgroundStartTool(mgr),
            new BackgroundOutputTool(mgr),
            new BackgroundListTool(mgr),
            new BackgroundKillTool(mgr),
        ];
        _registrations = _tools.Select(t => registry.Register(t)).ToArray();
        _toolsRegistry = registry;
    }

    /// <summary>
    /// astra-1 P5: the tools registry instance was replaced (Tools reload).
    /// Re-register our tools into the new instance without a restart; failures
    /// are logged, never thrown (we must not take the reloading plugin down).
    /// </summary>
    private async Task ReRegisterToolsAsync()
    {
        await Task.Run(async () =>
        {
            var ctx = _ctx;
            if (ctx is null) return;
            var gate = _reregisterGate;
            lock (gate)
            {
                try
                {
                    RegisterTools();
                    ctx.Log.Information("BackgroundTasks re-registered tools into the reloaded tools registry");
                }
                catch (Exception ex)
                {
                    ctx.Log.Error($"BackgroundTasks could not re-register tools into the new registry: {ex.Message}", ex);
                }
            }
            await ValueTask.CompletedTask;
        });
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _toolsWatch?.Dispose();
        _toolsWatch = null;
        if (_app is { } webApp)
        {
            _app = null;
            try { await webApp.StopAsync(cancellationToken); } catch { /* best-effort */ }
        }
        _mgr?.StopAll();
        _mgr = null;
        foreach (var r in _registrations) r.Dispose();
        _registrations = [];
        _tools = [];
        await ValueTask.CompletedTask;
    }

    public async ValueTask UnloadAsync(CancellationToken cancellationToken)
    {
        // astra-2: no panel to dispose (it moved to NetPI.Activity).
        await ValueTask.CompletedTask;
    }
}

/// <summary>Minimal JSON-schema/argument helpers (self-contained copy).</summary>
internal static class PluginArgs
{
    public static string Str(JsonElement el, string name, string fallback = "")
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? fallback : fallback;

    public static string? OptStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    public static int Int(JsonElement el, string name, int fallback)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : fallback;

    public static JsonElement Schema(params (string name, string type, string? desc)[] fields)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var f in fields)
        {
            props[f.name] = new Dictionary<string, object>
            {
                ["type"] = f.type,
                ["description"] = f.desc ?? string.Empty,
            };
            if (f.name is not ("workdir" or "offset")) required.Add(f.name);
        }
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = props,
        };
        if (required.Count > 0) schema["required"] = required;
        return JsonSerializer.SerializeToElement(schema, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
}
