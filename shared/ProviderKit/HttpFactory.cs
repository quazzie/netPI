// Compiled into each provider plugin from shared/ProviderKit (plugins do not reference each other): edit it here.
using System.Net;

namespace NetPI.Providers.Kit;

internal static class HttpFactory
{
    public const long MaxBufferedResponseBytes = 64 * 1024 * 1024;

    /// <summary>
    /// One client per plugin instance: pooled connections recycled every 5 minutes, no overall timeout
    /// (servers may hold a request for minutes; cancellation is driven by the caller's token).
    /// Loopback hosts never go through a system/env proxy.
    /// </summary>
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            Proxy = new LoopbackBypassProxy(HttpClient.DefaultProxy),
            UseProxy = true,
        };
        // Buffered reads (an error body, a non-streamed JSON answer, a model list) stop at this size instead of the
        // 2 GB default; a streamed answer is not buffered and is unaffected.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxBufferedResponseBytes };
    }

    private sealed class LoopbackBypassProxy(IWebProxy inner) : IWebProxy
    {
        public ICredentials? Credentials { get => inner.Credentials; set => inner.Credentials = value; }
        public Uri? GetProxy(Uri destination) => inner.GetProxy(destination);
        public bool IsBypassed(Uri host) => host.IsLoopback || inner.IsBypassed(host);
    }
}
