namespace NetPI.Host.Plugins;

/// <summary>
/// The host's <see cref="IReloadGate"/>: the holds plugins take while a reload would disturb their work, and the one
/// flag the plugin manager raises while it applies a batch of reloads. A batch is applied only when no hold of every
/// reload is alive (<see cref="TryBeginApply"/>), and it leaves out the plugins that hold their own (<see cref="IsHeld"/>);
/// work that enters while one is applied waits for that batch alone (<see cref="EnterAsync"/>) and then holds the gate.
/// When a release leaves nothing holding every reload, <see cref="Opened"/> tells the manager to go on. No timer and no
/// polling: the manager is woken by the release itself. Nothing ever waits for a reload that is only deferred: a busy
/// app keeps the old versions until it is idle, or until the next start loads the new files.
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

    /// <summary>
    /// Raised when a release leaves no hold of every reload alive (the last run ended, or a plugin's own hold went): on the
    /// releasing thread, so keep it short and schedule the work.
    /// </summary>
    public Action? Opened { get; set; }

    public IDisposable Hold(string owner, string reason) => Add(owner, reason, own: false);

    public IDisposable HoldOwn(string owner, string reason) => Add(owner, reason, own: true);

    private Handle Add(string owner, string reason, bool own)
    {
        var h = new Handle(this, owner, reason, own);
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
                var h = new Handle(this, owner, reason, own: false);
                _holds.Add(h);
                return ValueTask.FromResult<IDisposable>(h);
            }
            w = new Waiter(owner, reason);
        }
        // Register BEFORE the waiter is published: the admit path (EndApply) disposes the registration of every waiter it
        // admits, so it must already be set - a registration assigned after the admit would stay on ct until it is disposed.
        if (ct.CanBeCanceled)
        {
            w.Registration = ct.Register(() =>
            {
                lock (_lock)
                    if (_waiters.Remove(w)) w.Done.TrySetCanceled(ct);
            });
        }
        lock (_lock)
        {
            // Cancelled in the meantime (between the registration and the publish): the callback found no waiter to remove,
            // so complete the task here and do not publish it.
            if (ct.IsCancellationRequested)
            {
                w.Registration.Dispose();
                w.Done.TrySetCanceled(ct);
                return new ValueTask<IDisposable>(w.Done.Task);
            }
            _waiters.Add(w);
        }
        return new ValueTask<IDisposable>(w.Done.Task);
    }

    public IReadOnlyList<string> Holders()
    {
        lock (_lock) return [.. _holds.Select(Text)];
    }

    /// <summary>What holds the reload of <paramref name="pluginId"/>: every hold of every reload, and that plugin's own.</summary>
    public IReadOnlyList<string> HoldersOf(string pluginId)
    {
        lock (_lock) return [.. _holds.Where(h => !h.Own || Same(h.Owner, pluginId)).Select(Text)];
    }

    private static string Text(Handle h) => $"{h.Owner}{(h.Own ? " (its own reload)" : "")}: {h.Reason} (since {h.Since:HH:mm:ss}Z)";

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether something holds every reload now (a run): a batch that is under way stops before its next plugin.</summary>
    public bool Held
    {
        get { lock (_lock) return _holds.Any(h => !h.Own); }
    }

    /// <summary>Whether <paramref name="pluginId"/> holds its own reload: a batch leaves it waiting, and applies the rest.</summary>
    public bool IsHeld(string pluginId)
    {
        lock (_lock) return _holds.Any(h => h.Own && Same(h.Owner, pluginId));
    }

    /// <summary>How many callers wait in <see cref="EnterAsync"/> (tests, diagnostics).</summary>
    public int Waiting
    {
        get { lock (_lock) return _waiters.Count; }
    }

    /// <summary>How many holds are alive, of both kinds (tests, diagnostics).</summary>
    public int Count
    {
        get { lock (_lock) return _holds.Count; }
    }

    /// <summary>
    /// Start applying a batch: true when nothing holds every reload and no other batch is being applied. Otherwise
    /// <paramref name="held"/> says whether holds are the reason (then the reloads are deferred until <see cref="Opened"/>)
    /// or another batch is under way (whose flow goes on with what is pending). A plugin's own hold does not stop a
    /// batch: the manager leaves that plugin out (<see cref="IsHeld"/>).
    /// </summary>
    public bool TryBeginApply(out bool held)
    {
        lock (_lock)
        {
            held = _holds.Any(h => !h.Own);
            if (_disposed || _applying || held) return false;
            _applying = true;
            _flow = new ApplyFlow();
        }
        s_inApply.Value = _flow;
        return true;
    }

    /// <summary>The batch is done: whatever waited to enter now holds the gate and goes on.</summary>
    public void EndApply()
    {
        List<(Waiter W, Handle H)> admitted;
        lock (_lock)
        {
            _flow?.End();
            _flow = null;
            _applying = false;
            admitted = [.. _waiters.Select(w => (w, new Handle(this, w.Owner, w.Reason, own: false)))];
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
            n = _holds.RemoveAll(h => Same(h.Owner, owner));
            open = n > 0 && OpenLocked();
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
            open = OpenLocked();
        }
        if (open) Opened?.Invoke();
    }

    /// <summary>A release opens the gate when nothing holds every reload any more (own holds left are the manager's to skip).</summary>
    private bool OpenLocked() => !_applying && !_disposed && !_holds.Any(x => !x.Own);

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

    private sealed class Handle(ReloadGate gate, string owner, string reason, bool own) : IDisposable
    {
        private int _released;
        public string Owner { get; } = owner;
        public string Reason { get; } = reason;
        /// <summary>Holds only the owner's reload (<see cref="IReloadGate.HoldOwn"/>), not every reload.</summary>
        public bool Own { get; } = own;
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
