using System.Text.Json;

namespace NetPI.Host.Registries;

/// <summary>
/// RPC method registry. Registering an existing method stacks the new handler on top; disposing it restores the
/// previous one (so a plugin can override a core method while it is loaded).
/// </summary>
internal sealed class RpcRegistry : IRpcRegistry
{
    private sealed record Entry(string Method, RpcHandler Handler, string? Description, string Owner);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Entry>> _methods = new(StringComparer.Ordinal);

    public IDisposable Register(string method, RpcHandler handler, string? description = null) =>
        Register(method, handler, description, "host");

    public IDisposable Register(string method, RpcHandler handler, string? description, string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);
        var entry = new Entry(method.Trim(), handler, description, owner);
        lock (_gate)
        {
            if (!_methods.TryGetValue(entry.Method, out var stack)) _methods[entry.Method] = stack = [];
            stack.Add(entry);
        }
        return new Registration(() =>
        {
            lock (_gate)
            {
                if (!_methods.TryGetValue(entry.Method, out var stack)) return;
                stack.Remove(entry);
                if (stack.Count == 0) _methods.Remove(entry.Method);
            }
        });
    }

    public Task<object?> InvokeAsync(string method, object? parameters = null, CancellationToken ct = default) =>
        InvokeAsync(method, ToParams(parameters), null, ct);

    public async Task<object?> InvokeAsync(string method, JsonElement parameters, string? clientId, CancellationToken ct)
    {
        RpcHandler handler;
        lock (_gate)
        {
            if (!_methods.TryGetValue(method, out var stack) || stack.Count == 0)
                throw new RpcException("not_found", $"Unknown RPC method '{method}'");
            handler = stack[^1].Handler;
        }
        return await handler(new RpcRequest { Method = method, Params = parameters, ClientId = clientId }, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<RpcMethodInfo> List()
    {
        lock (_gate)
            return _methods.Values
                .Where(s => s.Count > 0)
                .Select(s => s[^1])
                .OrderBy(e => e.Method, StringComparer.Ordinal)
                .Select(e => new RpcMethodInfo(e.Method, e.Description, e.Owner))
                .ToList();
    }

    public bool Exists(string method)
    {
        lock (_gate) return _methods.TryGetValue(method, out var s) && s.Count > 0;
    }

    private static JsonElement ToParams(object? parameters) => parameters switch
    {
        null => default,
        JsonElement e => e,
        _ => NetPiJson.ToElement(parameters),
    };
}
