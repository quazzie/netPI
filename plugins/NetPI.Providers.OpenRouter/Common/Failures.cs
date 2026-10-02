using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

// NOTE: mirrored in NetPI.Providers.{AiProxy,Anthropic,OpenRouter}/Common (plugins cannot share code). Keep the copies in sync.
namespace NetPI.Providers.OpenRouter;

/// <summary>What one call sent and got back, for error messages and failed-request dumps.</summary>
internal sealed class CallInfo
{
    public string? Url;
    public string? Transport;
    public JsonObject? Body;
    public string? RequestId;
    /// <summary>The server's id for the response in flight. Every transport reports one (Responses:
    /// <c>response.id</c>, Chat: the completion id, OpenRouter: the generation id, Anthropic: the message id); a
    /// backend with no <c>x-request-id</c> header offers only this.</summary>
    public string? ResponseId;
    /// <summary>How the id is named in the message: OpenRouter calls it a generation, the others a response.</summary>
    public string ResponseIdLabel = "response";
    /// <summary>The response body as received, capped (16k) by <see cref="ProviderErrors.ReadBodySafeAsync"/>. The
    /// dump used to keep only the 2,000-char excerpt the error message carries, so a cause outside
    /// <c>error.message</c> was unreproducible from the file (idea-022jh1).</summary>
    public string? ResponseBody;
    public bool Dump;
}

/// <summary>The failed-request evidence: the server's ids in the message, and the request body saved beside it.</summary>
internal static class FailedRequests
{
    /// <summary>How many dumps are kept; the newest win.</summary>
    public const int Kept = 30;

    /// <summary>Errors worth no dump: the call never reached a backend, the credentials were refused, or the server
    /// is throttling us. Saving the last of those rotates the dump someone wanted out within a few calls, and it
    /// costs a write on the path that reports the failure (idea-saljbd).</summary>
    private static bool WorthDumping(ModelException ex) => ex.StatusCode != 429
        && ex.ErrorType is not ("network_error" or "provider_disabled" or "invalid_config" or "authentication_error"
            or "rate_limit_error" or "rate_limit_exceeded" or "overloaded_error" or "overloaded");

    /// <summary>
    /// The failure with the server's ids and the path of the saved request. The ids stay in the message (the log,
    /// diag and a bug report want them) and are handed over as Detail, so the retry notice can show the reason
    /// without them (idea-qz1a5z).
    /// </summary>
    public static async Task<ModelException> DescribeAsync(ModelException ex, ModelRequest request, CallInfo call,
        string providerId, string? dumpDir, ILogger? log, CancellationToken ct)
    {
        var ids = new List<string>();
        if (call.RequestId is { Length: > 0 } rq) ids.Add("request " + rq);
        if (call.ResponseId is { Length: > 0 } rs) ids.Add($"{call.ResponseIdLabel} {rs}");
        if (call.Dump && call.Body is not null && WorthDumping(ex)
            && await DumpAsync(ex, request, call, providerId, dumpDir, log, ct).ConfigureAwait(false) is { } dump)
            ids.Add("saved " + dump);
        if (ids.Count == 0) return ex;
        var detail = string.Join(", ", ids);
        return new ModelException($"{ex.Message} [{detail}]", ex.Transient, ex.StatusCode, ex.ErrorType, ex)
        {
            ContextOverflow = ex.ContextOverflow,
            RetryAfter = ex.RetryAfter,
            Detail = detail,
        };
    }

    /// <summary>Save the request (and the response body) as one JSON file; null when there is nowhere to save it.</summary>
    private static async Task<string?> DumpAsync(ModelException ex, ModelRequest request, CallInfo call,
        string providerId, string? dumpDir, ILogger? log, CancellationToken ct)
    {
        if (dumpDir is null) return null;
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Safe(request.Model.Id)}.json";
        var path = Path.Combine(dumpDir, name);
        var doc = new JsonObject
        {
            ["time"] = DateTimeOffset.Now.ToString("O"),
            ["provider"] = providerId,
            ["model"] = request.Model.Id,
            ["sessionId"] = request.SessionId,
            ["url"] = call.Url,
            ["transport"] = call.Transport,
            ["requestId"] = call.RequestId,
            ["responseId"] = call.ResponseId,
            ["error"] = new JsonObject { ["message"] = ex.Message, ["type"] = ex.ErrorType, ["status"] = ex.StatusCode },
            ["request"] = call.Body!.DeepClone(),
        };
        if (!string.IsNullOrEmpty(call.ResponseBody)) doc["response"] = call.ResponseBody;
        try
        {
            Directory.CreateDirectory(dumpDir);
            await File.WriteAllTextAsync(path, doc.ToJsonString(NetPiJson.Indented), ct).ConfigureAwait(false);
            foreach (var old in new DirectoryInfo(dumpDir).GetFiles("*.json").OrderByDescending(f => f.Name).Skip(Kept))
                try { old.Delete(); } catch { /* another call rotated it already */ }
            return path;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log?.LogDebug(e, "{Provider}: could not save the failed request", providerId);
            return null;
        }

        static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
    }
}

/// <summary>
/// Stream frames the parser could not read. They used to be dropped in silence, so a mangled <c>message_delta</c>
/// (the stop reason and the final usage) vanished and the call "completed" with <c>message_start</c>'s one-token
/// usage: the ledger under-recorded it and nothing said why (idea-saljbd).
/// </summary>
internal sealed class DroppedFrames
{
    private const int MaxFirstChars = 200;
    private int _count;
    private string? _first;

    public void Add(string data)
    {
        if (_count++ == 0) _first = data.Length > MaxFirstChars ? data[..MaxFirstChars] : data;
    }

    /// <summary>"3 malformed events, first: …", or null while every frame parsed.</summary>
    public string? Note => _count == 0 ? null
        : $"{_count} malformed event{(_count == 1 ? "" : "s")}, first: {(_first ?? "").Trim()}";
}