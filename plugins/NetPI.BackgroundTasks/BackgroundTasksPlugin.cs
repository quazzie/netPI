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
    public Process? Process { get; set; }
    public BackgroundJobState State { get; set; } = BackgroundJobState.Running;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
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
            if (offset <= _dropped)
            {
                // requested region is partly gone; start from the earliest kept char
                return (_output.ToString(), _dropped + _output.Length, offset < _dropped);
            }
            if (offset >= total) return (string.Empty, total, false);
            int local = offset - _dropped;
            int take = Math.Min(64_000, _output.Length - local);
            var slice = _output.ToString()[local..(local + take)];
            return (slice, offset + take, false);
        }
    }

    public BackgroundJobInfo ToInfo() => new(
        JobId, ShellId, Command, State, StartedAt, ExitedAt, ExitCode, null);

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

    public BackgroundJobManager(IPluginContext ctx) => _ctx = ctx;

    public async ValueTask<BackgroundJobInfo> StartAsync(
        string shellId, string command, string workingDirectory,
        JsonElement? options, CancellationToken cancellationToken = default)
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
        _ctx.Log.Information($"background {job.JobId} exited code={job.ExitCode}");
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
            var info = await _mgr.StartAsync(shell, command, workdir, null, ct);
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
/// the shared tool registry.
/// </summary>
public sealed class BackgroundTasksPlugin : INetPiPlugin
{
    private IPluginContext? _ctx;
    private IAgentTool[] _tools = [];
    private IDisposable[] _registrations = [];
    private BackgroundJobManager? _mgr;

    public PluginInfo Info { get; } = new("netPI.BackgroundTasks", "Background Tasks", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _ctx = context;
        _mgr = new BackgroundJobManager(context);
        context.Services.Register<IBackgroundJobManager>("background", _mgr);
        await ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        // All plugins have been loaded by the time any plugin starts, so the
        // tools registry (owned by the Tools plugin) is available now.
        var ctx = _ctx ?? throw new InvalidOperationException("Loaded context not available.");
        var registry = ctx.Services.Resolve<IToolRegistry>("tools");
        var mgr = _mgr ?? throw new InvalidOperationException("Job manager not initialised.");
        var tools = new IAgentTool[]
        {
            new BackgroundStartTool(mgr),
            new BackgroundOutputTool(mgr),
            new BackgroundListTool(mgr),
            new BackgroundKillTool(mgr),
        };
        _tools = tools;
        _registrations = tools.Select(t => registry.Register(t)).ToArray();
        ctx.Log.Information($"BackgroundTasks ready: {string.Join(", ", tools.Select(t => t.Name))}");
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _mgr?.StopAll();
        _mgr = null;
        foreach (var r in _registrations) r.Dispose();
        _registrations = [];
        _tools = [];
        await ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
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
