using System.Text.Json;

namespace NetPI.Host.Registries;

/// <summary>
/// RPC method registry. Registering an existing method stacks the new handler on top; disposing it restores the
/// previous one (so a plugin can override a core method while it is loaded).
/// </summary>
internal sealed class RpcRegistry : IRpcRegistry
{
    private sealed record Entry(string Method, RpcHandler Handler, string? Description, string Owner, bool ReadOnly, IReadOnlyList<RpcParam>? Params = null);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Entry>> _methods = new(StringComparer.Ordinal);
    public event Action<string>? Changed;

    public IDisposable Register(string method, RpcHandler handler, string? description = null) =>
        Register(method, handler, description, "host", false);

    public IDisposable Register(string method, RpcHandler handler, string? description, bool readOnly) =>
        Register(method, handler, description, "host", readOnly);

    public IDisposable Register(RpcMethod method, RpcHandler handler) => Register(method, handler, "host");

    public IDisposable Register(string method, RpcHandler handler, string? description, string owner, bool readOnly = false) =>
        Register(new RpcMethod(method, description, readOnly), handler, owner);

    /// <summary>
    /// Register a method, its declared parameters checked here: names unique and non-empty, types known. A declaration
    /// that is wrong is the plugin's bug, so it fails the registration (the plugin's start) rather than a user's call.
    /// </summary>
    public IDisposable Register(RpcMethod method, RpcHandler handler, string owner)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(method.Name);
        ArgumentNullException.ThrowIfNull(handler);
        if (method.Params is { } declared)
        {
            var types = new[] { RpcParamType.String, RpcParamType.Integer, RpcParamType.Number, RpcParamType.Boolean, RpcParamType.Object, RpcParamType.Array, RpcParamType.Any };
            foreach (var p in declared)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) throw new ArgumentException($"{method.Name}: a parameter without a name");
                if (!types.Contains(p.Type)) throw new ArgumentException($"{method.Name}: parameter '{p.Name}' has the unknown type '{p.Type}'");
            }
            if (declared.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1) is { } twice)
                throw new ArgumentException($"{method.Name}: parameter '{twice.Key}' is declared twice");
        }
        var entry = new Entry(method.Name.Trim(), handler, method.Description, owner, method.ReadOnly, method.Params);
        lock (_gate)
        {
            if (!_methods.TryGetValue(entry.Method, out var stack)) _methods[entry.Method] = stack = [];
            stack.Add(entry);
        }
        Changed?.Invoke(entry.Method);
        return new Registration(() =>
        {
            lock (_gate)
            {
                if (!_methods.TryGetValue(entry.Method, out var stack)) return;
                stack.Remove(entry);
                if (stack.Count == 0) _methods.Remove(entry.Method);
            }
            Changed?.Invoke(entry.Method);
        });
    }

    public Task<object?> InvokeAsync(string method, object? parameters = null, CancellationToken ct = default) =>
        InvokeAsync(method, ToParams(parameters), null, ct);

    public async Task<object?> InvokeAsync(string method, JsonElement parameters, string? clientId, CancellationToken ct)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_methods.TryGetValue(method, out var stack) || stack.Count == 0)
                throw new RpcException("not_found", $"Unknown RPC method '{method}'");
            entry = stack[^1];
        }
        // A declared method is checked before its handler runs (the UI, the CLI and other plugins alike).
        if (entry.Params is not null && new RpcMethod(method, Params: entry.Params).Validate(parameters) is { } problem)
            throw new RpcException("bad_request", $"{method}: {problem}");
        return await entry.Handler(new RpcRequest { Method = method, Params = parameters, ClientId = clientId, Declared = entry.Params }, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<RpcMethodInfo> List()
    {
        lock (_gate)
            return _methods.Values
                .Where(s => s.Count > 0)
                .Select(s => s[^1])
                .OrderBy(e => e.Method, StringComparer.Ordinal)
                .Select(e => new RpcMethodInfo(e.Method, e.Description, e.Owner, e.ReadOnly, e.Params))
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
