using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace NetPI;

/// <summary>
/// Dynamic service registry shared by all plugins. Services are registered by contract type
/// (an interface from NetPI.Abstractions). Do not cache resolved services for long: a plugin that
/// provides a service may be reloaded, so resolve per use.
/// </summary>
public interface IServiceRegistry
{
    /// <summary>Register an implementation. Higher priority wins for <see cref="Get{T}"/>.</summary>
    IDisposable Register<T>(T instance, int priority = 0) where T : class;
    /// <summary>The highest-priority implementation, or null.</summary>
    T? Get<T>() where T : class;
    T Require<T>() where T : class => Get<T>() ?? throw new InvalidOperationException($"Service {typeof(T).Name} is not available (is its plugin loaded?)");
    /// <summary>All implementations, highest priority first.</summary>
    IReadOnlyList<T> GetAll<T>() where T : class;
}

// ---------------------------------------------------------------- RPC

public delegate Task<object?> RpcHandler(RpcRequest request, CancellationToken ct);

public sealed class RpcRequest
{
    public required string Method { get; init; }
    public JsonElement Params { get; init; }
    /// <summary>Websocket client id when called from the UI, null for in-process calls.</summary>
    public string? ClientId { get; init; }
    /// <summary>
    /// The parameters the method declared (<see cref="RpcMethod.Params"/>), or null for a method registered without them.
    /// A handler that reads a name outside the declaration fails at once — a typo in a parameter name is found by the
    /// first test that calls the method, not by a user whose filter is silently ignored.
    /// </summary>
    public IReadOnlyList<RpcParam>? Declared { get; init; }

    private void Check(string name)
    {
        if (Declared is { } declared && !declared.Any(p => p.Name == name))
            throw new InvalidOperationException($"{Method} reads parameter '{name}', which it does not declare");
    }

    public T Bind<T>() where T : new()
    {
        if (Params.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new T();
        return Params.Deserialize<T>(NetPiJson.For(typeof(T))) ?? new T();
    }

    public string? Str(string name)
    {
        Check(name);
        return Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;
    }

    public string Required(string name) => Str(name) ?? throw new RpcException("bad_request", $"Missing parameter '{name}'");

    /// <summary>
    /// A number parameter, or null. A model that quotes one — <c>{"max": "2"}</c>, which it does readily for a value
    /// nested inside an object — means the number: the settings document is read the same way
    /// (<c>JsonNumberHandling.AllowReadingFromString</c>). Refusing it instead would ignore the caller's filter in
    /// silence, and a filter that is silently ignored is worse than one that errors.
    /// </summary>
    public int? Int(string name)
    {
        Check(name);
        return Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
                _ => null,
            }
            : null;
    }

    /// <summary>A boolean parameter, or null; <c>"true"</c> counts as <c>true</c> (see <see cref="Int"/>).</summary>
    public bool? Bool(string name)
    {
        Check(name);
        return Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : null,
                _ => null,
            }
            : null;
    }

    public JsonElement? Prop(string name)
    {
        Check(name);
        return Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty(name, out var v) ? v : null;
    }
}

public sealed class RpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record RpcMethodInfo(string Method, string? Description, string PluginId, bool ReadOnly = false, IReadOnlyList<RpcParam>? Params = null);

/// <summary>The JSON types a declared RPC parameter may have (<see cref="RpcParam.Type"/>).</summary>
public static class RpcParamType
{
    public const string String = "string";
    public const string Integer = "integer";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Object = "object";
    public const string Array = "array";
    public const string Any = "any";
}

/// <summary>One declared parameter of an RPC method: what <c>rpc.list</c> shows and what a request is checked against.</summary>
public sealed record RpcParam(string Name, string Type = RpcParamType.String, bool Required = false, string? Description = null)
{
    public static RpcParam Req(string name, string type = RpcParamType.String, string? description = null) => new(name, type, true, description);
    public static RpcParam Opt(string name, string type = RpcParamType.String, string? description = null) => new(name, type, false, description);
}

/// <summary>
/// An RPC method with its parameters declared (idea-yvcy8b). A request is checked against them before the handler runs:
/// an unknown parameter, a missing required one or one of the wrong type is a <c>bad_request</c> that names it, and a
/// handler that reads a name it did not declare fails on its first call (<see cref="RpcRequest.Declared"/>) instead of
/// quietly reading nothing. <see cref="Params"/> null is a method registered the older way: no checks.
/// </summary>
public sealed record RpcMethod(string Name, string? Description = null, bool ReadOnly = false, IReadOnlyList<RpcParam>? Params = null)
{
    /// <summary>The first problem with these parameters, or null when they fit the declaration.</summary>
    public string? Validate(JsonElement parameters)
    {
        if (Params is null) return null;
        if (parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return Params.FirstOrDefault(p => p.Required) is { } missing ? $"Missing parameter '{missing.Name}'" : null;
        if (parameters.ValueKind != JsonValueKind.Object) return "parameters must be a JSON object";
        foreach (var prop in parameters.EnumerateObject())
            if (!Params.Any(p => p.Name == prop.Name))
                return $"unknown parameter '{prop.Name}' (takes {(Params.Count == 0 ? "none" : string.Join(", ", Params.Select(p => p.Name)))})";
        foreach (var p in Params)
        {
            var present = parameters.TryGetProperty(p.Name, out var v) && v.ValueKind != JsonValueKind.Null;
            if (!present) { if (p.Required) return $"Missing parameter '{p.Name}'"; continue; }
            if (!Fits(p.Type, v)) return $"Parameter '{p.Name}' must be {Article(p.Type)}";
        }
        return null;
    }

    private static bool Fits(string type, JsonElement v) => type switch
    {
        RpcParamType.String => v.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array),
        RpcParamType.Integer => v.ValueKind == JsonValueKind.Number ? v.TryGetInt64(out _)
            : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        RpcParamType.Number => v.ValueKind == JsonValueKind.Number
            || v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        RpcParamType.Boolean => v.ValueKind is JsonValueKind.True or JsonValueKind.False
            || v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out _),
        RpcParamType.Object => v.ValueKind == JsonValueKind.Object,
        RpcParamType.Array => v.ValueKind == JsonValueKind.Array,
        _ => true,
    };

    private static string Article(string type) => type switch
    {
        RpcParamType.Integer => "an integer",
        RpcParamType.Object => "an object",
        RpcParamType.Array => "an array",
        _ => "a " + type,
    };
}

public interface IRpcRegistry
{
    /// <summary>
    /// Register a method. Unmarked means "may write": the read-only paths (<c>scripts/netpi.mjs</c>, the <c>diag</c>
    /// tool's rpc action) call only what the other overload marks, so a method that changes something has to say so to
    /// be reachable from a tool (idea-de1s7t).
    /// </summary>
    IDisposable Register(string method, RpcHandler handler, string? description = null);
    /// <summary>Register a method that only reads, so the read-only paths may call it (reported by <c>rpc.list</c>).</summary>
    IDisposable Register(string method, RpcHandler handler, string? description, bool readOnly);
    /// <summary>
    /// Register a method with its parameters declared: requests are checked against them before the handler runs, and
    /// <c>rpc.list</c> shows them. A registry that does not check (a test fake) registers it the older way.
    /// </summary>
    IDisposable Register(RpcMethod method, RpcHandler handler) => Register(method.Name, handler, method.Description, method.ReadOnly);
    /// <summary>Invoke a method in-process (plugins can call each other through RPC).</summary>
    Task<object?> InvokeAsync(string method, object? parameters = null, CancellationToken ct = default);
    IReadOnlyList<RpcMethodInfo> List();
    bool Exists(string method);
}

/// <summary>One way to say "this method only reads", so the claim is made the same way everywhere.</summary>
public static class RpcRegistryExtensions
{
    /// <summary>
    /// Register a method that only reads, so the read-only paths (<c>scripts/netpi.mjs</c>, the <c>diag</c> tool's rpc
    /// action) may call it: they trust <c>rpc.list</c>'s <c>readOnly</c> flag and nothing else, so a read surface that
    /// is not marked here is a read surface a tool cannot reach (idea-o934y1).
    /// <para>It is a claim, not a check: if the handler writes anything, do not use it.</para>
    /// </summary>
    public static IDisposable RegisterReadOnly(this IRpcRegistry rpc, string method, RpcHandler handler, string? description = null) =>
        rpc.Register(method, handler, description, readOnly: true);
}

// ---------------------------------------------------------------- HTTP

/// <summary>Plugin HTTP endpoints, served under <c>/api/p/{pluginId}/{path}</c>.</summary>
public interface IHttpRegistry
{
    /// <summary>
    /// Serve <paramref name="path"/>. By default the host lets a request through only with its token and from its own
    /// origin, like every <c>/api</c> call. <paramref name="open"/>: the host checks neither, and the handler
    /// authenticates the request itself — for a client that cannot hold the host's per-run token or comes from another
    /// origin (a browser extension with a secret of its own). The server still listens on loopback only.
    /// </summary>
    IDisposable Map(string path, Func<HttpContext, Task> handler, bool open = false);
}

// ---------------------------------------------------------------- UI

/// <summary>
/// Where a plugin tab shows. <see cref="Left"/> and <see cref="Right"/>: the side panels (narrow, 230–320 px).
/// <see cref="Session"/>: a view of one chat, in the chat's own area (wide), switched on from the chat header or by the
/// <c>ui.open</c> event; its <c>ctx.sessionId</c> is the chat it shows.
/// </summary>
public enum UiPanel { Left, Right, Session }

public sealed class UiTabInfo
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public UiPanel Panel { get; init; } = UiPanel.Right;
    /// <summary>Icon name from the UI icon set (e.g. "work", "idea", "bug", "files", "list") or an inline SVG string.</summary>
    public string? Icon { get; init; }
    /// <summary>ES module path relative to the plugin's wwwroot (default "ui.js"). Must export mount(el, ctx).</summary>
    public string Module { get; init; } = "ui.js";
    /// <summary>Optional named export when one module hosts several tabs (called as module[Export](el, ctx)).</summary>
    public string? Export { get; init; }
    public int Order { get; init; } = 100;
    // Filled in by the host:
    public string? PluginId { get; set; }
    public string? Version { get; set; }
}

public sealed class SlashCommandInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>RPC method invoked with { sessionId, args }. Its string result (if any) is shown as a toast.</summary>
    public string? Rpc { get; init; }
    /// <summary>Client-side action instead of RPC, e.g. "openTab:netpi.ideas/ideas" or "insert:text".</summary>
    public string? ClientAction { get; init; }
    public string? ArgsHint { get; init; }
    public string? PluginId { get; set; }
}

public interface IUiRegistry
{
    IDisposable AddTab(UiTabInfo tab);
    IDisposable AddCommand(SlashCommandInfo command);
    IReadOnlyList<UiTabInfo> Tabs { get; }
    IReadOnlyList<SlashCommandInfo> Commands { get; }
}
