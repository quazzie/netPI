extern alias codesearch;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using codesearch::NetPI.CodeSearch;
using codesearch::NetPI.SearchKit;

namespace NetPI.Tools.Tests;

/// <summary>
/// The code search plugin: passages cut at declarations (per language, with their containers as the signature), the
/// incremental refresh by content hash (a changed file re-reads and re-embeds only itself, a deleted one is dropped),
/// words and meaning fused, words only without an embedding model, the workspace boundary (a bound session searches its
/// checkout, a broken binding is an error, never the project), and the counted metric. With NETPI_CODESEARCH_LIVE=1 one
/// more test indexes this repository and prints its numbers.
/// </summary>
public static class CodeSearchTests
{
    /// <summary>A deterministic embedder: a bag of words hashed into 256 dimensions; synonyms give meaning without shared words.</summary>
    private sealed class BagOfWords(string? model = "bow-64") : IEmbeddingService
    {
        public string? Model { get; set; } = model;
        public bool Available { get; set; } = true;
        public List<string> Documents { get; } = [];
        public Dictionary<string, string> Synonyms { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct)
        {
            if (!Available || Model is null) throw new EmbeddingException("unavailable", "down");
            if (request.Kind == EmbeddingKind.Document) lock (Documents) Documents.AddRange(request.Texts);
            return Task.FromResult(new EmbeddingResult(Model, 256, request.Texts.Select(Vector).ToList()));
        }

        private float[] Vector(string text)
        {
            var v = new float[256];
            foreach (var raw in Lexical.CodeTokens(text))
            {
                var w = Synonyms.GetValueOrDefault(raw, raw);
                if (w.Length < 3) continue;
                uint hash = 2166136261;
                foreach (var c in w) hash = unchecked((hash ^ c) * 16777619);
                v[(int)(hash % 255)] += 1;
            }
            v[255] += 0.01f;
            return VectorMath.Normalize(v);
        }
    }

    private const string CSharp = """
        using System;

        namespace Demo;

        /// <summary>Keeps the radio's queue topped up.</summary>
        public sealed class RadioQueue : IDisposable
        {
            private readonly int _size;
            private int _count;

            public RadioQueue(int size)
            {
                _size = size;
                _count = 0;
            }

            /// <summary>Adds songs until the buffer holds a batch.</summary>
            [Obsolete("x")]
            public void TopUp(int songsPerBatch)
            {
                while (_count < songsPerBatch)
                {
                    _count++;
                }
                Console.WriteLine(_count);
            }

            public int Count => _count;

            public void Dispose()
            {
                _count = 0;
                Console.WriteLine("disposed");
            }
        }
        """;

    private static FakePluginContext Context(string root, IEmbeddingService? embedder)
    {
        var ctx = new FakePluginContext(root, T.TempDir("cs-home"));
        if (embedder is not null) ctx.Services.Register(embedder);
        return ctx;
    }

    private static async Task<JsonObject> Rpc(FakePluginContext ctx, string method, object args) =>
        (JsonObject)NetPiJson.ToNode(await ctx.RpcFake.InvokeAsync(method, (JsonObject)NetPiJson.ToNode(args)!))!;

    private static async Task<FakePluginContext> Started(string root, IEmbeddingService? embedder)
    {
        var ctx = Context(root, embedder);
        await new CodeSearchPlugin().StartAsync(ctx, CancellationToken.None);
        return ctx;
    }

    private static string Repo(string name, bool git = true)
    {
        var dir = T.TempDir(name);
        if (git && !TestGit.Run(dir, "init", "-q")) Check.Skip("git is not on PATH");
        return dir;
    }

    /// <summary>Query → the file that implements it, from this repository: plain-words questions first, then bare identifiers.</summary>
    private static readonly (string Query, string File)[] RankingSet =
    [
        ("where is the reload gate that waits for agent runs to end", "src/NetPI.Host/Plugins/ReloadGate.cs"),
        ("how are tool results truncated to fit the context", "src/NetPI.Abstractions/Tools.cs"),
        ("parse server sent events from a streaming response", "shared/ProviderKit/SseReader.cs"),
        ("error when the session's workspace folder is gone", "src/NetPI.Contracts/Workspaces.cs"),
        ("reciprocal rank fusion of word and meaning rankings", "shared/SearchKit/Lexical.cs"),
        ("split a source file into passages at declarations", "plugins/NetPI.CodeSearch/Chunker.cs"),
        ("which messages to summarize when compacting a chat", "plugins/NetPI.Compaction/CompactionPlanner.cs"),
        ("parse the yaml frontmatter of a skill file", "plugins/NetPI.Skills/Frontmatter.cs"),
        ("list the git tracked files while honouring gitignore", "plugins/NetPI.CodeSearch/FileSource.cs"),
        ("the skill tool that loads a skill's instructions", "plugins/NetPI.Skills/SkillTool.cs"),
        ("ReloadGate", "src/NetPI.Host/Plugins/ReloadGate.cs"),
        ("ToolResultLimit", "src/NetPI.Abstractions/Tools.cs"),
        ("SseReader", "shared/ProviderKit/SseReader.cs"),
        ("WorkspaceUnavailableException", "src/NetPI.Contracts/Workspaces.cs"),
        ("CompactionPlanner", "plugins/NetPI.Compaction/CompactionPlanner.cs"),
    ];

    public static void Register(TestRunner r)
    {
        r.Add("codesearch chunker: C# is cut at its type and members, comments and attributes go with them, the class names its members' signatures", () =>
        {
            var chunks = Chunker.Split("Demo/RadioQueue.cs", CSharp, 80, 8000);
            var lines = CSharp.Replace("\r\n", "\n").Split('\n');
            var topUp = chunks.Single(c => c.Signature.Contains("TopUp"));
            Check.Equal("public sealed class RadioQueue : IDisposable › public void TopUp(int songsPerBatch)", topUp.Signature);
            Check.Contains(lines[topUp.Start - 1], "/// <summary>Adds songs", "the doc comment above the method starts its passage");
            Check.Contains(topUp.Text, "[Obsolete(\"x\")]");
            Check.True(topUp.End >= 26 && !topUp.Text.Contains("Dispose"), $"ends before the next member: line {topUp.End}");
            var cls = chunks.Single(c => c.Signature.StartsWith("public sealed class RadioQueue") && !c.Signature.Contains('›'));
            Check.Contains(cls.Text, "/// <summary>Keeps the radio's queue topped up.");
            Check.Contains(cls.Text, "private int _count;");
            Check.True(chunks.Any(c => c.Signature.Contains("public void Dispose()")), string.Join(" | ", chunks.Select(c => c.Signature)));
            Check.True(chunks.All(c => c.Text == string.Join("\n", lines[(c.Start - 1)..c.End])), "every passage is exactly its lines");
            Check.False(chunks.Any(c => c.Signature.Contains("Console.WriteLine")), "a call is not a declaration");
            return Task.CompletedTask;
        });

        r.Add("codesearch chunker: TS, Python, Go, Rust, Svelte and markdown headings; long declarations in parts; unknown files in windows", () =>
        {
            string Sigs(string path, string text) => string.Join(" | ", Chunker.Split(path, text, 80, 8000).Select(c => c.Signature));
            var ts = "import x from 'y';\n\nexport function alpha(a: number): number {\n  return a + 1;\n  // more\n  // lines\n}\n\nexport class Beta {\n  private n = 0;\n  constructor(n: number) {\n    this.n = n;\n    this.n++;\n    this.n++;\n  }\n  async gamma(x: string): Promise<void> {\n    if (x) {\n      console.log(x);\n    }\n  }\n}\nexport const delta = async (q) => {\n  return q;\n  // 1\n  // 2\n};\n";
            var sigs = Sigs("a/b.ts", ts);
            Check.Contains(sigs, "export function alpha(a: number): number");
            Check.Contains(sigs, "export class Beta › async gamma(x: string): Promise<void>");
            Check.Contains(sigs, "export const delta = async (q) =>");
            Check.NotContains(sigs, "if (x)");
            Check.Contains(Sigs("m.py", "import os\n\nclass Store:\n    def get(self, k):\n        return 1\n        # a\n        # b\n\n    async def put(self, k, v):\n        pass\n        # a\n        # b\n"), "class Store: › async def put(self, k, v):");
            Check.Contains(Sigs("m.go", "package m\n\nfunc Run(x int) error {\n\treturn nil\n\t// a\n\t// b\n}\n\ntype Server struct {\n\tA int\n\tB int\n\tC int\n}\n"), "func Run(x int) error");
            Check.Contains(Sigs("m.rs", "use std::io;\n\npub struct Pool {\n    a: u8,\n    b: u8,\n}\n\nimpl Pool {\n    pub async fn take(&self) -> u8 {\n        self.a\n        // a\n        // b\n    }\n}\n"), "impl Pool › pub async fn take(&self) -> u8");
            Check.Contains(Sigs("C.svelte", "<script>\n  let n = $state(0);\n  function inc() {\n    n++;\n    n++;\n    n++;\n  }\n</script>\n\n<button onclick={inc}>{n}</button>\n<p>a</p>\n<p>b</p>\n"), "<script> › function inc()");
            var md = Sigs("d.md", "# Title\n\nintro\nmore\nmore\n\n## Setup\n\n```sh\n# not a heading\nnpm ci\n```\nmore\nmore\n\n### Details\n\ntext\nmore\nmore\n");
            Check.Contains(md, "# Title › ## Setup › ### Details");
            Check.NotContains(md, "not a heading");

            var longBody = "public class Big\n{\n    public void Huge()\n    {\n" + string.Join("\n", Enumerable.Range(0, 300).Select(i => $"        Call{i}();")) + "\n    }\n}\n";
            var parts = Chunker.Split("Big.cs", longBody, 80, 8000);
            Check.True(parts.Count(c => c.Signature.Contains("Huge() (part")) >= 4, string.Join(" | ", parts.Select(c => c.Signature)));
            Check.True(parts.All(c => c.End - c.Start + 1 <= 80), "no part is longer than the cap");
            var plain = Chunker.Split("notes.txt", string.Join("\n", Enumerable.Range(0, 150).Select(i => $"line {i}")), 80, 8000);
            Check.True(plain.Count >= 3 && plain[0].Start == 1 && plain[^1].End == 150, $"{plain.Count} windows over 150 lines");

            var tokens = Lexical.CodeTokens("CodeSearchIndex HTMLParser err_quix_4471 x64").ToList();
            foreach (var t in new[] { "codesearchindex", "code", "search", "index", "htmlparser", "html", "parser", "err_quix_4471", "err", "quix", "4471" })
                Check.True(tokens.Contains(t), $"{t} in {string.Join(",", tokens)}");
            return Task.CompletedTask;
        });

        r.Add("codesearch: git's files only (ignored, generated, binary and large ones left out); a refresh re-reads only changed files and drops deleted ones", async () =>
        {
            var root = Repo("cs-incremental");
            T.WriteText(root, ".gitignore", "secret/\n");
            T.WriteText(root, "src/RadioQueue.cs", CSharp);
            T.WriteText(root, "src/Other.cs", "public class Other\n{\n    public void Frobnicate()\n    {\n        var timer = 1;\n    }\n}\n");
            T.WriteText(root, "src/Gone.cs", "public class Gone\n{\n    public void Vanish() { }\n}\n");
            T.WriteText(root, "secret/Hidden.cs", "public class Hidden { public void Frobnicate() { } }\n");
            T.WriteText(root, "web/bundle.js", "var a=1;" + new string('x', 5000) + "Frobnicate\n");
            T.WriteBytes(root, "data/blob.dat", [1, 0, 2, 0, (byte)'F']);
            T.WriteText(root, "big/Huge.cs", "// Frobnicate\n" + new string('a', 40 * 1024));
            var embedder = new BagOfWords();
            using var ctx = await Started(root, embedder);
            ctx.SettingsFake.Set("codesearch.maxFileKb", JsonValue.Create(16));

            var first = await Rpc(ctx, "codesearch.reindex", new { root });
            Check.True(first["git"]!.GetValue<bool>(), first.ToJsonString());
            Check.Equal(4, first["files"]!.GetValue<int>(), first.ToJsonString()); // .gitignore, Gone.cs, Other.cs, RadioQueue.cs
            Check.Equal(1, first["skipped"]!["large"]!.GetValue<int>());
            Check.Equal(1, first["skipped"]!["binary"]!.GetValue<int>());
            Check.Equal(1, first["skipped"]!["generated"]!.GetValue<int>());
            var passages = first["passages"]!.GetValue<int>();
            Check.Equal(passages, first["embedded"]!.GetValue<int>(), "every passage embedded");
            var found = await Rpc(ctx, "codesearch.search", new { root, query = "Frobnicate" });
            var paths = ((JsonArray)found["passages"]!).Select(p => p!["path"]!.GetValue<string>()).ToList();
            Check.Equal("src/Other.cs", paths.FirstOrDefault(), found.ToJsonString());
            Check.False(paths.Any(p => p.StartsWith("secret/") || p.StartsWith("web/") || p.StartsWith("big/")), string.Join(",", paths));

            // nothing changed: nothing read, nothing embedded
            var embeddedBefore = embedder.Documents.Count;
            var again = await Rpc(ctx, "codesearch.reindex", new { root });
            Check.Equal(0, again["lastChanged"]!.GetValue<int>());
            Check.Equal(embeddedBefore, embedder.Documents.Count);

            // touched without a change: re-hashed, kept; one method edited: only its passage is embedded again; one deleted; one new
            File.SetLastWriteTimeUtc(Path.Combine(root, "src", "Other.cs"), DateTime.UtcNow.AddMinutes(1));
            File.WriteAllText(Path.Combine(root, "src", "RadioQueue.cs"), CSharp.Replace("Console.WriteLine(\"disposed\");", "Console.WriteLine(\"gone for good\");"));
            File.SetLastWriteTimeUtc(Path.Combine(root, "src", "RadioQueue.cs"), DateTime.UtcNow.AddMinutes(2));
            File.Delete(Path.Combine(root, "src", "Gone.cs"));
            T.WriteText(root, "docs/Notes.md", "# Notes\n\nThe untracked note about Zanzibar.\n");
            var second = await Rpc(ctx, "codesearch.reindex", new { root });
            Check.Equal(2, second["lastChanged"]!.GetValue<int>(), second.ToJsonString()); // RadioQueue.cs, docs/Notes.md
            Check.Equal(1, second["lastRemoved"]!.GetValue<int>());
            var fresh = embedder.Documents.Skip(embeddedBefore).ToList();
            Check.True(fresh.Count == 2 && fresh.Any(t => t.Contains("gone for good")) && fresh.Any(t => t.Contains("Zanzibar")), string.Join("\n---\n", fresh));
            var zanzibar = await Rpc(ctx, "codesearch.search", new { root, query = "zanzibar" });
            Check.Equal("docs/Notes.md", zanzibar["passages"]![0]!["path"]!.GetValue<string>(), "a new untracked file is found");
            Check.Equal(4, second["files"]!.GetValue<int>(), second.ToJsonString()); // .gitignore, Other.cs, RadioQueue.cs, Notes.md
            var vanish = await Rpc(ctx, "codesearch.search", new { root, query = "Vanish" });
            Check.False(vanish.ToJsonString().Contains("Gone.cs"), "a deleted file is not found");
        });

        r.Add("codesearch: a restart embeds nothing again (vectors are stored per passage), and a second checkout of the same files reuses them", async () =>
        {
            var root = Repo("cs-restart");
            T.WriteText(root, "src/RadioQueue.cs", CSharp);
            var copy = Repo("cs-restart-copy");
            T.WriteText(copy, "src/RadioQueue.cs", CSharp);
            var embedder = new BagOfWords();
            using var ctx = await Started(root, embedder);
            var one = await Rpc(ctx, "codesearch.reindex", new { root });
            var n1 = embedder.Documents.Count;
            Check.Equal(one["passages"]!.GetValue<int>(), n1, "each passage embedded once");
            var two = await Rpc(ctx, "codesearch.reindex", new { root = copy });
            Check.Equal(n1, embedder.Documents.Count, "the second checkout holds the same passages: nothing embedded again");
            Check.Equal(one["passages"]!.GetValue<int>(), two["embedded"]!.GetValue<int>());

            // a restart or reload: a new index over the same store reads the files again and finds their vectors stored
            using var fresh = new CodeIndex(ctx);
            var ri = fresh.Get(root);
            await fresh.EnsureFresh(ri, force: true);
            await ri.Embedding!;
            Check.Equal(n1, embedder.Documents.Count, "nothing embedded after the restart");
            Check.Equal(one["passages"]!.GetValue<int>(), fresh.Embedded(ri));

            // the sweep keeps what files refer to and drops what nothing refers to any more
            Check.Equal(0, fresh.Sweep(CancellationToken.None)["vectors"]!.GetValue<int>());
            fresh.Forget(root);
            fresh.Forget(copy);
            Check.Equal(n1, fresh.Sweep(CancellationToken.None)["vectors"]!.GetValue<int>(), "no file refers to them: dropped");
        });

        r.Add("codesearch: words and meaning fused (an exact identifier first, a synonym found by meaning); words only without a model, nothing embedded", async () =>
        {
            var root = Repo("cs-fusion");
            T.WriteText(root, "src/Gate.cs", "public class Gate\n{\n    /// <summary>Lets calls through slowly.</summary>\n    public void Throttle(int perSecond)\n    {\n        Thread.Sleep(1000 / perSecond);\n    }\n}\n");
            for (var i = 0; i < 6; i++)
                T.WriteText(root, $"src/Calls{i}.cs", $"public class Calls{i}\n{{\n    public void Run{i}()\n    {{\n        // calls per second, attempt {i}\n        Send();\n    }}\n}}\n");
            T.WriteText(root, "src/Needle.cs", "public class Needle\n{\n    public void Fail()\n    {\n        throw new Exception(\"ERR_QUIX_4471 calls per second\");\n    }\n}\n");
            var embedder = new BagOfWords();
            embedder.Synonyms["limiter"] = "throttle";
            embedder.Synonyms["rate"] = "throttle";
            using var ctx = await Started(root, embedder);
            await Rpc(ctx, "codesearch.reindex", new { root });

            var exact = await Rpc(ctx, "codesearch.search", new { root, query = "ERR_QUIX_4471 calls per second" });
            Check.True(exact["meaning"]!.GetValue<bool>());
            Check.Equal("src/Needle.cs", exact["passages"]![0]!["path"]!.GetValue<string>(), exact.ToJsonString());
            var synonym = await Rpc(ctx, "codesearch.search", new { root, query = "rate limiter" });
            Check.Equal("src/Gate.cs", synonym["passages"]![0]!["path"]!.GetValue<string>(), synonym.ToJsonString());
            Check.Equal(0, synonym["passages"]![0]!["words"]!.GetValue<int>(), "found by meaning alone");

            var tool = ctx.ToolsFake.Get("code_search")!;
            var result = await tool.ExecuteAsync(T.Ctx(root), T.Args(new { query = "Throttle perSecond" }), CancellationToken.None);
            Check.False(result.IsError, result.Content);
            Check.Contains(result.Content, "src/Gate.cs:1-8  public class Gate › public void Throttle(int perSecond)");
            Check.Contains(result.Content, "by meaning and words");

            // words only: no model
            var plainRoot = Repo("cs-words");
            T.WriteText(plainRoot, "src/Gate.cs", "public class Gate\n{\n    public void Throttle(int perSecond)\n    {\n        Thread.Sleep(1000 / perSecond);\n    }\n}\n");
            var off = new BagOfWords(model: null);
            using var plain = await Started(plainRoot, off);
            var status = await Rpc(plain, "codesearch.reindex", new { root = plainRoot });
            Check.Equal(0, status["embedded"]!.GetValue<int>());
            var words = await plain.ToolsFake.Get("code_search")!.ExecuteAsync(T.Ctx(plainRoot), T.Args(new { query = "throttle" }), CancellationToken.None);
            Check.False(words.IsError, words.Content);
            Check.Contains(words.Content, "by words only");
            Check.Contains(words.Content, "src/Gate.cs:1-7");
            Check.Equal(0, off.Documents.Count, "nothing sent to an embedding server");
            var meaningOnly = await plain.ToolsFake.Get("code_search")!.ExecuteAsync(T.Ctx(plainRoot), T.Args(new { query = "rate limiter" }), CancellationToken.None);
            Check.Contains(meaningOnly.Content, "No passage matches");
            // and with no embedding service at all
            using var none = await Started(plainRoot, null);
            var bare = await none.ToolsFake.Get("code_search")!.ExecuteAsync(T.Ctx(plainRoot), T.Args(new { query = "throttle" }), CancellationToken.None);
            Check.Contains(bare.Content, "src/Gate.cs:1-7");
        });

        r.Add("codesearch: the session's workspace — a bound session searches its checkout, a broken binding is an error, path stays inside", async () =>
        {
            var project = Repo("cs-project");
            T.WriteText(project, "src/Main.cs", "public class Main\n{\n    public void OnlyInProject()\n    {\n    }\n}\n");
            T.WriteText(project, ".gitignore", ".worktrees/\n");
            var worktree = Path.Combine(project, ".worktrees", "agent");
            Directory.CreateDirectory(worktree);
            TestGit.Run(worktree, "init", "-q"); // a checkout of its own, as a real worktree is (its .git names it)
            T.WriteText(worktree, "src/Main.cs", "public class Main\n{\n    public void OnlyInWorktree()\n    {\n    }\n}\n");
            using var ctx = await Started(project, null);
            var tool = ctx.ToolsFake.Get("code_search")!;

            var inProject = await tool.ExecuteAsync(T.Ctx(project), T.Args(new { query = "OnlyInWorktree" }), CancellationToken.None);
            Check.NotContains(inProject.Content, "OnlyInWorktree", "the ignored nested checkout is not part of the project's index");
            var binding = new WorkspaceBinding("wsp_1", worktree, Kind: "worktree");
            var bound = await tool.ExecuteAsync(T.Ctx(project, binding), T.Args(new { query = "OnlyInWorktree" }), CancellationToken.None);
            Check.Contains(bound.Content, "src/Main.cs:1-6", "the bound session searches its own checkout, even with the project as its folder");
            Check.Equal(CodeIndex.Key(worktree), ((JsonObject)bound.Details!)["root"]!.GetValue<string>());

            // bound, but the checkout is broken: the resolver's error, never the project
            var session = ctx.Sessions.CreateSession(new SessionInfo { Title = "bound" });
            ctx.Services.Register<IWorkspaceResolver>(new BrokenResolver());
            var broken = await tool.ExecuteAsync(new ToolContext { SessionId = session.Id, AgentId = "a", CallId = "c", Cwd = project, Services = ctx.Services, Events = ctx.Events },
                T.Args(new { query = "OnlyInProject" }), CancellationToken.None);
            Check.True(broken.IsError, broken.Content);
            Check.Contains(broken.Content, "workspace is gone");
            await Check.ThrowsAsync<RpcException>(() => Rpc(ctx, "codesearch.search", new { sessionId = session.Id, query = "x" }));

            var outside = await tool.ExecuteAsync(T.Ctx(project), T.Args(new { query = "Main", path = "../elsewhere" }), CancellationToken.None);
            Check.True(outside.IsError && outside.Content.Contains("outside the workspace"), outside.Content);
            var under = await tool.ExecuteAsync(T.Ctx(project), T.Args(new { query = "Main", path = "docs" }), CancellationToken.None);
            Check.Contains(under.Content, "No passage under docs");

            // without git: the walk respects .gitignore and does not enter nested repositories
            var folder = Repo("cs-walk", git: false);
            T.WriteText(folder, ".gitignore", "*.log\nout/\n!keep.log\n/rooted.txt\n");
            T.WriteText(folder, "a.txt", "walkword here");
            T.WriteText(folder, "x.log", "walkword log");
            T.WriteText(folder, "keep.log", "walkword kept");
            T.WriteText(folder, "rooted.txt", "walkword rooted");
            T.WriteText(folder, "sub/rooted.txt", "walkword not rooted");
            T.WriteText(folder, "out/o.txt", "walkword out");
            T.WriteText(folder, "nested/.git/HEAD", "ref: x");
            T.WriteText(folder, "nested/n.txt", "walkword nested");
            var walked = await FileSource.ListAsync(folder, 100, CancellationToken.None);
            Check.False(walked.Git);
            Check.Equal("a.txt,keep.log,sub/rooted.txt", string.Join(",", walked.Files.Where(f => f.EndsWith(".txt") || f.EndsWith(".log")).Order()));
        });

        r.Add("codesearch: the metric — tokens returned against the files' tokens, per search in details and summed in the status", async () =>
        {
            var root = Repo("cs-metric");
            var big = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"public class Filler{i}\n{{\n    public void Work{i}()\n    {{\n        var value = {i};\n        value++;\n    }}\n}}"));
            T.WriteText(root, "src/Big.cs", big.Replace("Work42()", "Work42Gizmo()"));
            using var ctx = await Started(root, null);
            await Rpc(ctx, "codesearch.reindex", new { root });
            var found = await Rpc(ctx, "codesearch.search", new { root, query = "gizmo", limit = 1 });
            var returned = found["returnedTokens"]!.GetValue<long>();
            var files = found["fileTokens"]!.GetValue<long>();
            Check.True(returned > 0 && files > returned * 5, $"{returned} returned of {files}");
            Check.Equal((long)Math.Ceiling(new FileInfo(Path.Combine(root, "src", "Big.cs")).Length / 4.0), files);
            await Rpc(ctx, "codesearch.search", new { root, query = "gizmo", limit = 1 });
            var status = await Rpc(ctx, "codesearch.status", new { });
            Check.Equal(2L, status["totals"]!["queries"]!.GetValue<long>());
            Check.Equal(files * 2, status["totals"]!["fileTokens"]!.GetValue<long>());
            Check.Equal(1, ((JsonArray)status["roots"]!).Count);
        });

        r.Add("codesearch: a search during the first build answers from what is indexed and says it is still indexing", async () =>
        {
            var root = Repo("cs-progress");
            for (var i = 0; i < 40; i++) T.WriteText(root, $"src/F{i}.cs", $"public class F{i}\n{{\n    public void Hello{i}() {{ }}\n}}\n");
            using var ctx = await Started(root, null);
            var old = CodeSearcher.Wait;
            CodeSearcher.Wait = TimeSpan.Zero;
            try
            {
                var result = await ctx.ToolsFake.Get("code_search")!.ExecuteAsync(T.Ctx(root), T.Args(new { query = "hello" }), CancellationToken.None);
                Check.False(result.IsError, result.Content);
                Check.Contains(result.Content, "still indexing");
                Check.True(((JsonObject)result.Details!)["indexing"]!.GetValue<bool>());
            }
            finally { CodeSearcher.Wait = old; }
            await Rpc(ctx, "codesearch.reindex", new { root });
            var done = await ctx.ToolsFake.Get("code_search")!.ExecuteAsync(T.Ctx(root), T.Args(new { query = "hello" }), CancellationToken.None);
            Check.NotContains(done.Content, "still indexing");
        });

        r.Add("codesearch live: this repository's index size and search latency (NETPI_CODESEARCH_LIVE=1)", async () =>
        {
            if (Environment.GetEnvironmentVariable("NETPI_CODESEARCH_LIVE") != "1") Check.Skip("set NETPI_CODESEARCH_LIVE=1 to index this repository");
            var root = NetPI.TestShared.T.RepoRoot;
            using var ctx = await Started(root, null);
            var watch = Stopwatch.StartNew();
            var status = await Rpc(ctx, "codesearch.reindex", new { root });
            var build = watch.ElapsedMilliseconds;
            Console.WriteLine($"  build {build} ms: {status.ToJsonString()}");
            watch.Restart();
            var again = await Rpc(ctx, "codesearch.reindex", new { root });
            Console.WriteLine($"  refresh (nothing changed) {watch.ElapsedMilliseconds} ms");
            var mem = GC.GetTotalMemory(forceFullCollection: true);
            Console.WriteLine($"  managed heap after build: {mem / 1024 / 1024} MB");
            foreach (var q in new[] { "hybrid memory search reciprocal rank fusion", "WorkspaceUnavailableException", "reload gate deferred plugin reload", "how are tool results truncated", "SseReader" })
            {
                watch.Restart();
                var found = await Rpc(ctx, "codesearch.search", new { root, query = q, limit = 8 });
                var top = string.Join(", ", ((JsonArray)found["passages"]!).Take(3).Select(p => $"{p!["path"]}:{p["start"]}"));
                Console.WriteLine($"  \"{q}\": {watch.ElapsedMilliseconds} ms (search {found["ms"]} ms), {found["returnedTokens"]} of {found["fileTokens"]} tokens; {top}");
            }
            Check.True(again["files"]!.GetValue<int>() > 100);
        });

        r.Add("codesearch ranking: natural-language and identifier queries find the implementation file (NETPI_CODESEARCH_LIVE=1; words only, plus meaning with NETPI_CODESEARCH_EMBED_URL/_MODEL/_PREFIX; prints hit@1 / hit@4)", async () =>
        {
            if (Environment.GetEnvironmentVariable("NETPI_CODESEARCH_LIVE") != "1") Check.Skip("set NETPI_CODESEARCH_LIVE=1 to index this repository");
            var words = await Ranking(null, "words only");
            Check.True(words.Named4 == words.Named, "every identifier query finds its file in the top 4");
            Check.True(words.Natural4 >= words.Natural * 6 / 10, $"natural-language hit@4 {words.Natural4}/{words.Natural}");
            var url = Environment.GetEnvironmentVariable("NETPI_CODESEARCH_EMBED_URL");
            var model = Environment.GetEnvironmentVariable("NETPI_CODESEARCH_EMBED_MODEL");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model)) return;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var both = await Ranking(new HttpEmbedder(http, url.TrimEnd('/'), model, Environment.GetEnvironmentVariable("NETPI_CODESEARCH_EMBED_PREFIX") ?? ""), $"words + meaning ({model})");
            Check.True(both.Named4 == both.Named, "with meaning every identifier query still finds its file in the top 4");
            Check.True(both.Natural4 >= words.Natural4, $"meaning does not lose natural-language hits: {both.Natural4} vs {words.Natural4}");
        });
    }

    private sealed record RankStats(int Natural, int Natural1, int Natural4, int Named, int Named1, int Named4);

    /// <summary>Indexes this repository (waiting for the vectors when there is an embedder), runs the RankingSet and prints the hits and the timings.</summary>
    private static async Task<RankStats> Ranking(IEmbeddingService? embedder, string label)
    {
        var root = NetPI.TestShared.T.RepoRoot;
        using var ctx = await Started(root, embedder);
        var watch = Stopwatch.StartNew();
        var status = await Rpc(ctx, "codesearch.reindex", new { root });
        var indexMs = watch.ElapsedMilliseconds;
        while (embedder is not null && (status["embedding"]?.GetValue<bool>() == true || status["embedded"]!.GetValue<int>() < status["passages"]!.GetValue<int>()) && status["embedError"] is null && watch.ElapsedMilliseconds < 600_000)
        {
            await Task.Delay(250);
            status = await Rpc(ctx, "codesearch.reindex", new { root });
        }
        var totalMs = watch.ElapsedMilliseconds;
        Console.WriteLine($"  [{label}] passages {status["passages"]}, embedded {status["embedded"]}, index {indexMs} ms, index+embed {totalMs} ms, embedError {status["embedError"]?.ToString() ?? "-"}");
        int natural = 0, named = 0, h1 = 0, h4 = 0, n1 = 0, n4 = 0;
        foreach (var (query, expected) in RankingSet)
        {
            var found = await Rpc(ctx, "codesearch.search", new { root, query, limit = 8 });
            var files = ((JsonArray)found["passages"]!).Select(p => p!["path"]!.GetValue<string>()).Distinct().ToList();
            var rank = files.IndexOf(expected) + 1;
            if (!query.Contains(' ')) { named++; if (rank == 1) n1++; if (rank is >= 1 and <= 4) n4++; }
            else { natural++; if (rank == 1) h1++; if (rank is >= 1 and <= 4) h4++; }
            Console.WriteLine($"  rank {(rank == 0 ? "-" : rank.ToString())}  \"{query}\" -> {expected}  [{string.Join(", ", files.Take(4).Select(f => System.IO.Path.GetFileName(f)))}]");
        }
        Console.WriteLine($"  [{label}] natural-language: hit@1 {h1}/{natural}, hit@4 {h4}/{natural}; identifiers: hit@1 {n1}/{named}, hit@4 {n4}/{named}");
        return new RankStats(natural, h1, h4, named, n1, n4);
    }

    /// <summary>POST /v1/embeddings as the Embeddings plugin does (a query gets the prefix, a document none), for the live ranking test.</summary>
    private sealed class HttpEmbedder(HttpClient http, string root, string model, string queryPrefix) : IEmbeddingService
    {
        public string? Model => model;
        public bool Available => true;

        public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct)
        {
            var prefix = request.Kind == EmbeddingKind.Query ? queryPrefix : "";
            var vectors = new List<float[]>();
            for (var at = 0; at < request.Texts.Count; at += 32)
            {
                var body = new JsonObject { ["model"] = model, ["input"] = new JsonArray(request.Texts.Skip(at).Take(32).Select(t => (JsonNode)(prefix + t)).ToArray()) };
                using var res = await http.PostAsync(root + "/v1/embeddings", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct);
                var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
                if (!res.IsSuccessStatusCode || json?["data"] is not JsonArray data) throw new EmbeddingException("http", $"HTTP {(int)res.StatusCode}: {json}");
                foreach (var d in data) vectors.Add(VectorMath.Normalize(d!["embedding"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray()));
            }
            return new EmbeddingResult(model, vectors.Count == 0 ? 0 : vectors[0].Length, vectors);
        }
    }

    private sealed class BrokenResolver : IWorkspaceResolver
    {
        public WorkspaceBinding? Resolve(SessionInfo? session) => throw new WorkspaceUnavailableException("The session's workspace is gone.");
        public WorkspaceBinding Require(SessionInfo session) => throw new WorkspaceUnavailableException("The session's workspace is gone.");
        public string CwdOf(SessionInfo session) => throw new WorkspaceUnavailableException("The session's workspace is gone.");
        public string IdentityOf(SessionInfo session) => "broken";
    }
}
