using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace NetPI.Tools.Shell;

/// <summary>Process snapshot sent to the UI (see docs/PROTOCOL.md).</summary>
public sealed class ProcessInfo
{
    public required string Id { get; init; }
    public int Pid { get; init; }
    /// <summary>bash | pwsh</summary>
    public required string Shell { get; init; }
    public required string Command { get; init; }
    public required string Cwd { get; init; }
    public string? SessionId { get; init; }
    public string? AgentId { get; init; }
    public bool Background { get; init; }
    /// <summary>running | exited | killed | timeout</summary>
    public required string Status { get; init; }
    public int? ExitCode { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public long OutputBytes { get; init; }
}

/// <summary>What to launch: executable + arguments (+ environment tweaks and a temp script to delete afterwards).</summary>
public sealed class LaunchSpec
{
    public required string Shell { get; init; }
    public required string Executable { get; init; }
    public List<string> Arguments { get; init; } = [];
    public Dictionary<string, string?> Environment { get; init; } = [];
    public string? TempScript { get; init; }
}

/// <summary>A spawned shell command with captured output. Created and tracked by <see cref="ProcessRegistry"/>.</summary>
public sealed class ManagedProcess : IDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly List<Task> _pumps = [];
    private readonly object _statusLock = new();
    private readonly LaunchSpec _spec;
    private OutputThrottle? _live;
    private string _status = "running";
    private string? _requestedStatus;

    public string Id { get; }
    public int Pid { get; }
    public string Shell => _spec.Shell;
    public string Command { get; }
    public string Cwd { get; }
    public string? SessionId { get; init; }
    public string? AgentId { get; init; }
    public bool Background { get; init; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }
    public int? ExitCode { get; private set; }
    public OutputCapture Output { get; }
    public string Status { get { lock (_statusLock) return _status; } }
    public bool IsRunning => Status == "running";
    /// <summary>Completes when the process has exited and its output was drained.</summary>
    public Task Completion => _exited.Task;
    public TimeSpan Elapsed => (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    /// <summary>Called once when the process has finished (status set, output drained).</summary>
    internal Action<ManagedProcess>? OnExited { get; set; }

    /// <summary>Grace period to drain output after the shell exited (orphaned grandchildren may keep the pipes open).</summary>
    public static TimeSpan DrainGrace { get; set; } = TimeSpan.FromMilliseconds(750);

    private ManagedProcess(string id, LaunchSpec spec, string command, string cwd, Process process, OutputCapture output)
    {
        Id = id;
        _spec = spec;
        Command = command;
        Cwd = cwd;
        _process = process;
        Output = output;
        Pid = process.Id;
        StartedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Start the process. Throws <see cref="System.ComponentModel.Win32Exception"/>/<see cref="InvalidOperationException"/> when it cannot be spawned.</summary>
    public static ManagedProcess Start(string id, LaunchSpec spec, string command, string cwd, OutputCapture output,
        Action<string>? live = null, TimeSpan? liveInterval = null, string? sessionId = null, string? agentId = null, bool background = false,
        Action<ManagedProcess>? onExited = null, Action<ManagedProcess>? onStarted = null)
    {
        var psi = new ProcessStartInfo(spec.Executable)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Environment)
        {
            if (v is null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException($"Failed to start {spec.Executable}");
        var mp = new ManagedProcess(id, spec, command, cwd, process, output)
        {
            SessionId = sessionId,
            AgentId = agentId,
            Background = background,
        };
        mp.OnExited = onExited;
        if (live is not null) mp._live = new OutputThrottle(live, liveInterval ?? TimeSpan.FromMilliseconds(50));
        try { process.StandardInput.Close(); } catch { /* already gone */ }
        var sinkLock = new object();
        void Sink(string text)
        {
            lock (sinkLock)
            {
                mp.Output.Append(text);
                mp._live?.Add(text);
            }
        }
        mp._pumps.Add(Pump(process.StandardOutput.BaseStream, Sink, mp._pumpCts.Token));
        mp._pumps.Add(Pump(process.StandardError.BaseStream, Sink, mp._pumpCts.Token));
        try { onStarted?.Invoke(mp); } catch { }
        _ = mp.WatchAsync();
        return mp;
    }

    private static async Task Pump(Stream stream, Action<string> sink, CancellationToken ct)
    {
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        var stripper = new AnsiStripper();
        var buffer = new byte[16 * 1024];
        var chars = new char[16 * 1024 + 4];
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) break;
                var c = decoder.GetChars(buffer, 0, n, chars, 0, flush: false);
                if (c > 0) sink(stripper.Process(chars.AsSpan(0, c)));
            }
            var last = decoder.GetChars([], 0, 0, chars, 0, flush: true);
            if (last > 0) sink(stripper.Process(chars.AsSpan(0, last)));
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private async Task WatchAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch { }
        // Drain what is left; do not wait forever for grandchildren that inherited the pipes.
        var pumps = Task.WhenAll(_pumps);
        if (await Task.WhenAny(pumps, Task.Delay(DrainGrace)).ConfigureAwait(false) != pumps)
            _pumpCts.Cancel();
        int? code = null;
        try { code = _process.ExitCode; } catch { }
        lock (_statusLock)
        {
            ExitCode = code;
            EndedAt = DateTimeOffset.UtcNow;
            _status = _requestedStatus ?? "exited";
        }
        _live?.Dispose();
        // This record stays in the registry (the last 50). The callbacks are the caller's closures, so keeping them pins the
        // caller's plugin: reloading the agent runtime after any bash call could not collect the old load context.
        _live = null;
        if (_spec.TempScript is not null)
        {
            try { File.Delete(_spec.TempScript); } catch { }
        }
        try { OnExited?.Invoke(this); } catch { }
        OnExited = null;
        _exited.TrySetResult();
    }

    /// <summary>Kill the whole process tree. <paramref name="status"/>: killed | timeout. Returns false when it had already exited.</summary>
    public bool Kill(string status = "killed")
    {
        lock (_statusLock)
        {
            if (_status != "running") return false;
            try { if (_process.HasExited) return false; } catch { }
            _requestedStatus ??= status;
        }
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (NotSupportedException) { }
        return true;
    }

    public ProcessInfo ToInfo()
    {
        lock (_statusLock)
        {
            return new ProcessInfo
            {
                Id = Id, Pid = Pid, Shell = Shell, Command = Command, Cwd = Cwd, SessionId = SessionId, AgentId = AgentId,
                Background = Background, Status = _status, ExitCode = ExitCode, StartedAt = StartedAt, EndedAt = EndedAt,
                OutputBytes = Output.TotalBytes,
            };
        }
    }

    public void Dispose()
    {
        _live?.Dispose();
        Output.Dispose();
        try { _process.Dispose(); } catch { }
        _pumpCts.Dispose();
    }
}

/// <summary>Running processes plus the most recent finished ones (last 50).</summary>
public sealed class ProcessRegistry(IEventBus? events = null)
{
    public const int KeepFinished = 50;
    /// <summary>In-memory output kept for finished foreground commands (trimmed after the result was formatted).</summary>
    public const int FinishedTailChars = 256 * 1024;

    private readonly ConcurrentDictionary<string, ManagedProcess> _all = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _finished = new();

    public IEventBus? Events { get; } = events;

    public ManagedProcess? Get(string idOrPid)
    {
        idOrPid = idOrPid.Trim();
        if (_all.TryGetValue(idOrPid, out var p)) return p;
        if (!idOrPid.StartsWith("proc_", StringComparison.Ordinal) && _all.TryGetValue("proc_" + idOrPid, out p)) return p;
        if (int.TryParse(idOrPid, out var pid))
            return _all.Values.Where(x => x.Pid == pid).OrderByDescending(x => x.StartedAt).FirstOrDefault();
        return null;
    }

    /// <summary>Running first, then finished; newest first.</summary>
    public IReadOnlyList<ManagedProcess> List() =>
        _all.Values.OrderByDescending(p => p.IsRunning).ThenByDescending(p => p.StartedAt).ToList();

    public int RunningCount => _all.Values.Count(p => p.IsRunning);

    internal void Add(ManagedProcess p)
    {
        _all[p.Id] = p;
        Publish(EventTypes.ProcessStarted, p);
    }

    internal void OnExited(ManagedProcess p)
    {
        Publish(EventTypes.ProcessExited, p);
        _finished.Enqueue(p.Id);
        while (_finished.Count > KeepFinished && _finished.TryDequeue(out var old))
        {
            if (_all.TryRemove(old, out var removed)) removed.Dispose();
        }
    }

    private void Publish(string type, ManagedProcess p)
    {
        try { Events?.Publish(type, new { process = p.ToInfo() }); } catch { }
    }

    /// <summary>Kill every running process (plugin stop).</summary>
    public async Task KillAllAsync(TimeSpan wait)
    {
        var running = _all.Values.Where(p => p.IsRunning).ToList();
        foreach (var p in running) p.Kill();
        if (running.Count > 0)
            await Task.WhenAny(Task.WhenAll(running.Select(p => p.Completion)), Task.Delay(wait)).ConfigureAwait(false);
    }
}
