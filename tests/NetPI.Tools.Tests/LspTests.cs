using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Lsp;

namespace NetPI.Tools.Tests;

/// <summary>The lsp plugin (idea-fe26oh): the LSP client against a fake server, the pinned install, the pool with fake
/// servers in node, the changed files from git, and (NETPI_LSP_LIVE=1) the real servers.</summary>
public static class LspTests
{
    public static void Register(TestRunner t)
    {
        t.Add("lsp: the client frames requests and answers, takes the server's notifications, answers its requests, and fails what is in flight when it ends or talks nonsense", ClientProtocol);
        t.Add("lsp: a file goes to its server by extension; a download that does not match its pinned SHA-256 is refused and nothing is installed", PinnedInstall);
        t.Add("lsp: server URIs in any spelling are one path; tsserver's diagnostics read as LSP ones", Uris);
        t.Add("lsp: the changed files are git's under the chat's folder (a subfolder of the repository too), renamed ones by their new name, deleted ones not", ChangedFiles);
        t.Add("lsp: the pool starts servers outside its lock and shares a start, stops the least recently used and the idle, starts a dead one again, forgets a failed start; diagnostics pulled and published", Pool);
        t.Add("lsp: references list every use with its line; rename asks the server, applies its edit to all files through files.applyEdits (EOL kept), and refuses file operations; rename is not read-only", ReferencesAndRename);
        t.Add("lsp: Go goes to gopls, which is found by NETPI_GOPLS or the usual folders; a missing one is an error naming the install command", GoRouting);
        t.Add("lsp: live: gopls (when installed) gives diagnostics, an outline, a workspace search, references and a definition for a Go module", LiveGo);
        t.Add("lsp: live: csharp-ls finds the uses of a method in two files and renames it in both (set NETPI_LSP_LIVE=1; downloads)", LiveRename);
        t.Add("lsp: live: csharp-ls, svelte-language-server and typescript-language-server installed: diagnostics, a definition, an outline and a workspace search (set NETPI_LSP_LIVE=1; downloads)", Live);
    }

    /// <summary>A fake language server on two in-memory pipes: it answers initialize, publishes diagnostics, asks the client
    /// for its configuration, and can hang up.</summary>
    private static async Task ClientProtocol()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var client = new LspClient(toClient.Reader.AsStream(), toServer.Writer.AsStream(), NullLogger.Instance);
        var notified = new TaskCompletionSource<(string, JsonNode?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnNotification = (m, p) => notified.TrySetResult((m, p));
        var serverIn = toServer.Reader.AsStream();
        var serverOut = toClient.Writer.AsStream();
        async Task Send(JsonObject message)
        {
            var body = Encoding.UTF8.GetBytes(message.ToJsonString());
            await serverOut.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n"));
            await serverOut.WriteAsync(body);
            await serverOut.FlushAsync();
        }

        var initialize = client.RequestAsync("initialize", new JsonObject { ["rootUri"] = "file:///x" }, TimeSpan.FromSeconds(10), CancellationToken.None);
        var request = await LspClient.ReadMessageAsync(serverIn, CancellationToken.None);
        Check.Equal("initialize", (string?)request!["method"]);
        Check.Equal("file:///x", (string?)request["params"]!["rootUri"]);
        // the server's own request first: the client answers it (one null per item), then the answer to initialize
        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 77, ["method"] = "workspace/configuration", ["params"] = new JsonObject { ["items"] = new JsonArray(new JsonObject(), new JsonObject()) } });
        var answered = await LspClient.ReadMessageAsync(serverIn, CancellationToken.None);
        Check.Equal(77, (int?)answered!["id"]);
        Check.Equal("[null,null]", answered["result"]!.ToJsonString());
        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = new JsonObject { ["capabilities"] = new JsonObject { ["definitionProvider"] = true } } });
        Check.Equal(true, (bool?)(await initialize)!["capabilities"]!["definitionProvider"]);

        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/publishDiagnostics", ["params"] = new JsonObject { ["uri"] = "file:///x/a.cs", ["diagnostics"] = new JsonArray() } });
        var (method, p) = await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check.Equal("textDocument/publishDiagnostics", method);
        Check.Equal("file:///x/a.cs", (string?)p!["uri"]);

        // an error answer is an LspException with the server's message; the server hanging up fails what waits
        var failing = client.RequestAsync("textDocument/definition", new JsonObject(), TimeSpan.FromSeconds(10), CancellationToken.None);
        var asked = await LspClient.ReadMessageAsync(serverIn, CancellationToken.None);
        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = asked!["id"]!.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "no such thing" } });
        Check.Contains((await Check.ThrowsAsync<LspException>(() => failing)).Message, "no such thing");
        var pending = client.RequestAsync("workspace/symbol", new JsonObject { ["query"] = "A" }, TimeSpan.FromSeconds(10), CancellationToken.None);
        await LspClient.ReadMessageAsync(serverIn, CancellationToken.None);
        await toClient.Writer.CompleteAsync();
        Check.Contains((await Check.ThrowsAsync<LspException>(() => pending)).Message, "gone");
        Check.True(client.Closed is not null, "the client knows the server is gone");
        Check.Contains((await Check.ThrowsAsync<LspException>(() => client.RequestAsync("workspace/symbol", new JsonObject(), TimeSpan.FromSeconds(10), CancellationToken.None))).Message,
            "gone", "and says so to the next request at once");
        await client.DisposeAsync();

        // a body that is not JSON ends the conversation too: what waits fails now, not at its timeout
        var toServer2 = new Pipe();
        var toClient2 = new Pipe();
        var confused = new LspClient(toClient2.Reader.AsStream(), toServer2.Writer.AsStream(), NullLogger.Instance);
        var waiting = confused.RequestAsync("initialize", new JsonObject(), TimeSpan.FromSeconds(60), CancellationToken.None);
        await LspClient.ReadMessageAsync(toServer2.Reader.AsStream(), CancellationToken.None);
        var nonsense = toClient2.Writer.AsStream();
        await nonsense.WriteAsync(Encoding.ASCII.GetBytes("Content-Length: 5\r\n\r\n{oops"));
        await nonsense.FlushAsync();
        var sw = Stopwatch.StartNew();
        Check.Contains((await Check.ThrowsAsync<LspException>(() => waiting)).Message, "gone");
        Check.True(sw.Elapsed < TimeSpan.FromSeconds(10), "it failed at once");
        await confused.DisposeAsync();
    }

    private sealed class Bytes(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private static Task GoRouting()
    {
        Check.Equal("go", LspServer.For("cmd/main.go")!.Id);
        var old = Environment.GetEnvironmentVariable("NETPI_GOPLS");
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var fake = Path.Combine(T.TempDir("lsp-gopls"), OperatingSystem.IsWindows() ? "gopls.exe" : "gopls");
            File.WriteAllText(fake, "");
            Environment.SetEnvironmentVariable("NETPI_GOPLS", fake);
            Check.Equal(fake, LspServer.FindGopls());
            Environment.SetEnvironmentVariable("NETPI_GOPLS", Path.Combine(Path.GetDirectoryName(fake)!, "nope"));
            Check.Contains(Check.Throws<LspException>(() => LspServer.FindGopls()).Message, "NETPI_GOPLS");
            Environment.SetEnvironmentVariable("NETPI_GOPLS", null);
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(fake));
            Check.Equal(fake, LspServer.FindGopls());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETPI_GOPLS", old);
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
        return Task.CompletedTask;
    }

    private static async Task LiveGo()
    {
        try { LspServer.FindGopls(); } catch (LspException ex) { Check.Skip(ex.Message); }
        var root = T.TempDir("lsp-go-project");
        File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/probe\n\ngo 1.21\n");
        File.WriteAllText(Path.Combine(root, "bridge.go"), "package probe\n\ntype Bridge struct{ N int }\n\nfunc NewBridge() *Bridge { return &Bridge{} }\n");
        File.WriteAllText(Path.Combine(root, "use.go"), "package probe\n\nfunc Use(b *Bridge) int { return b.N }\n");
        File.WriteAllText(Path.Combine(root, "bad.go"), "package probe\n\nfunc Bad() int { return \"x\" }\n");
        using var http = new HttpClient();
        await using var pool = new LspPool(new LspInstaller(T.TempDir("lsp-servers-go"), AppContext.BaseDirectory, http, NullLogger.Instance), NullLogger.Instance, () => 2, () => TimeSpan.FromMinutes(15));
        var tool = new LspTool(pool);
        async Task<string> Run(object args) => (await tool.ExecuteAsync(T.Ctx(root, (ModelInfo?)null), T.Args(args), CancellationToken.None)).Content;

        var bad = await Run(new { action = "diagnostics", files = new[] { "bad.go" } });
        Check.Contains(bad, "bad.go:3:", bad);
        var clean = await Run(new { action = "diagnostics", files = new[] { "use.go" } });
        Check.Contains(clean, "no problems", clean);
        var outline = await Run(new { action = "symbols", file = "bridge.go" });
        Check.Contains(outline, "Bridge", outline);
        var search = await Run(new { action = "symbols", query = "Bridge", language = "go" });
        Check.Contains(search, "Bridge", search);
        var refs = await Run(new { action = "references", file = "bridge.go", line = 3, column = 6 });
        Check.Contains(refs, "use.go:3:", refs);
        Check.Contains(refs, "bridge.go:3:", refs);
        var def = await Run(new { action = "definition", file = "use.go", line = 3, column = 15 });
        Check.Contains(def, "bridge.go:3:", def);
    }

    private static async Task PinnedInstall()
    {
        Check.Equal("csharp", LspServer.For("src/A.cs")!.Id);
        Check.Equal("svelte", LspServer.For("web/App.svelte")!.Id);
        Check.Equal("typescript", LspServer.For("web/lib/a.mjs")!.Id);
        Check.Equal("typescript", LspServer.For("x.TS")!.Id, "extensions in any case");
        Check.Equal(null, LspServer.For("README.md"));

        var dir = T.TempDir("lsp-install");
        using var http = new HttpClient(new Bytes(Encoding.UTF8.GetBytes("not the package")));
        var installer = new LspInstaller(dir, dir, http, NullLogger.Instance);
        var refused = await Check.ThrowsAsync<LspException>(() => installer.EnsureAsync(LspServer.CSharp, CancellationToken.None));
        Check.Contains(refused.Message, "pinned SHA-256");
        Check.False(installer.Installed(LspServer.CSharp), "nothing installed");
        Check.False(Directory.EnumerateFileSystemEntries(dir).Any(), "and no half-installed folder left behind");
    }

    private static Task Uris()
    {
        var local = Path.Combine(Path.GetTempPath(), "a b#c", "x.ts");
        var ours = LspSession.Uri(local);
        Check.Equal(Path.GetFullPath(local), LspSession.LocalPath(ours), "our own URI back to its path");
        Check.False(ours.Contains('#'), "a # in a folder name is escaped, not a fragment: " + ours);
        if (OperatingSystem.IsWindows())
        {
            Check.Equal(@"C:\Users\x\a b.ts", LspSession.LocalPath("file:///C:/Users/x/a%20b.ts"));
            Check.Equal(@"c:\Users\x\a b.ts", LspSession.LocalPath("file:///c%3A/Users/x/a%20b.ts"), "an escaped drive colon (vscode-uri)");
            Check.Equal(@"C:\x\y.cs", LspSession.LocalPath("file://localhost/C:/x/y.cs"));
            Check.Equal(@"\\server\share\y.cs", LspSession.LocalPath("file://server/share/y.cs"), "a UNC path");
            Check.Equal(LspSession.Normalize("file:///C:/Users/X/A%20b.ts"), LspSession.Normalize("file:///c%3A/users/x/a b.ts"), "one spelling to match by");
        }
        Check.Equal("csharp:/metadata/projects/x/System.String.cs", LspSession.LocalPath("csharp:/metadata/projects/x/System.String.cs"), "not a file URI: as it is");

        var d = LspSession.FromTsServer(JsonNode.Parse("""{"start":{"line":3,"offset":7},"end":{"line":3,"offset":9},"text":"Type 'string' is not assignable to type 'number'.","code":2322,"category":"error"}""")!.AsObject());
        Check.Equal(2, (int)d["range"]!["start"]!["line"]!);
        Check.Equal(6, (int)d["range"]!["start"]!["character"]!);
        Check.Equal(1, (int)d["severity"]!);
        Check.Equal("TS2322", (string?)d["code"]);
        Check.Contains((string?)d["message"], "not assignable");
        Check.Equal(4, (int)LspSession.FromTsServer(JsonNode.Parse("""{"start":{"line":1,"offset":1},"end":{"line":1,"offset":2},"text":"x","code":80001,"category":"suggestion"}""")!.AsObject())["severity"]!);
        return Task.CompletedTask;
    }

    private static async Task ChangedFiles()
    {
        var repo = T.TempDir("lsp-git");
        if (!TestGit.Run(repo, "init", "-q", "-b", "main")) Check.Skip("no git on PATH");
        TestGit.Run(repo, "config", "user.email", "test@example.com");
        TestGit.Run(repo, "config", "user.name", "Test");
        var src = Path.Combine(repo, "web", "src");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(Path.Combine(repo, "server"));
        File.WriteAllText(Path.Combine(src, "a.ts"), "export const a = 1;\n");
        File.WriteAllText(Path.Combine(src, "gone.ts"), "export {};\n");
        File.WriteAllText(Path.Combine(src, "old name.ts"), "export const o = 1;\n");
        File.WriteAllText(Path.Combine(repo, "server", "B.cs"), "class B {}\n");
        TestGit.Run(repo, "add", "-A");
        Check.True(TestGit.Run(repo, "commit", "-q", "-m", "first"), "committed");
        File.AppendAllText(Path.Combine(src, "a.ts"), "export const b = 2;\n");
        File.WriteAllText(Path.Combine(src, "new \u00fc.ts"), "export {};\n");
        File.Delete(Path.Combine(src, "gone.ts"));
        Check.True(TestGit.Run(repo, "mv", "web/src/old name.ts", "web/src/renamed.ts"), "renamed");
        File.AppendAllText(Path.Combine(repo, "server", "B.cs"), "// changed\n");

        var fromWeb = await LspTool.ChangedFilesAsync(Path.Combine(repo, "web"), CancellationToken.None);
        Check.Equal("web/src/a.ts|web/src/new \u00fc.ts|web/src/renamed.ts",
            string.Join("|", fromWeb.Select(f => Path.GetRelativePath(repo, f).Replace('\\', '/')).Order(StringComparer.Ordinal)),
            "full paths of the changes under the folder, from the repository's top (not the folder doubled)");
        var fromTop = await LspTool.ChangedFilesAsync(repo, CancellationToken.None);
        Check.True(fromTop.Any(f => f.EndsWith(Path.Combine("server", "B.cs"), StringComparison.OrdinalIgnoreCase)), "from the top, the other folder's change too");
        Check.Equal(4, fromTop.Count);
    }

    /// <summary>A fake language server in node: initialize answered after argv[2] ms; diagnostics answered when asked
    /// (argv[3] "pull") or published for each version of a file ("push"); workspace/symbol "die" ends it.</summary>
    private const string FakeServer = """
        const delay = Number(process.argv[2] || '0');
        const mode = process.argv[3] || 'pull';
        let buffer = Buffer.alloc(0);

        function send(message) {
          const body = Buffer.from(JSON.stringify(message), 'utf8');
          process.stdout.write('Content-Length: ' + body.length + '\r\n\r\n');
          process.stdout.write(body);
        }

        function problems(uri, version) {
          const name = decodeURIComponent(uri.split('/').pop());
          return [{
            range: { start: { line: 1, character: 2 }, end: { line: 1, character: 3 } },
            severity: 1, code: 'F1',
            message: 'fake problem in ' + name + (version === undefined ? '' : ' v' + version),
          }];
        }

        function handle(message) {
          const { id, method, params } = message;
          if (method === 'initialize') {
            const capabilities = { textDocumentSync: 1, definitionProvider: true, workspaceSymbolProvider: true, referencesProvider: true, renameProvider: { prepareProvider: true } };
            if (mode === 'pull') capabilities.diagnosticProvider = { interFileDependencies: false, workspaceDiagnostics: false };
            setTimeout(() => send({ jsonrpc: '2.0', id, result: { capabilities } }), delay);
          } else if (method === 'textDocument/diagnostic') {
            send({ jsonrpc: '2.0', id, result: { kind: 'full', items: problems(params.textDocument.uri) } });
          } else if (method === 'textDocument/didOpen' || method === 'textDocument/didChange') {
            if (mode === 'push') {
              const doc = params.textDocument;
              setTimeout(() => send({ jsonrpc: '2.0', method: 'textDocument/publishDiagnostics',
                params: { uri: doc.uri, version: doc.version, diagnostics: problems(doc.uri, doc.version) } }), 50);
            }
          } else if (method === 'textDocument/references') {
            const uri = params.textDocument.uri;
            const sibling = uri.replace(/[^/]*$/, 'b.fake');
            send({ jsonrpc: '2.0', id, result: [
              { uri, range: { start: { line: 0, character: 0 }, end: { line: 0, character: 3 } } },
              { uri: sibling, range: { start: { line: 1, character: 4 }, end: { line: 1, character: 7 } } }] });
          } else if (method === 'textDocument/prepareRename') {
            send({ jsonrpc: '2.0', id, result: params.position.character === 99 ? null
              : { range: { start: { line: 0, character: 0 }, end: { line: 0, character: 3 } }, placeholder: 'foo' } });
          } else if (method === 'textDocument/rename') {
            const uri = params.textDocument.uri;
            const sibling = uri.replace(/[^/]*$/, 'b.fake');
            const edit = (line, from, to) => ({ range: { start: { line, character: from }, end: { line, character: to } }, newText: params.newName });
            if (params.newName === 'fileop') {
              send({ jsonrpc: '2.0', id, result: { documentChanges: [
                { textDocument: { uri, version: 1 }, edits: [edit(0, 0, 3)] }, { kind: 'rename', oldUri: sibling, newUri: sibling + '2' }] } });
            } else {
              send({ jsonrpc: '2.0', id, result: { changes: { [uri]: [edit(0, 0, 3)], [sibling]: [edit(1, 4, 7)] } } });
            }
          } else if (method === 'workspace/symbol') {
            if (params.query === 'die') process.exit(3);
            send({ jsonrpc: '2.0', id, result: [] });
          } else if (method === 'shutdown') {
            send({ jsonrpc: '2.0', id, result: null });
          } else if (method === 'exit') {
            process.exit(0);
          } else if (id !== undefined && method !== undefined) {
            send({ jsonrpc: '2.0', id, error: { code: -32601, message: 'not here: ' + method } });
          }
        }

        process.stdin.on('data', chunk => {
          buffer = Buffer.concat([buffer, chunk]);
          for (;;) {
            const end = buffer.indexOf('\r\n\r\n');
            if (end < 0) return;
            const match = /Content-Length: *(\d+)/i.exec(buffer.subarray(0, end).toString('ascii'));
            const length = match ? Number(match[1]) : 0;
            if (buffer.length < end + 4 + length) return;
            const body = buffer.subarray(end + 4, end + 4 + length).toString('utf8');
            buffer = buffer.subarray(end + 4 + length);
            handle(JSON.parse(body));
          }
        });
        process.stdin.on('end', () => process.exit(0));
        """;

    private static bool NodeAvailable()
    {
        try
        {
            using var node = Process.Start(new ProcessStartInfo("node", "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            if (node is null) return false;
            node.StandardOutput.ReadToEnd();
            return node.WaitForExit(15_000) && node.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }
    }

    private static async Task Pool()
    {
        if (!NodeAvailable()) Check.Skip("node is not on PATH");
        var dir = T.TempDir("lsp-fake");
        var script = Path.Combine(dir, "fake-server.mjs");
        File.WriteAllText(script, FakeServer);
        var servers = Path.Combine(dir, "servers");
        Directory.CreateDirectory(Path.Combine(servers, "fake"));
        File.WriteAllText(Path.Combine(servers, "fake", ".installed"), "a test's fake servers: nothing to install");
        LspServer Fake(string id, int delayMs, string mode, string command = "node") => new()
        {
            Id = id, Install = "fake", Extensions = [".fake"], LanguageIdOf = _ => "fake",
            CommandOf = _ => (command, [script, delayMs.ToString(CultureInfo.InvariantCulture), mode]),
            Needs = "(a test's fake server)", StartTimeout = TimeSpan.FromSeconds(30), RequestTimeout = TimeSpan.FromSeconds(10),
        };
        var slow = Fake("slow", 6000, "pull");
        var fast = Fake("fast", 0, "pull");
        var push = Fake("push", 0, "push");
        var missing = Fake("missing", 0, "pull", command: "netpi-no-such-server-" + Guid.NewGuid().ToString("N")[..6]);
        var idle = TimeSpan.FromMinutes(15);
        using var http = new HttpClient(new Bytes([]));
        await using var pool = new LspPool(new LspInstaller(servers, dir, http, NullLogger.Instance), NullLogger.Instance, () => 2, () => idle);
        var rootA = T.TempDir("lsp-a");
        var rootB = T.TempDir("lsp-b");

        // a server that cannot start says why, and is not remembered as started
        var failed = await Check.ThrowsAsync<LspException>(() => pool.GetAsync(missing, rootA, CancellationToken.None));
        Check.Contains(failed.Message, "could not be started");
        Check.Contains(failed.Message, "(a test's fake server)", "with what it needs");
        Check.Equal(0, pool.Count);

        // a slow start holds up nobody else; two asking for the slow one share its start
        var slowFirst = pool.GetAsync(slow, rootA, CancellationToken.None);
        var slowSecond = pool.GetAsync(slow, rootA, CancellationToken.None);
        var sw = Stopwatch.StartNew();
        var fastSession = await pool.GetAsync(fast, rootA, CancellationToken.None);
        Check.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"the fast server came up while the slow one was starting ({sw.Elapsed.TotalSeconds:0.0} s)");
        Check.False(slowFirst.IsCompleted, "the slow one is still starting");
        var slowSession = await slowFirst;
        Check.True(ReferenceEquals(slowSession, await slowSecond), "one start, shared");
        Check.Equal(2, pool.Count);

        // diagnostics asked for (the fast server offers that); asking makes it the recently used one
        var a = Path.Combine(rootA, "a.fake");
        File.WriteAllText(a, "one\ntwo\n");
        var (pulled, pulledComplete) = await fastSession.DiagnosticsAsync(a, TimeSpan.FromSeconds(10), CancellationToken.None);
        Check.True(pulledComplete, "pulled: complete");
        Check.Contains(pulled.ToJsonString(), "fake problem in a.fake");

        // a third server (the cap is two) stops the least recently used: the slow one
        var pushSession = await pool.GetAsync(push, rootB, CancellationToken.None);
        await Wait.Until(() => !slowSession.Alive, "the least recently used server stopped");
        Check.True(fastSession.Alive, "the recently used one runs on");
        Check.Equal(2, pool.Count);

        // a server that only publishes: this version's diagnostics are waited for, and the next version's are its own
        var b = Path.Combine(rootB, "b.fake");
        File.WriteAllText(b, "x");
        var (pushed, pushedComplete) = await pushSession.DiagnosticsAsync(b, TimeSpan.FromSeconds(10), CancellationToken.None);
        Check.True(pushedComplete, "published: complete");
        Check.Contains(pushed.ToJsonString(), "fake problem in b.fake v1");
        (pushed, pushedComplete) = await pushSession.DiagnosticsAsync(b, TimeSpan.FromSeconds(10), CancellationToken.None);
        Check.True(pushedComplete, "published again: complete");
        Check.Contains(pushed.ToJsonString(), "fake problem in b.fake v2");

        // a server that died is started again on the next request
        await Check.ThrowsAsync<LspException>(() => pushSession.WorkspaceSymbolsAsync("die", CancellationToken.None));
        await Wait.Until(() => !pushSession.Alive, "the dead server is noticed");
        var again = await pool.GetAsync(push, rootB, CancellationToken.None);
        Check.False(ReferenceEquals(again, pushSession), "a new session");
        Check.True(again.Alive, "and it runs");

        // the idle sweep stops what nobody asked for in lsp.idleMinutes
        idle = TimeSpan.Zero;
        await pool.SweepAsync();
        Check.Equal(0, pool.Count);
        await Wait.Until(() => !again.Alive && !fastSession.Alive, "the idle servers stopped");
    }

    private static async Task ReferencesAndRename()
    {
        if (!NodeAvailable()) Check.Skip("node is not on PATH");
        var dir = T.TempDir("lsp-rename");
        var script = Path.Combine(dir, "fake-server.mjs");
        File.WriteAllText(script, FakeServer);
        var servers = Path.Combine(dir, "servers");
        Directory.CreateDirectory(Path.Combine(servers, "fake"));
        File.WriteAllText(Path.Combine(servers, "fake", ".installed"), "a test's fake servers: nothing to install");
        var fake = new LspServer
        {
            Id = "fake", Install = "fake", Extensions = [".fake"], LanguageIdOf = _ => "fake",
            CommandOf = _ => ("node", [script, "0", "pull"]),
            Needs = "(a test's fake server)", StartTimeout = TimeSpan.FromSeconds(30), RequestTimeout = TimeSpan.FromSeconds(10),
        };
        using var http = new HttpClient(new Bytes([]));
        await using var pool = new LspPool(new LspInstaller(servers, dir, http, NullLogger.Instance), NullLogger.Instance, () => 2, () => TimeSpan.FromMinutes(15));
        var root = T.TempDir("lsp-rename-root");
        var a = Path.Combine(root, "a.fake");
        var b = Path.Combine(root, "b.fake");
        File.WriteAllBytes(a, Encoding.UTF8.GetBytes("old = 1\r\nx\r\n"));
        File.WriteAllBytes(b, Encoding.UTF8.GetBytes("first\nuse old here\n"));
        using var ctx = new FakePluginContext(root);
        await new NetPI.Tools.Files.FilesPlugin().StartAsync(ctx, CancellationToken.None);
        var tool = new LspTool(pool, ctx.Rpc) { ServerFor = f => f.EndsWith(".fake", StringComparison.Ordinal) ? fake : null };
        Check.False(tool.Definition.ReadOnly, "the tool as a whole may write");
        Check.True(tool.IsReadOnly(T.Args(new { action = "references", file = "a.fake", line = 1, column = 1 })), "references only reads");
        Check.False(tool.IsReadOnly(T.Args(new { action = "rename", file = "a.fake", line = 1, column = 1, newName = "n" })), "rename writes");
        async Task<ToolResult> Run(object args) => await tool.ExecuteAsync(T.Ctx(root, (ModelInfo?)null), T.Args(args), CancellationToken.None);

        var refs = await Run(new { action = "references", file = "a.fake", line = 1, column = 1 });
        Check.False(refs.IsError, refs.Content);
        Check.Contains(refs.Content, "2 reference(s) in 2 file(s)");
        Check.Contains(refs.Content, "a.fake:1:1: old = 1");
        Check.Contains(refs.Content, "b.fake:2:5: use old here");

        var nothing = await Run(new { action = "rename", file = "a.fake", line = 1, column = 100, newName = "n" });
        Check.True(nothing.IsError && nothing.Content.Contains("nothing to rename"), nothing.Content);

        var fileOp = await Run(new { action = "rename", file = "a.fake", line = 1, column = 1, newName = "fileop" });
        Check.True(fileOp.IsError && fileOp.Content.Contains("rename a file") && fileOp.Content.Contains("Nothing was changed"), fileOp.Content);
        Check.Equal("old = 1\r\nx\r\n", File.ReadAllText(a));

        var renamed = await Run(new { action = "rename", file = "a.fake", line = 1, column = 1, newName = "fresh" });
        Check.False(renamed.IsError, renamed.Content);
        Check.Contains(renamed.Content, "2 edit(s) in 2 file(s)");
        Check.Equal("fresh = 1\r\nx\r\n", File.ReadAllText(a), "CRLF kept");
        Check.Equal("first\nuse fresh here\n", File.ReadAllText(b));

        // a protocol-level look at the edit parsing
        var (edits, problem) = LspTool.ParseWorkspaceEdit(JsonNode.Parse("""{"documentChanges":[{"kind":"delete","uri":"file:///x/y.cs"}]}"""));
        Check.True(problem is not null && problem.Contains("delete"), problem ?? "");
        Check.Equal(0, edits.Count);
    }

    private static async Task LiveRename()
    {
        if (Environment.GetEnvironmentVariable("NETPI_LSP_LIVE") != "1") Check.Skip("set NETPI_LSP_LIVE=1 to download and run csharp-ls");
        var servers = T.TempDir("lsp-servers-rename");
        var pluginDir = Path.GetDirectoryName(typeof(LspPlugin).Assembly.Location)!;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        await using var pool = new LspPool(new LspInstaller(servers, pluginDir, http, NullLogger.Instance), NullLogger.Instance, () => 2, () => TimeSpan.FromMinutes(15));
        var root = T.TempDir("lsp-rename-project");
        File.WriteAllText(Path.Combine(root, "probe.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "Greeter.cs"), "class Greeter\n{\n    public static string Hello() => \"hi\";\n}\n");
        File.WriteAllText(Path.Combine(root, "Program.cs"), "class Program\n{\n    static void Main() { System.Console.WriteLine(Greeter.Hello()); }\n}\n");
        using var ctx = new FakePluginContext(root);
        await new NetPI.Tools.Files.FilesPlugin().StartAsync(ctx, CancellationToken.None);
        var tool = new LspTool(pool, ctx.Rpc);
        async Task<ToolResult> Run(object args) => await tool.ExecuteAsync(T.Ctx(root, (ModelInfo?)null), T.Args(args), CancellationToken.None);
        var refs = await Run(new { action = "references", file = "Greeter.cs", line = 3, column = 26 });
        Check.Contains(refs.Content, "Program.cs:3:", refs.Content);
        Check.Contains(refs.Content, "Greeter.cs:3:", refs.Content);
        var renamed = await Run(new { action = "rename", file = "Greeter.cs", line = 3, column = 26, newName = "Greet" });
        Check.False(renamed.IsError, renamed.Content);
        Check.Contains(File.ReadAllText(Path.Combine(root, "Program.cs")), "Greeter.Greet()");
        Check.Contains(File.ReadAllText(Path.Combine(root, "Greeter.cs")), "string Greet()");
    }

    /// <summary>The real thing, into a temporary servers folder: a C# project with an error, JS and TS files with a syntax
    /// and a type error, a clean JS file asked about twice, a Svelte component with a script error.</summary>
    private static async Task Live()
    {
        if (Environment.GetEnvironmentVariable("NETPI_LSP_LIVE") != "1") Check.Skip("set NETPI_LSP_LIVE=1 to download and run the real servers");
        var servers = T.TempDir("lsp-servers");
        var pluginDir = Path.GetDirectoryName(typeof(LspPlugin).Assembly.Location)!;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var installer = new LspInstaller(servers, pluginDir, http, NullLogger.Instance);
        await using var pool = new LspPool(installer, NullLogger.Instance, () => 3, () => TimeSpan.FromMinutes(15));

        var root = T.TempDir("lsp-project");
        File.WriteAllText(Path.Combine(root, "probe.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var cs = Path.Combine(root, "Program.cs");
        File.WriteAllText(cs, "class Greeter\n{\n    public static string Hello() => \"hi\";\n    static void Main() { int x = \"text\"; System.Console.WriteLine(Hello()); }\n}\n");
        var tool = new LspTool(pool);
        async Task<string> Run(object args) => (await tool.ExecuteAsync(T.Ctx(root, (ModelInfo?)null), T.Args(args), CancellationToken.None)).Content;

        var diagnostics = await Run(new { action = "diagnostics", files = new[] { "Program.cs" } });
        Check.Contains(diagnostics, "CS0029", "the C# server reports the type error: " + diagnostics);
        Check.Contains(diagnostics, "1 file(s) checked:", diagnostics);
        var call = File.ReadAllLines(cs)[3];
        var definition = await Run(new { action = "definition", file = "Program.cs", line = 4, column = call.IndexOf("Hello()", StringComparison.Ordinal) + 1 });
        Check.Contains(definition, "Program.cs:3:", "Hello() is defined on line 3: " + definition);
        var outline = await Run(new { action = "symbols", file = "Program.cs" });
        Check.Contains(outline, "Greeter", outline);
        var search = await Run(new { action = "symbols", query = "Greeter" });
        Check.Contains(search, "class Greeter", "the workspace search finds the class: " + search);

        File.WriteAllText(Path.Combine(root, "broken.mjs"), "export const x = ;\n");
        var jsDiagnostics = await Run(new { action = "diagnostics", files = new[] { "broken.mjs" } });
        Check.Contains(jsDiagnostics, "broken.mjs:1:", "the JS server reports the syntax error: " + jsDiagnostics);
        File.WriteAllText(Path.Combine(root, "typed.ts"), "const n: number = \"text\";\nexport { n };\n");
        var tsDiagnostics = await Run(new { action = "diagnostics", files = new[] { "typed.ts" } });
        Check.Contains(tsDiagnostics, "typed.ts:1:", tsDiagnostics);
        Check.Contains(tsDiagnostics, "TS2322", "the type error too, not only syntax: " + tsDiagnostics);
        File.WriteAllText(Path.Combine(root, "clean.mjs"), "export const ok = 1;\n");
        await Run(new { action = "diagnostics", files = new[] { "clean.mjs" } });
        var sw = Stopwatch.StartNew();
        var clean = await Run(new { action = "diagnostics", files = new[] { "clean.mjs" } });
        Check.Contains(clean, "no problems", clean);
        Check.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"a clean file asked about again answers at once ({sw.Elapsed.TotalSeconds:0.0} s)");

        File.WriteAllText(Path.Combine(root, "Broken.svelte"), "<script>\n  let count = ;\n</script>\n<p>{count}</p>\n");
        var svelteDiagnostics = await Run(new { action = "diagnostics", files = new[] { "Broken.svelte" } });
        Check.Contains(svelteDiagnostics, "Broken.svelte:2:", "the Svelte server reports the script error: " + svelteDiagnostics);
    }
}
