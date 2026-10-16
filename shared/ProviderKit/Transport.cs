// Compiled into each provider plugin from shared/ProviderKit (plugins do not reference each other): edit it here.
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace NetPI.Providers.Kit;

/// <summary>
/// The parts of a provider's streaming path that are dialect-agnostic: the headers every provider sends the same
/// way, the translation of a transport exception, and the <c>StreamAsync</c> wrapper that extends a failure with
/// the server's ids and the path of the saved request. The dialect-specific parts (which key header, which
/// transport label) stay in each provider.
/// </summary>
internal static class HttpTransport
{
    /// <summary>The bearer key, when the provider has one.</summary>
    public static void ApplyBearer(HttpRequestMessage req, string? apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>The provider's custom headers, with <c>env:</c>/$NAME secrets resolved the same way everywhere.</summary>
    public static void ApplyCustomHeaders(HttpRequestMessage req, IEnumerable<KeyValuePair<string, string>> headers)
    {
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, SettingsExtensions.ResolveSecret(v) ?? v);
    }

    /// <summary>A transport exception becomes a provider error; user cancellation comes back as itself.</summary>
    public static Exception Rethrow(Exception ex, string provider, CancellationToken ct)
    {
        var t = ProviderErrors.Translate(ex, provider, ct);
        if (ReferenceEquals(t, ex)) ExceptionDispatchInfo.Capture(ex).Throw();
        return t;
    }

    /// <summary>
    /// <paramref name="core"/> with the failure description: a <see cref="ModelException"/> is extended with the
    /// server's request/response ids, and the request body is saved so the failure can be reproduced (idea-saljbd).
    /// </summary>
    public static async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        Func<ModelRequest, CallInfo, CancellationToken, IAsyncEnumerable<ModelStreamEvent>> core,
        ModelRequest request, CallInfo call, string providerId, string? dumpDir, ILogger? log,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var e = core(request, call, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            ModelStreamEvent current;
            try
            {
                if (!await e.MoveNextAsync().ConfigureAwait(false)) yield break;
                current = e.Current;
            }
            catch (ModelException ex) when (!ct.IsCancellationRequested)
            {
                throw await FailedRequests.DescribeAsync(ex, request, call, providerId, dumpDir, log, ct).ConfigureAwait(false);
            }
            yield return current;
        }
    }
}
