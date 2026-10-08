using Microsoft.Extensions.Logging;

namespace NetPI.Lsp;

/// <summary>
/// The running servers, one per (server, workspace root), started lazily on their first request and installed then if
/// needed. At most <c>lsp.maxServers</c> run (3): starting one more stops the least recently used; one unused for
/// <c>lsp.idleMinutes</c> (15) is stopped by the sweep. A server that died, or failed to start, is started again on its
/// next request. The lock is held only to look up and book: an install or a start (csharp-ls loading a solution takes
/// minutes) runs outside it, shared by everyone asking for that server meanwhile, and never holds up the others.
/// </summary>
internal sealed class LspPool(LspInstaller installer, ILogger log, Func<int> maxServers, Func<TimeSpan> idle) : IAsyncDisposable
{
    /// <summary>One server's start (shared while it runs, cancelled when the server is stopped before it is up), then its session.</summary>
    private sealed class Entry
    {
        public Entry(Func<CancellationToken, Task<LspSession>> start, CancellationToken closing)
        {
            Cancel = CancellationTokenSource.CreateLinkedTokenSource(closing);
            var token = Cancel.Token;
            Start = Task.Run(() => start(token), CancellationToken.None);
        }

        public CancellationTokenSource Cancel { get; }
        public Task<LspSession> Start { get; }
        public DateTimeOffset Asked { get; set; } = DateTimeOffset.UtcNow;
        public LspSession? Session => Start.IsCompletedSuccessfully ? Start.Result : null;
        public DateTimeOffset LastUsed => Session is { } s && s.LastUsed > Asked ? s.LastUsed : Asked;
        /// <summary>Started and gone since, or never started: the next request starts it again.</summary>
        public bool Dead => Start.IsFaulted || Start.IsCanceled || Session is { Alive: false };
    }

    private readonly Dictionary<(string Server, string Root), Entry> _entries = [];
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _closing = new();
    private bool _closed;

    public async Task<LspSession> GetAsync(LspServer server, string root, CancellationToken ct)
    {
        var key = (server.Id, Key(root));
        var stop = new List<Entry>();
        Entry entry;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_entries.TryGetValue(key, out entry!) || entry.Dead)
            {
                if (entry is not null)
                {
                    _entries.Remove(key);
                    stop.Add(entry);
                }
                while (_entries.Count >= Math.Max(1, maxServers()))
                {
                    var oldest = _entries.MinBy(e => e.Value.LastUsed);
                    _entries.Remove(oldest.Key);
                    stop.Add(oldest.Value);
                    log.LogInformation("lsp: stopping {Server} for {Root} to make room", oldest.Key.Server, oldest.Key.Root);
                }
                entry = new Entry(token => StartAsync(server, root, token), _closing.Token);
                _entries[key] = entry;
            }
            entry.Asked = DateTimeOffset.UtcNow;
        }
        foreach (var old in stop) _ = StopAsync(old);
        try { return await entry.Start.WaitAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // a start that failed is forgotten, so the next request tries again (an install that was refused, too)
            var forgotten = false;
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var current) && current == entry) forgotten = _entries.Remove(key);
            }
            if (forgotten) entry.Cancel.Dispose();
            // a start cancelled under the caller (stopped to make room, or NetPI stopping) is this call's error, not the turn's cancellation
            if (ex is OperationCanceledException)
                throw new LspException($"The {server.Id} language server was stopped while it started (more servers were asked for than lsp.maxServers allows, or NetPI is stopping): ask again.");
            throw;
        }
    }

    private async Task<LspSession> StartAsync(LspServer server, string root, CancellationToken ct)
    {
        var dir = await installer.EnsureAsync(server, ct).ConfigureAwait(false);
        var session = await LspSession.StartAsync(server, root, dir, log, ct).ConfigureAwait(false);
        log.LogInformation("lsp: started {Server} for {Root}", server.Id, root);
        return session;
    }

    private static async Task StopAsync(Entry entry)
    {
        try
        {
            entry.Cancel.Cancel();
            await (await entry.Start.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
        catch { /* it never started, or was gone already */ }
        finally { entry.Cancel.Dispose(); }
    }

    private static string Key(string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    /// <summary>Stop the servers nobody asked for in <c>lsp.idleMinutes</c>, and forget the dead ones. A start still
    /// running is left alone.</summary>
    public async Task SweepAsync()
    {
        var stop = new List<Entry>();
        lock (_lock)
        {
            var cutoff = DateTimeOffset.UtcNow - idle();
            foreach (var (key, entry) in _entries.ToList())
            {
                if (!entry.Start.IsCompleted || !entry.Dead && entry.LastUsed >= cutoff) continue;
                _entries.Remove(key);
                stop.Add(entry);
            }
        }
        foreach (var entry in stop) await StopAsync(entry).ConfigureAwait(false);
    }

    /// <summary>How many servers are booked (running or starting).</summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> all;
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            all = [.. _entries.Values];
            _entries.Clear();
        }
        // outside the lock: cancelling runs callbacks inline, and what they continue may want the lock
        _closing.Cancel();
        await Task.WhenAll(all.Select(StopAsync)).ConfigureAwait(false);
    }
}
