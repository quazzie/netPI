using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Lsp;

/// <summary>
/// One running language server for one workspace root: the process, the handshake, the documents it has open, and the
/// diagnostics it last published. Every request reads the file from disk again and sends it (didOpen, then didChange
/// with the next version), so an answer is about the file as it is now, whoever changed it (the owner's freshness rule).
/// </summary>
internal sealed class LspSession : IAsyncDisposable
{
    /// <summary>typescript-language-server's command that passes a request straight to its tsserver.</summary>
    private const string TsServerRequest = "typescript.tsserverRequest";

    private readonly Process _process;
    private readonly LspClient _client;
    private readonly ConcurrentDictionary<string, int> _versions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Version, JsonArray Items, long At)> _published = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _docs = new(1, 1);
    private JsonObject? _capabilities;
    private int _disposed;

    public LspServer Server { get; }
    public string Root { get; }
    public DateTimeOffset LastUsed { get; private set; } = DateTimeOffset.UtcNow;
    /// <summary>Whether a document was sent yet: the JS servers search workspace symbols in the projects of open files.</summary>
    public bool HasDocuments => !_versions.IsEmpty;

    public bool Alive
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0 || _client.Closed is not null) return false;
            try { return !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private LspSession(LspServer server, string root, Process process, ILogger log)
    {
        Server = server;
        Root = root;
        _process = process;
        _client = new LspClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, log) { OnNotification = Notified };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.LogDebug("lsp {Server}: {Line}", server.Id, e.Data); };
        process.BeginErrorReadLine();
    }

    public static async Task<LspSession> StartAsync(LspServer server, string root, string installDir, ILogger log, CancellationToken ct)
    {
        var (file, args) = server.Command(installDir);
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // csharp-ls is built for .NET 10; a newer runtime runs it too
        psi.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        Process process;
        try { process = Process.Start(psi) ?? throw new LspException($"The {server.Id} language server did not start."); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new LspException($"The {server.Id} language server could not be started ({file}: {ex.Message}). {server.Needs}".TrimEnd());
        }
        try { ChildJob.Assign(process); } catch { /* the job is a safety net: the session ends the process itself */ }
        var session = new LspSession(server, root, process, log);
        try
        {
            await session.InitializeAsync(installDir, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(string installDir, CancellationToken ct)
    {
        var rootUri = Uri(Root);
        var answer = await _client.RequestAsync("initialize", new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["rootUri"] = rootUri,
            ["rootPath"] = Root,
            ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = rootUri, ["name"] = Path.GetFileName(Root) }),
            ["clientInfo"] = new JsonObject { ["name"] = "NetPI" },
            ["capabilities"] = new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["synchronization"] = new JsonObject { ["didSave"] = false },
                    ["publishDiagnostics"] = new JsonObject { ["relatedInformation"] = false, ["versionSupport"] = true },
                    // csharp-ls and svelte-language-server answer diagnostics when asked (pull) to a client that says it can ask
                    ["diagnostic"] = new JsonObject { ["dynamicRegistration"] = false },
                    ["definition"] = new JsonObject { ["linkSupport"] = false },
                    ["references"] = new JsonObject(),
                    ["rename"] = new JsonObject { ["prepareSupport"] = true },
                    ["documentSymbol"] = new JsonObject { ["hierarchicalDocumentSymbolSupport"] = true },
                },
                ["workspace"] = new JsonObject { ["symbol"] = new JsonObject(), ["workspaceFolders"] = true, ["configuration"] = true },
                ["window"] = new JsonObject { ["workDoneProgress"] = false },
            },
            ["initializationOptions"] = Server.InitializationOptions(installDir),
        }, Server.StartTimeout, ct).ConfigureAwait(false);
        _capabilities = answer?["capabilities"] as JsonObject;
        await _client.NotifyAsync("initialized", new JsonObject(), ct).ConfigureAwait(false);
    }

    private void Notified(string method, JsonNode? p)
    {
        if (method != "textDocument/publishDiagnostics" || p?["uri"]?.GetValue<string>() is not { } uri) return;
        var version = p["version"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : -1;
        _published[Normalize(uri)] = (version, p["diagnostics"] as JsonArray ?? [], Stopwatch.GetTimestamp());
    }

    /// <summary>The largest file sent to a server (bytes).</summary>
    internal const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>Send the file as it is on disk now; its version, to match the diagnostics published for it.</summary>
    private async Task<(string Uri, int Version)> SyncAsync(string path, CancellationToken ct)
    {
        // The whole text goes into memory and into a didOpen/didChange message: a generated or minified file is refused.
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new LspException($"{Path.GetFileName(path)} is larger than {MaxFileBytes / (1024 * 1024)} MB: it is not sent to the {Server.Id} language server.");
        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var uri = Uri(path);
        await _docs.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LastUsed = DateTimeOffset.UtcNow;
            if (_versions.TryGetValue(uri, out var version))
            {
                version++;
                await _client.NotifyAsync("textDocument/didChange", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
                    ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
                }, ct).ConfigureAwait(false);
            }
            else
            {
                version = 1;
                await _client.NotifyAsync("textDocument/didOpen", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = Server.LanguageId(path), ["version"] = version, ["text"] = text },
                }, ct).ConfigureAwait(false);
            }
            _versions[uri] = version;
            return (uri, version);
        }
        finally { _docs.Release(); }
    }

    /// <summary>Open (or refresh) a file without asking anything about it, so a server knows its project.</summary>
    public Task TouchAsync(string path, CancellationToken ct) => SyncAsync(path, ct);

    /// <summary>
    /// The file's diagnostics now. Asked for (textDocument/diagnostic) when the server offers that (csharp-ls,
    /// svelte-language-server); typescript-language-server only publishes, and not at all while a clean file stays clean,
    /// so its tsserver is asked directly; any other server's published ones for this version of the file are waited for,
    /// up to <paramref name="wait"/>, and when none came the last ones are returned as not complete.
    /// </summary>
    public async Task<(JsonArray Items, bool Complete)> DiagnosticsAsync(string path, TimeSpan wait, CancellationToken ct)
    {
        var since = Stopwatch.GetTimestamp();
        var (uri, version) = await SyncAsync(path, ct).ConfigureAwait(false);
        if (_capabilities?["diagnosticProvider"] is JsonObject)
        {
            var report = await _client.RequestAsync("textDocument/diagnostic", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, wait, ct).ConfigureAwait(false);
            return (report is JsonObject r && r["items"] is JsonArray items ? items : [], true);
        }
        if (Offers(TsServerRequest)) return (await TsServerDiagnosticsAsync(uri, wait, ct).ConfigureAwait(false), true);
        var key = Normalize(uri);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < wait)
        {
            // this version's (or a later one's: the file changed again meanwhile), or an unversioned set sent since the sync
            if (_published.TryGetValue(key, out var got) && (got.Version >= version || got.Version < 0 && got.At > since))
                return ((JsonArray)got.Items.DeepClone(), true);
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return _published.TryGetValue(key, out var last) ? ((JsonArray)last.Items.DeepClone(), false) : ([], false);
    }

    private bool Offers(string command) =>
        _capabilities?["executeCommandProvider"] is JsonObject provider && provider["commands"] is JsonArray commands &&
        commands.Any(c => c?.GetValueKind() == JsonValueKind.String && c.GetValue<string>() == command);

    /// <summary>The file's syntax and semantic diagnostics from tsserver itself, as LSP diagnostics.</summary>
    private async Task<JsonArray> TsServerDiagnosticsAsync(string uri, TimeSpan wait, CancellationToken ct)
    {
        var items = new JsonArray();
        foreach (var command in new[] { "syntacticDiagnosticsSync", "semanticDiagnosticsSync" })
        {
            var answer = await _client.RequestAsync("workspace/executeCommand", new JsonObject
            {
                ["command"] = TsServerRequest,
                ["arguments"] = new JsonArray(command, new JsonObject { ["file"] = uri }),
            }, wait, ct).ConfigureAwait(false);
            var found = answer switch { JsonObject response => response["body"] as JsonArray, JsonArray list => list, _ => null };
            foreach (var d in (found ?? []).OfType<JsonObject>()) items.Add(FromTsServer(d));
        }
        return items;
    }

    /// <summary>A tsserver diagnostic (1-based line and offset, a category, a numeric code) as an LSP one.</summary>
    internal static JsonObject FromTsServer(JsonObject d)
    {
        static JsonObject Position(JsonNode? p) => new()
        {
            ["line"] = Math.Max(0, (p?["line"] is JsonValue l && l.TryGetValue<int>(out var line) ? line : 1) - 1),
            ["character"] = Math.Max(0, (p?["offset"] is JsonValue o && o.TryGetValue<int>(out var offset) ? offset : 1) - 1),
        };
        return new JsonObject
        {
            ["range"] = new JsonObject { ["start"] = Position(d["start"]), ["end"] = Position(d["end"]) },
            ["severity"] = (d["category"] is JsonValue c && c.TryGetValue<string>(out var category) ? category : null) switch
            {
                "error" => 1, "warning" => 2, "suggestion" => 4, _ => 3,
            },
            ["code"] = d["code"] is JsonValue code ? "TS" + code.ToJsonString().Trim('"') : null,
            ["source"] = "ts",
            ["message"] = d["text"] is JsonValue t && t.TryGetValue<string>(out var text) ? text : "",
        };
    }

    public async Task<JsonNode?> DefinitionAsync(string path, int line, int column, CancellationToken ct)
    {
        var (uri, _) = await SyncAsync(path, ct).ConfigureAwait(false);
        return await _client.RequestAsync("textDocument/definition", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = column },
        }, Server.RequestTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>Whether the server offers a capability (<c>referencesProvider</c>, <c>renameProvider</c>): true or an options object.</summary>
    public bool Offers(string provider, out JsonObject? options)
    {
        options = null;
        var node = _capabilities?[provider];
        if (node is JsonObject o) { options = o; return true; }
        return node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    public async Task<JsonNode?> ReferencesAsync(string path, int line, int column, CancellationToken ct)
    {
        var (uri, _) = await SyncAsync(path, ct).ConfigureAwait(false);
        return await _client.RequestAsync("textDocument/references", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = column },
            ["context"] = new JsonObject { ["includeDeclaration"] = true },
        }, Server.RequestTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>textDocument/prepareRename: null when there is nothing renameable at the position.</summary>
    public async Task<JsonNode?> PrepareRenameAsync(string path, int line, int column, CancellationToken ct)
    {
        var (uri, _) = await SyncAsync(path, ct).ConfigureAwait(false);
        return await _client.RequestAsync("textDocument/prepareRename", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = column },
        }, Server.RequestTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>textDocument/rename: the WorkspaceEdit the server proposes (nothing is applied here).</summary>
    public async Task<JsonNode?> RenameAsync(string path, int line, int column, string newName, CancellationToken ct)
    {
        var (uri, _) = await SyncAsync(path, ct).ConfigureAwait(false);
        return await _client.RequestAsync("textDocument/rename", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = column },
            ["newName"] = newName,
        }, Server.RequestTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>Send the documents of these files that the server has open again, after something else changed them on disk.</summary>
    public async Task RefreshOpenAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        foreach (var path in paths)
            if (File.Exists(path) && _versions.ContainsKey(Uri(path)))
                await SyncAsync(path, ct).ConfigureAwait(false);
    }

    public async Task<JsonNode?> DocumentSymbolsAsync(string path, CancellationToken ct)
    {
        var (uri, _) = await SyncAsync(path, ct).ConfigureAwait(false);
        return await _client.RequestAsync("textDocument/documentSymbol", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } },
            Server.RequestTimeout, ct).ConfigureAwait(false);
    }

    public Task<JsonNode?> WorkspaceSymbolsAsync(string query, CancellationToken ct)
    {
        LastUsed = DateTimeOffset.UtcNow;
        return _client.RequestAsync("workspace/symbol", new JsonObject { ["query"] = query }, Server.RequestTimeout, ct);
    }

    internal static string Uri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>
    /// A file URI's local path, however the server spelled it: <c>file:///C:/x</c>, <c>file:///c%3A/x</c> (an escaped drive
    /// colon), <c>file://localhost/C:/x</c>, <c>file://server/share/x</c> (a UNC path). Anything else is returned as it is.
    /// </summary>
    internal static string LocalPath(string uri)
    {
        if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return uri;
        var rest = uri[5..];
        if (rest.StartsWith("//localhost/", StringComparison.OrdinalIgnoreCase)) rest = rest["//localhost".Length..];
        // three slashes: an empty authority and an absolute path; two: a host, which makes it a UNC path
        var path = System.Uri.UnescapeDataString(rest.StartsWith("///", StringComparison.Ordinal) ? rest[2..] : rest);
        if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':') path = path[1..];
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// <summary>A file URI in one spelling, to match the servers' URIs with ours (case-insensitive on Windows).</summary>
    internal static string Normalize(string uri) => OperatingSystem.IsWindows() ? LocalPath(uri).ToLowerInvariant() : LocalPath(uri);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_client.Closed is null && !_process.HasExited)
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _client.RequestAsync("shutdown", null, TimeSpan.FromSeconds(3), bye.Token).ConfigureAwait(false);
                await _client.NotifyAsync("exit", null, bye.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is LspException or OperationCanceledException or IOException or InvalidOperationException) { }
        await _client.DisposeAsync().ConfigureAwait(false);
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { try { _process.Kill(entireProcessTree: true); } catch { /* gone */ } }
        catch (InvalidOperationException) { /* never started */ }
        _process.Dispose();
        // _docs is left alone: a request still waiting for it gets the client's "gone" instead of a disposed semaphore
    }
}
