using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetPI.Host;

/// <summary>
/// Restarts NetPI by itself once a publish left something for the next start and nothing goes on. build.ps1 -Publish
/// writes <see cref="MarkerName"/> into the app folder when it did (new host files, plugins built against changed
/// contracts, <c>-NextStart</c>); the start that installs them deletes it (<see cref="PendingBuild"/>). Nothing goes on when
/// the reload gate has no hold at all - no run, active or queued, no process the shell started - and no client (the UI, a
/// script) called a method that may write for <c>host.autoRestartQuietSeconds</c>. Then the gate is closed to new runs
/// (they wait for the new process instead of starting on this one), <c>app.restarting</c> tells the clients, and the shell
/// restarts (<see cref="NetPiServerOptions.Restart"/>: it starts its successor, which waits for this process to exit, and
/// stops). <c>host.autoRestart: false</c> turns it off; without a shell that can restart it never runs.
/// </summary>
internal sealed class RestartWhenIdle : IDisposable
{
    public const string MarkerName = ".restart.json";
    public const int DefaultQuietSeconds = 60;

    private readonly HostKernel _k;
    private readonly Func<Task> _restart;
    private readonly TimeSpan _announce;
    private readonly TimeSpan? _quiet;
    private readonly string _appDir;
    private readonly Timer _timer;
    private readonly Lock _lock = new();
    private long _busySince;
    private string[]? _reasons;
    private DateTimeOffset? _wantedSince;
    private string? _waitingFor;
    private bool _restarting;
    private bool _disposed;

    /// <param name="tick">How often it looks (the marker is a file, the gate and the activity are counters).</param>
    /// <param name="announce">Between <c>app.restarting</c> and the restart, so the clients can say so.</param>
    /// <param name="quiet">The quiet time, in place of the setting (tests).</param>
    /// <param name="appDir">Where the marker is looked for, in place of the app folder (tests).</param>
    public RestartWhenIdle(HostKernel kernel, Func<Task> restart, TimeSpan? tick = null, TimeSpan? announce = null, TimeSpan? quiet = null, string? appDir = null)
    {
        _k = kernel;
        _appDir = appDir ?? kernel.Paths.AppDir;
        _restart = restart;
        _announce = announce ?? TimeSpan.FromSeconds(3);
        _quiet = quiet;
        _busySince = Environment.TickCount64;
        var every = tick ?? TimeSpan.FromSeconds(5);
        _timer = new Timer(_ => Check(), null, every, every);
    }

    private string Marker => Path.Combine(_appDir, MarkerName);

    /// <summary>For <c>app.info</c>: null while nothing waits for a restart, else why and what it waits for.</summary>
    public object? Status
    {
        get
        {
            lock (_lock)
                return _reasons is null ? null : new { reasons = _reasons, since = _wantedSince, waitingFor = _restarting ? "restarting" : _waitingFor };
        }
    }

    /// <summary>One look; the timer calls it (tests call it directly).</summary>
    internal void Check()
    {
        lock (_lock)
        {
            if (_disposed || _restarting) return;
            if (!File.Exists(Marker))
            {
                _reasons = null;
                _wantedSince = null;
                return;
            }
            if (_reasons is null)
            {
                _reasons = ReadReasons();
                _wantedSince = DateTimeOffset.UtcNow;
                _busySince = Environment.TickCount64; // the quiet time counts from the publish on
                _k.Log.LogInformation("A publish left {Reasons} for the next start: NetPI restarts once nothing goes on", string.Join(", ", _reasons));
            }
            if (!(_k.Settings.Get<bool?>("host.autoRestart") ?? true))
            {
                _waitingFor = "host.autoRestart is off";
                return;
            }
            var gate = _k.Plugins.Gate;
            if (gate.Count > 0 || gate.Waiting > 0)
            {
                _waitingFor = "work in progress: " + string.Join("; ", gate.Holders());
                _busySince = Environment.TickCount64;
                return;
            }
            var quiet = _quiet ?? TimeSpan.FromSeconds(Math.Max(10, _k.Settings.Get<int?>("host.autoRestartQuietSeconds") ?? DefaultQuietSeconds));
            var last = Math.Max(_busySince, _k.Rpc.LastClientWrite);
            if (Environment.TickCount64 - last < quiet.TotalMilliseconds)
            {
                _waitingFor = $"{quiet.TotalSeconds:0} s without a change from you";
                return;
            }
            // closes the gate to new runs: one that starts now waits, and runs on the new process
            if (!gate.TryBeginApply(out _))
            {
                _waitingFor = "a plugin reload under way";
                return;
            }
            _restarting = true;
        }
        _k.Log.LogInformation("Restarting to install {Reasons}", string.Join(", ", _reasons!));
        _k.Bus.Publish("app.restarting", new { reasons = _reasons, inMs = (long)_announce.TotalMilliseconds });
        _ = Task.Run(RestartAsync);
    }

    private async Task RestartAsync()
    {
        try
        {
            await Task.Delay(_announce).ConfigureAwait(false);
            await _restart().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // the successor did not start: this process goes on, and the next publish tries again
            _k.Log.LogError(ex, "The restart failed; NetPI goes on, and what waits stays for the next start");
            _k.Plugins.Gate.EndApply();
            _k.Bus.Publish("app.restartFailed", new { error = ex.Message });
            lock (_lock) _waitingFor = "the restart failed: " + ex.Message;
            try { File.Delete(Marker); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            lock (_lock) _restarting = false;
        }
    }

    private string[] ReadReasons()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Marker));
            if (doc.RootElement.TryGetProperty("reasons", out var r) && r.ValueKind == JsonValueKind.Array)
            {
                var list = r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
                if (list.Length > 0) return list;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return ["an update"];
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        _timer.Dispose();
    }
}
