using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetPI.Host.Web;

/// <summary>
/// What the server listens on (<see cref="NetPiServerOptions.Bind"/> / the setting <c>server.bind</c>):
/// <c>loopback</c> (the default, 127.0.0.1), <c>all</c> (every interface — a client on the LAN) or a specific IP
/// address. Resolving it gives the interface Kestrel binds (null = all of them) and the host name URLs should name.
/// </summary>
internal static class WebBind
{
    public const string Loopback = "loopback";
    public const string All = "all";

    /// <summary>The interface to listen on (null = all interfaces) and the host name for the base URL.</summary>
    public static (IPAddress? Listen, string Host) Resolve(string? bind)
    {
        var b = (bind ?? "").Trim();
        if (b.Length == 0 || b.Equals(Loopback, StringComparison.OrdinalIgnoreCase))
            return (IPAddress.Loopback, "127.0.0.1");
        if (b.Equals(All, StringComparison.OrdinalIgnoreCase))
            return (null, LocalHost());
        if (IPAddress.TryParse(b, out var ip))
            return (ip, ip.ToString());
        throw new ArgumentException($"server.bind: '{bind}' must be 'loopback', 'all' or an IP address");
    }

    /// <summary>
    /// Whether the origin's host names this machine: a loopback name, or one of the machine's own addresses (a hostname
    /// that resolves to one too). This is what a LAN client's origin looks like when the server is bound beyond
    /// loopback — and the only origins such a server accepts on its own port besides <c>server.publicOrigins</c>.
    /// </summary>
    public static bool IsLocalHost(string host)
    {
        if (host.Length == 0) return false;
        if (host is "localhost" or "127.0.0.1" or "::1") return true;
        if (IPAddress.TryParse(host, out var ip))
            return IsLoopback(ip) || LocalAddresses().Contains(ip);
        try
        {
            foreach (var a in Dns.GetHostAddresses(host))
                if (IsLoopback(a) || LocalAddresses().Contains(a)) return true;
        }
        catch
        {
            // a name this machine does not resolve is not one of its addresses
        }
        return false;
    }

    /// <summary>Whether the address is a loopback one (127.0.0.1 / ::1).</summary>
    public static bool IsLoopback(IPAddress ip) =>
        ip.Equals(IPAddress.Loopback) || ip.Equals(IPAddress.IPv6Loopback);

    private static readonly Lazy<HashSet<IPAddress>> _local = new(BuildLocalAddresses);

    private static HashSet<IPAddress> BuildLocalAddresses()
    {
        var set = new HashSet<IPAddress>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var a in n.GetIPProperties().UnicastAddresses)
                    if (!a.Address.IsIPv4MappedToIPv6) set.Add(a.Address);
            }
        }
        catch
        {
            // best effort: the loopback names still answer
        }
        return set;
    }

    private static HashSet<IPAddress> LocalAddresses() => _local.Value;

    /// <summary>The address a URL should name while listening on all interfaces: the first non-loopback IPv4, else loopback.</summary>
    private static string LocalHost()
    {
        var best = LocalAddresses().FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IsLoopback(a));
        return best?.ToString() ?? "127.0.0.1";
    }
}
