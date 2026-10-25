using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Host;

namespace NetPI.Desktop;

/// <summary>
/// Connect mode: this NetPI.exe as a client of a NetPI server on another machine (or this one, headless).
/// The connection is <c>--connect [url]</c> (a URL, or the stored one when the flag is bare) and a token
/// (<c>--token</c>, the <c>NETPI_TOKEN</c> environment variable, a <c>?token=</c> in the URL, or the stored one);
/// the resolved connection is written to <c>&lt;home&gt;/remote.json</c>, so a later bare <c>--connect</c> reuses it.
/// A local path is never handed to the remote server: file features download the server's file
/// (<c>files.read</c>) and work on the local copy.
/// </summary>
internal sealed class RemoteConnection
{
    public const string FileName = "remote.json";
    private const string TokenHeader = "X-NetPI-Token";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>The server's base URL (scheme, host, port — no token).</summary>
    public required string Url { get; init; }
    /// <summary>The auth token, when one is known (null: the page's cookie is all there is).</summary>
    public string? Token { get; init; }
    /// <summary>What the WebView navigates to: the login route, so the server sets the cookie.</summary>
    public required string LaunchUrl { get; init; }
    /// <summary>The host the connection is to (for the window's status line).</summary>
    public string Host { get; init; } = "";

    /// <summary>
    /// Resolve a connect mode: <paramref name="connectArg"/> the value of <c>--connect</c> (null = local mode, an empty
    /// string = the stored connection), <paramref name="tokenArg"/> the value of <c>--token</c>. null = nothing to
    /// connect to. The resolved connection is stored, so a later bare <c>--connect</c> finds it.
    /// </summary>
    public static RemoteConnection? Resolve(string? connectArg, string? tokenArg, string home)
    {
        if (connectArg is null) return null;
        var stored = Load(home);
        var url = string.IsNullOrWhiteSpace(connectArg) ? stored?.Url : connectArg.Trim();
        if (url is null) return null;
        Uri baseUri;
        try { baseUri = new Uri(url, UriKind.Absolute); }
        catch (ArgumentException) { return null; } // a URL that is not a URL (UriException is gone in .NET 10; it throws ArgumentException)
        if (baseUri.Scheme is not ("http" or "https")) return null;
        var inUrl = TokenInUrl(baseUri);
        var token = tokenArg
            ?? Environment.GetEnvironmentVariable(Successor.TokenVariable)
            ?? inUrl
            ?? (string.IsNullOrWhiteSpace(connectArg) ? stored?.Token : null);
        var urlBase = baseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var launch = token is { Length: > 0 } t
            ? $"{urlBase}/?token={Uri.EscapeDataString(t)}"
            : urlBase + "/";
        var connection = new RemoteConnection
        {
            Url = urlBase,
            Token = token,
            Host = baseUri.Host,
            LaunchUrl = launch,
        };
        Save(home, connection);
        return connection;
    }

    /// <summary>The <c>?token=</c> of a launch URL, unescaped (the server prints the launch URL with it).</summary>
    private static string? TokenInUrl(Uri uri)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
            if (part.StartsWith("token=", StringComparison.OrdinalIgnoreCase) && part.Length > 6)
                try { return Uri.UnescapeDataString(part["token=".Length..]); }
                catch (UriFormatException) { return part["token=".Length..]; }
        return null;
    }

    /// <summary>The last stored connection (<c>&lt;home&gt;/remote.json</c>), or null.</summary>
    public static RemoteConnection? Load(string home)
    {
        try
        {
            var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(home, FileName)))?.AsObject();
            var url = doc?["url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url)) return null;
            var token = doc?["token"]?.GetValue<string>();
            var root = url.TrimEnd('/');
            return new RemoteConnection
            {
                Url = root,
                Token = token,
                LaunchUrl = token is { Length: > 0 } t ? $"{root}/?token={Uri.EscapeDataString(t)}" : root + "/",
                Host = new Uri(root, UriKind.Absolute).Host,
            };
        }
        catch
        {
            return null; // no stored connection (or a broken file): the flags decide
        }
    }

    private static void Save(string home, RemoteConnection connection)
    {
        try
        {
            var doc = new JsonObject { ["url"] = connection.Url, ["token"] = connection.Token };
            var path = Path.Combine(home, FileName);
            Directory.CreateDirectory(home);
            // The token in it must not be readable by other local users (like <home>/server.json's): the tmp is
            // created owner-only, and the move keeps the mode.
            var tmp = path + ".tmp";
            using (var fs = PathUtil.CreateOwnerOnly(tmp)) fs.Write(Encoding.UTF8.GetBytes(doc.ToJsonString()));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // storing is a convenience: a failed store never stops the connect
        }
    }

    /// <summary>
    /// The bytes of a server file through the server's <c>files.read</c> RPC (the client's own token in the header):
    /// what a remote "open" and "reveal" download before they work on the local copy. null when the server says no
    /// (missing, a directory, past the cap, no token).
    /// </summary>
    public async Task<(string Name, byte[] Bytes)?> ReadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Url + "/api/rpc/files.read")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { path }), Encoding.UTF8, "application/json"),
            };
            if (Token is { Length: > 0 } t) req.Headers.Add(TokenHeader, t);
            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var doc = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc?["data"] is not JsonValue { } data) return null;
            var name = System.IO.Path.GetFileName(doc?["path"]?.GetValue<string>() ?? path);
            return (name, Convert.FromBase64String(data.GetValue<string>()));
        }
        catch
        {
            return null; // the server is gone, the file is not readable: the caller shows nothing
        }
    }
}
