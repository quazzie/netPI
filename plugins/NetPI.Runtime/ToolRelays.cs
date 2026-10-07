using System.Collections.Concurrent;

namespace NetPI.Runtime;

/// <summary>
/// The relays of the model calls in flight, one per session (<see cref="IToolRelay"/>, idea-0qo9xb). The runner opens one
/// around each model call and closes it when the call's stream ends, so a provider can relay a call to a NetPI tool only
/// while the turn it belongs to is the one running; afterwards (or for a session with no run) the call is refused.
/// </summary>
internal sealed class ToolRelays : IToolRelay
{
    private readonly ConcurrentDictionary<string, Func<ToolCallPart, CancellationToken, Task<ToolResult>>> _open = new(StringComparer.Ordinal);

    public IDisposable Open(string sessionId, Func<ToolCallPart, CancellationToken, Task<ToolResult>> run)
    {
        _open[sessionId] = run;
        return new Closer(() => _open.TryRemove(new KeyValuePair<string, Func<ToolCallPart, CancellationToken, Task<ToolResult>>>(sessionId, run)));
    }

    public async Task<ToolResult?> RunAsync(string sessionId, ToolCallPart call, CancellationToken ct) =>
        _open.TryGetValue(sessionId, out var run) ? await run(call, ct).ConfigureAwait(false) : null;

    private sealed class Closer(Action close) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) close();
        }
    }
}
