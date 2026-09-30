using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Mcp;

internal sealed class McpHttpException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

internal sealed class HttpTransport : IMcpTransport
{
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ServerConfig _config;
    private readonly int _limit;
    private readonly CancellationTokenSource _stop = new();
    private string? _session;
    private long _next;
    private int _disposed;
    public event Action<JsonObject>? Notification;
    public event Action<Exception>? Closed;
    public string Version { get; set; } = Protocol.Modern;

    public HttpTransport(ServerConfig config, int limit) { _config = config; _limit = limit; }
    private HttpRequestMessage Http(HttpMethod method, JsonObject? message = null, JsonObject? parameterHeaders = null)
    {
        var request = new HttpRequestMessage(method, _config.Url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", Version);
        if (Version == Protocol.Legacy && _session is not null) request.Headers.Add("Mcp-Session-Id", _session);
        foreach (var (name, source) in _config.HeaderEnv)
        {
            var value = _config.Secret(source);
            if (value.IndexOfAny(['\r', '\n']) >= 0) throw new McpException("Credential headers must not contain newlines.");
            request.Headers.Add(name, value);
        }
        if (message is not null)
        {
            var json = message.ToJsonString();
            if (json.Length > _limit) throw new McpException("MCP request exceeds the configured size limit.");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (Version == Protocol.Modern)
            {
                request.Headers.Add("Mcp-Method", message["method"]!.GetValue<string>());
                var name = message["params"]?["name"] ?? message["params"]?["uri"];
                if (name is not null) request.Headers.Add("Mcp-Name", HeaderValue(name.GetValue<string>()));
                if (parameterHeaders is not null)
                    foreach (var (key, value) in parameterHeaders) request.Headers.Add("Mcp-Param-" + key, HeaderValue(value!.GetValue<string>()));
            }
        }
        return request;
    }
    internal static string HeaderValue(string value) =>
        value.Any(c => (c < 32 && c != '\t') || c > 126) || value.Trim() != value
            || (value.StartsWith("=?base64?", StringComparison.Ordinal) && value.EndsWith("?=", StringComparison.Ordinal))
            ? "=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=" : value;
    public async Task<JsonObject> RequestAsync(string method, JsonObject? parameters, CancellationToken ct, JsonObject? headers = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var id = Interlocked.Increment(ref _next);
        using var request = Http(HttpMethod.Post, Protocol.Message(method, parameters, id, Version), headers);
        try
        {
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        await CheckAsync(response, linked.Token).ConfigureAwait(false);
        if (method == "initialize" && response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) _session = sessions.Single();
        var result = await ReadAsync(response, id, listening: false, linked.Token).ConfigureAwait(false);
        return result ?? throw new McpException("MCP stream closed without a response. The tool was not replayed.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && Version == Protocol.Legacy)
        {
            try
            {
                using var signal = new CancellationTokenSource(500);
                await NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "Caller cancelled" }, signal.Token).ConfigureAwait(false);
            }
            catch { }
            throw;
        }
    }
    public async Task NotifyAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        using var request = Http(HttpMethod.Post, Protocol.Message(method, parameters, null, Version));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        await CheckAsync(response, linked.Token).ConfigureAwait(false);
    }
    private async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new McpHttpException(response.StatusCode, $"MCP authentication failed (HTTP {(int)response.StatusCode}). Check the configured environment-backed credentials.");
        if (Version == Protocol.Legacy && _session is not null && response.StatusCode == HttpStatusCode.NotFound)
        {
            var ex = new McpHttpException(response.StatusCode, "MCP session expired. Reconnect before retrying; the tool was not replayed.");
            Closed?.Invoke(ex); throw ex;
        }
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
            var body = await ReadBodyAsync(reader, ct).ConfigureAwait(false);
            try { Protocol.Result(Protocol.Parse(body, _limit)); } catch (McpException) { throw; } catch { }
        }
        throw new McpHttpException(response.StatusCode, $"MCP endpoint returned HTTP {(int)response.StatusCode}. No tool call was replayed.");
    }
    private async Task<string> ReadBodyAsync(TextReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        while (await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false) is var n && n > 0)
        {
            if (text.Length + n > _limit) throw new McpException("MCP response exceeds the configured size limit.");
            text.Append(buffer, 0, n);
        }
        return text.ToString();
    }
    private async Task<JsonObject?> ReadAsync(HttpResponseMessage response, long id, bool listening, CancellationToken ct)
    {
        var type = response.Content.Headers.ContentType?.MediaType;
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        if (type == "application/json")
        {
            var message = Protocol.Parse(await ReadBodyAsync(reader, ct).ConfigureAwait(false), _limit);
            if (Protocol.Id(message["id"]) != id.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new McpException("Mismatched MCP response id.");
            return Protocol.Result(message);
        }
        if (type != "text/event-stream") throw new McpException("MCP response must be application/json or text/event-stream.");
        var data = new StringBuilder();
        var subscription = new Subscription();
        var count = 0; var since = DateTime.UtcNow;
        while (await Protocol.ReadLineAsync(reader, _limit, ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length == 0) continue;
                var message = Protocol.Parse(data.ToString().TrimEnd('\n'), _limit); data.Clear();
                if ((DateTime.UtcNow - since).TotalSeconds > 1) { since = DateTime.UtcNow; count = 0; }
                if (++count > 1024) throw new McpException("MCP notification rate exceeded the client limit.");
                if (message["method"] is not null)
                {
                    if (message["id"] is not null)
                    {
                        if (Version == Protocol.Modern) throw new McpException("Modern MCP streams must not contain server requests.");
                        using var reply = Http(HttpMethod.Post);
                        reply.Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(),
                            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Client method not supported." } }.ToJsonString(), Encoding.UTF8, "application/json");
                        using var ignored = await _client.SendAsync(reply, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    }
                    else if (Version == Protocol.Legacy || listening && subscription.Accept(message, id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                        Notification?.Invoke(message);
                }
                else if (Protocol.Id(message["id"]) == id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    return Protocol.Result(message);
                else throw new McpException("Mismatched MCP stream response id.");
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..]; if (value.StartsWith(' ')) value = value[1..];
                if (data.Length + value.Length + 1 > _limit) throw new McpException("MCP SSE event exceeds the configured size limit.");
                data.Append(value).Append('\n');
            }
            // SSE comments/event/id/retry fields have no JSON payload.
        }
        return null;
    }
    public async Task ListenAsync(bool modern, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var id = Interlocked.Increment(ref _next);
        using var request = modern ? Http(HttpMethod.Post, Protocol.Message("subscriptions/listen", new JsonObject { ["notifications"] = new JsonObject { ["toolsListChanged"] = true } }, id, Version)) : Http(HttpMethod.Get);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        if (!modern && response.StatusCode == HttpStatusCode.MethodNotAllowed) return;
        await CheckAsync(response, linked.Token).ConfigureAwait(false);
        await ReadAsync(response, id, listening: true, linked.Token).ConfigureAwait(false);
        throw new McpException("MCP notification stream ended.");
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        if (Version == Protocol.Legacy && _session is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(500);
                using var request = Http(HttpMethod.Delete);
                using var response = await _client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch { }
        }
        _client.Dispose();
        _stop.Dispose();
    }
}
