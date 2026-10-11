namespace NetPI.MockLlm;

/// <summary>
/// Named gates a test opens on demand (<c>gate=&lt;name&gt;</c> in a scenario tag, <c>POST /_release?gate=&lt;name&gt;</c>): a model
/// call that reaches a closed gate waits there after its first chunk, so a test can look at the live state for as long as it needs
/// and end it when it is done — where <c>hold=&lt;ms&gt;</c> is a fixed time that is too long on an idle machine and too short on a
/// loaded one. Releasing a gate opens it for good, so a release that arrives before the call does not leave the call waiting.
/// </summary>
public sealed class Gates
{
    /// <summary>A gate nobody releases lets its calls go after this long, the same cap as <c>hold</c>.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    private sealed class Gate
    {
        public bool Open;
        public int Waiting;
        public TaskCompletionSource Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);

    /// <summary>Waits until <paramref name="name"/> is released (at once when it already was), at most <see cref="MaxWait"/>.</summary>
    public async Task WaitAsync(string name, CancellationToken ct)
    {
        Gate gate;
        lock (_lock)
        {
            if (!_gates.TryGetValue(name, out gate!)) _gates[name] = gate = new Gate();
            if (gate.Open) return;
            gate.Waiting++;
        }
        try
        {
            await gate.Opened.Task.WaitAsync(MaxWait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        finally
        {
            lock (_lock) gate.Waiting--;
        }
    }

    /// <summary>Opens <paramref name="name"/>: its waiting calls go on, and later ones pass. Returns how many were waiting.</summary>
    public int Release(string name)
    {
        lock (_lock)
        {
            if (!_gates.TryGetValue(name, out var gate)) _gates[name] = gate = new Gate();
            gate.Open = true;
            gate.Opened.TrySetResult();
            return gate.Waiting;
        }
    }

    /// <summary>Releases every gate and forgets them all (<c>POST /_reset</c>).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            foreach (var gate in _gates.Values) gate.Opened.TrySetResult();
            _gates.Clear();
        }
    }

    /// <summary>Each known gate with the calls waiting at it and whether it is open (<c>GET /_gates</c>).</summary>
    public List<(string Name, int Waiting, bool Open)> List()
    {
        lock (_lock) return _gates.Select(g => (g.Key, g.Value.Waiting, g.Value.Open)).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
    }
}
