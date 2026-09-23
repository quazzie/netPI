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

    public T Bind<T>() where T : new()
    {
        if (Params.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new T();
        return Params.Deserialize<T>(NetPiJson.For(typeof(T))) ?? new T();
    }

    public string? Str(string name) =>
        Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    public string Required(string name) => Str(name) ?? throw new RpcException("bad_request", $"Missing parameter '{name}'");

    public int? Int(string name) =>
        Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    public bool? Bool(string name) =>
        Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public JsonElement? Prop(string name) =>
        Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty(name, out var v) ? v : null;
}

public sealed class RpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record RpcMethodInfo(string Method, string? Description, string PluginId);

public interface IRpcRegistry
{
    IDisposable Register(string method, RpcHandler handler, string? description = null);
    /// <summary>Invoke a method in-process (plugins can call each other through RPC).</summary>
    Task<object?> InvokeAsync(string method, object? parameters = null, CancellationToken ct = default);
    IReadOnlyList<RpcMethodInfo> List();
    bool Exists(string method);
}

// ---------------------------------------------------------------- HTTP

/// <summary>Plugin HTTP endpoints, served under <c>/api/p/{pluginId}/{path}</c>.</summary>
public interface IHttpRegistry
{
    IDisposable Map(string path, Func<HttpContext, Task> handler);
}

// ---------------------------------------------------------------- UI

public enum UiPanel { Left, Right }

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
