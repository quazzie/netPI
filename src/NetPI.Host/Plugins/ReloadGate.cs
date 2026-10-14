namespace NetPI.Host.Plugins;

/// <summary>
/// The host's <see cref="IReloadGate"/>: the holds plugins take while a reload would disturb their work, and the one
/// flag the plugin manager raises while it applies a batch of reloads. A batch is applied only when no hold is alive
/// (<see cref="TryBeginApply"/>); work that enters while one is applied waits for that batch alone
/// (<see cref="EnterAsync"/>) and then holds the gate. When the last hold goes, <see cref="Opened"/> tells the manager
/// to go on. No timer and no polling: the manager is woken by the release itself. Nothing ever waits for a reload that is
/// only deferred: a busy app keeps the old versions until it is idle, or until the next start loads the new files.
/// </summary>
internal sealed class ReloadGate : IReloadGate, IDisposable
{
    /// <summary>
    /// The apply under way, in its own flow: a plugin's start or stop that enters the gate must not wait for the apply it
    /// is part of. A box, not a bool, so a task that start left running sees it end with the apply instead of keeping a
    /// copy of "true" for ever.
    /// </summary>
    private static readonly AsyncLocal<ApplyFlow?> s_inApply = new();
    private ApplyFlow? _flow;

    private readonly Lock _lock = new();
    private readonly List<Handle> _holds = [];
    private readonly List<Waiter> _waiters = [];
    private bool _applying;
    private bool _disposed;

    /// <summary>Raised when the last hold was released (on the releasing thread: keep it short and schedule the work).</summary>
    public Action? Opened { get; set; }

    public IDisposable Hold(string owner, string reason)
    {
        var h = new Handle(this, owner, reason);
        lock (_lock) _holds.Add(h);
        return h;
    }

    public ValueTask<IDisposable> EnterAsync(string owner, string reason, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Waiter w;
        lock (_lock)
        {
            if (_disposed || !_applying || s_inApply.Value is { Active: true })
            {
                var h = new Handle(this, owner, reason);
                _holds.Add(h);
                return ValueTask.FromResult<IDisposable>(h);
            }
            w = new Waiter(owner, reason);
            _waiters.Add(w);
        }
        if (ct.CanBeCanceled)
        {
            w.Registration = ct.Register(() =>
            {
                bool removed;
                lock (_lock) removed = _waiters.Remove(w);
                if (removed) w.Done.TrySetCanceled(ct);
            });
        }
        return new ValueTask<IDisposable>(w.Done.Task);
    }

    public IReadOnlyList<string> Holders()
    {
        lock (_lock) return [.. _holds.Select(h => $"{h.Owner}: {h.Reason} (since {h.Since:HH:mm:ss}Z)")];
    }

    /// <summary>How many callers wait in <see cref="EnterAsync"/> (tests, diagnostics).</summary>
    public int Waiting
    {
        get { lock (_lock) return _waiters.Count; }
    }

    /// <summary>How many holds are alive (tests, diagnostics).</summary>
    public int Count
    {
        get { lock (_lock) return _holds.Count; }
    }

    /// <summary>
    /// Start applying one reload: true when nothing holds the gate and no other reload is being applied. Otherwise
    /// <paramref name="held"/> says whether holds are the reason (then the reload is deferred until <see cref="Opened"/>)
    /// or another apply is under way (whose flow goes on with what is pending).
    /// </summary>
    public bool TryBeginApply(out bool held)
    {
        lock (_lock)
        {
            held = _holds.Count > 0;
            if (_disposed || _applying || held) return false;
            _applying = true;
            _flow = new ApplyFlow();
        }
        s_inApply.Value = _flow;
        return true;
    }

    /// <summary>The reload is done: whatever waited to enter now holds the gate and goes on.</summary>
    public void EndApply()
    {
        List<(Waiter W, Handle H)> admitted;
        lock (_lock)
        {
            _flow?.End();
            _flow = null;
            _applying = false;
            admitted = [.. _waiters.Select(w => (w, new Handle(this, w.Owner, w.Reason)))];
            _waiters.Clear();
            _holds.AddRange(admitted.Select(a => a.H));
        }
        foreach (var (w, h) in admitted)
        {
            w.Registration.Dispose();
            if (!w.Done.TrySetResult(h)) h.Dispose();   // cancelled in the same instant: its hold is not wanted
        }
    }

    /// <summary>Drop every hold of <paramref name="owner"/>: a plugin that was stopped for good cannot release its own any more.</summary>
    public int ReleaseOwner(string owner)
    {
        int n;
        bool open;
        lock (_lock)
        {
            n = _holds.RemoveAll(h => string.Equals(h.Owner, owner, StringComparison.OrdinalIgnoreCase));
            open = n > 0 && _holds.Count == 0 && !_applying && !_disposed;
        }
        if (open) Opened?.Invoke();
        return n;
    }

    private void Release(Handle h)
    {
        bool open;
        lock (_lock)
        {
            if (!_holds.Remove(h)) return;
            open = _holds.Count == 0 && !_applying && !_disposed;
        }
        if (open) Opened?.Invoke();
    }

    /// <summary>Shutdown: nothing is applied any more, and whatever waits to enter goes on.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        EndApply();
    }

    private sealed class Handle(ReloadGate gate, string owner, string reason) : IDisposable
    {
        private int _released;
        public string Owner { get; } = owner;
        public string Reason { get; } = reason;
        public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(this);
        }
    }

    private sealed class ApplyFlow
    {
        private volatile bool _active = true;
        public bool Active => _active;
        public void End() => _active = false;
    }

    private sealed class Waiter(string owner, string reason)
    {
        public string Owner { get; } = owner;
        public string Reason { get; } = reason;
        public TaskCompletionSource<IDisposable> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
    }
}
