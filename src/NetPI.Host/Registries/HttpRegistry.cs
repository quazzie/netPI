using Microsoft.AspNetCore.Http;

namespace NetPI.Host.Registries;

/// <summary>
/// Plugin HTTP endpoints served under <c>/api/p/{pluginId}/{path}</c>. A path is matched exactly
/// (case-insensitive); a trailing <c>/*</c> or <c>/{**rest}</c> (or just <c>*</c>) makes it a prefix route.
/// The longest match wins, ties go to the latest registration. Handlers see <c>Request.PathBase</c> =
/// <c>/api/p/{pluginId}</c> and <c>Request.Path</c> = the remainder.
/// </summary>
internal sealed class HttpRegistry
{
    private sealed record Entry(string PluginId, string Path, bool Prefix, Func<HttpContext, Task> Handler, long Seq);

    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private long _seq;

    public IDisposable Map(string pluginId, string path, Func<HttpContext, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(handler);
        var (normalized, prefix) = Parse(path ?? "");
        Entry entry;
        lock (_gate)
        {
            entry = new Entry(pluginId, normalized, prefix, handler, ++_seq);
            _entries.Add(entry);
        }
        return new Registration(() => { lock (_gate) _entries.Remove(entry); });
    }

    public Func<HttpContext, Task>? Match(string pluginId, string subPath)
    {
        var path = subPath.Trim('/');
        Entry? best = null;
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                if (!string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) continue;
                var ok = e.Prefix
                    ? e.Path.Length == 0 || string.Equals(path, e.Path, StringComparison.OrdinalIgnoreCase) ||
                      path.StartsWith(e.Path + "/", StringComparison.OrdinalIgnoreCase)
                    : string.Equals(path, e.Path, StringComparison.OrdinalIgnoreCase);
                if (!ok) continue;
                if (best is null || Score(e) > Score(best) || (Score(e) == Score(best) && e.Seq > best.Seq)) best = e;
            }
        }
        return best?.Handler;
    }

    // Exact routes beat prefix routes of the same length.
    private static int Score(Entry e) => e.Path.Length * 2 + (e.Prefix ? 0 : 1);

    private static (string Path, bool Prefix) Parse(string path)
    {
        var p = path.Trim().Trim('/');
        if (p == "*" || p.StartsWith("{**", StringComparison.Ordinal)) return ("", true);
        if (p.EndsWith("/*", StringComparison.Ordinal)) return (p[..^2].Trim('/'), true);
        var rest = p.IndexOf("/{**", StringComparison.Ordinal);
        if (rest >= 0) return (p[..rest].Trim('/'), true);
        return (p, false);
    }
}
