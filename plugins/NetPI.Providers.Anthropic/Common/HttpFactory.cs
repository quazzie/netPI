using System.Net;

// NOTE: mirrored in NetPI.Providers.{AiProxy,Anthropic,OpenRouter}/Common (plugins cannot share code). Keep the copies in sync.
namespace NetPI.Providers.Anthropic;

internal static class HttpFactory
{
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
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class LoopbackBypassProxy(IWebProxy inner) : IWebProxy
    {
        public ICredentials? Credentials { get => inner.Credentials; set => inner.Credentials = value; }
        public Uri? GetProxy(Uri destination) => inner.GetProxy(destination);
        public bool IsBypassed(Uri host) => host.IsLoopback || inner.IsBypassed(host);
    }
}
