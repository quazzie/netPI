using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

// NOTE: mirrored in NetPI.Providers.AiProxy/Common (plugins cannot share code). Keep both copies in sync.
namespace NetPI.Providers.Anthropic;

/// <summary>Maps HTTP / in-stream / network failures to <see cref="ModelException"/> (transient, context overflow...).</summary>
internal static partial class ProviderErrors
{
    private const int MaxBodyChars = 2000;

    private static readonly HashSet<int> TransientStatus = [408, 409, 425, 429, 500, 502, 503, 504, 529];

    private static readonly HashSet<string> TransientTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "backend_unavailable", "overloaded_error", "overloaded", "rate_limit_error", "rate_limit_exceeded",
        "server_error", "api_error", "internal_error", "internal_server_error", "service_unavailable",
        "timeout", "timeout_error", "network_error", "unavailable",
    };

    private static readonly HashSet<string> OverflowTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "context_length_exceeded", "exceed_context_size_error", "context_window_exceeded", "prompt_too_long",
        "model_context_window_exceeded",
    };

    [GeneratedRegex(@"prompt is too long|prompt too long|too many tokens|context[ _-]?(length|window|size)|maximum context|exceeds? (the )?(available |maximum |model'?s? )?(context|token limit)|input is too long|exceed_context|reduce the length|too long for (the )?context|tokens? > \d+ maximum", RegexOptions.IgnoreCase)]
    private static partial Regex OverflowRegex();

    public static bool LooksLikeContextOverflow(string? text) => !string.IsNullOrEmpty(text) && OverflowRegex().IsMatch(text);

    /// <summary>Extract (message, type) from typical error envelopes (OpenAI, Anthropic, llama.cpp, AiProxy, FastAPI).</summary>
    public static (string? Message, string? Type) ExtractError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            return ExtractError(doc.RootElement);
        }
        catch (JsonException) { return (null, null); }
    }

    public static (string? Message, string? Type) ExtractError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return (null, null);
        var err = root.Prop("error");
        if (err.ValueKind == JsonValueKind.String) return (err.GetString(), root.Str("type") is { } t && t != "error" ? t : null);
        if (err.IsObj())
        {
            var type = err.Str("type") ?? err.Str("code");
            if (type is not null && int.TryParse(type, out _)) type = err.Str("code") is { } c && !int.TryParse(c, out _) ? c : null;
            // Prefer a string code when "type" is generic.
            if (type is "invalid_request_error" or "error" && err.Str("code") is { } code && !int.TryParse(code, out _)) type = code;
            return (err.Str("message") ?? err.Str("detail"), type);
        }
        var msg = root.Str("message") ?? root.Str("detail");
        return (msg, root.Str("type") is { } rt && rt != "error" ? rt : root.Str("code"));
    }

    public static ModelException FromHttp(string provider, int status, string? reason, string? body)
    {
        var (message, type) = ExtractError(body);
        var detail = message ?? J.Truncate(body?.Trim(), MaxBodyChars);
        if (string.IsNullOrWhiteSpace(detail)) detail = reason ?? "request failed";
        var text = $"{provider}: HTTP {status}{(type is null ? "" : $" {type}")}: {J.Truncate(detail, MaxBodyChars)}";

        var overflow = (status is 400 or 413 or 422 && LooksLikeContextOverflow(message ?? body))
                       || (type is not null && OverflowTypes.Contains(type));
        var transient = !overflow && (TransientStatus.Contains(status) || (type is not null && TransientTypes.Contains(type)));
        return new ModelException(text, transient, status, type) { ContextOverflow = overflow };
    }

    /// <summary>An error reported inside an otherwise successful stream.</summary>
    public static ModelException FromStream(string provider, string? type, string? message, int? status = null)
    {
        var text = $"{provider}: {(type is null ? "stream error" : type)}: {J.Truncate(message ?? "unknown error", MaxBodyChars)}";
        var overflow = (type is not null && OverflowTypes.Contains(type)) || LooksLikeContextOverflow(message);
        var transient = !overflow && ((type is not null && TransientTypes.Contains(type))
                                      || (status is { } s && TransientStatus.Contains(s))
                                      || (message?.Contains("overloaded", StringComparison.OrdinalIgnoreCase) ?? false));
        return new ModelException(text, transient, status, type) { ContextOverflow = overflow };
    }

    public static ModelException UnexpectedEnd(string provider) =>
        new($"{provider}: the response stream ended unexpectedly", transient: true, null, "stream_truncated");

    /// <summary>Translate a transport exception. User cancellation is re-thrown as <see cref="OperationCanceledException"/>.</summary>
    public static Exception Translate(Exception ex, string provider, CancellationToken ct)
    {
        if (ex is ModelException) return ex;
        if (ct.IsCancellationRequested)
            return ex as OperationCanceledException ?? new OperationCanceledException("The model call was cancelled.", ex, ct);
        return ex switch
        {
            OperationCanceledException => new ModelException($"{provider}: request timed out", true, null, "timeout", ex),
            HttpRequestException or IOException or SocketException or ObjectDisposedException =>
                new ModelException($"{provider}: connection error: {Flatten(ex)}", true, null, "network_error", ex),
            _ => ex,
        };
    }

    private static string Flatten(Exception ex)
    {
        var msg = ex.Message;
        if (ex.InnerException is { } inner && !msg.Contains(inner.Message, StringComparison.Ordinal)) msg += " (" + inner.Message + ")";
        return msg;
    }

    public static async Task<string> ReadBodySafeAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return s.Length > 16_000 ? s[..16_000] : s;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return ""; }
    }

    /// <summary>Iterate SSE events translating transport failures into <see cref="ModelException"/>s.</summary>
    public static async IAsyncEnumerable<SseEvent> Guard(IAsyncEnumerable<SseEvent> source, string provider,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var e = source.GetAsyncEnumerator(ct);
        while (true)
        {
            bool has;
            try { has = await e.MoveNextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not ModelException)
            {
                var t = Translate(ex, provider, ct);
                if (ReferenceEquals(t, ex)) throw;
                throw t;
            }
            if (!has) yield break;
            yield return e.Current;
        }
    }
}
