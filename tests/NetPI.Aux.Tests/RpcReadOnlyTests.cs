using NetPI.Diagnostics;
using NetPI.Tools.Files;
using NetPI.Tools.Shell;

namespace NetPI.Aux.Tests;

/// <summary>
/// The read-only paths — <c>scripts/netpi.mjs</c> and the <c>diag</c> tool's rpc action — call only what
/// <c>rpc.list</c> marks <c>readOnly</c>, and they trust that flag and nothing else. So the flag on a registration is
/// the contract: a diagnostic view that is not marked is a view no tool can reach, and <c>node scripts/netpi.mjs
/// diag.overview</c> is refused (idea-o934y1).
/// </summary>
public static class RpcReadOnlyTests
{
    public static void Register(TestRunner r)
    {
        r.Add("rpc: every diag.* view is registered read-only; /reload is not", async () =>
        {
            var ctx = new FakePluginContext(T.TempDir("rpc-readonly"));
            await new DiagnosticsPlugin().StartAsync(ctx, CancellationToken.None);
            var flags = ctx.RpcFake.List().ToDictionary(m => m.Method, m => m.ReadOnly);

            var views = flags.Keys.Where(m => m.StartsWith("diag.", StringComparison.Ordinal)).OrderBy(m => m, StringComparer.Ordinal).ToArray();
            // Every method the plugin registers is accounted for here, so a new one cannot slip in unmarked.
            Check.Equal(17, views.Length, "diag.* methods registered");
            foreach (var m in views.Where(m => m != "diag.reload"))
                Check.True(flags[m], $"{m} only reads, so it is marked read-only");
            Check.False(flags["diag.reload"], "diag.reload reloads plugins — it changes the app, so the tools may not call it");
        });

        r.Add("rpc: the process views are read-only; killing a process and opening a path are not", async () =>
        {
            var ctx = new FakePluginContext(T.TempDir("rpc-readonly"));
            await new ShellPlugin().StartAsync(ctx, CancellationToken.None);
            await new FilesPlugin().StartAsync(ctx, CancellationToken.None);
            var all = ctx.RpcFake.List();

            foreach (var m in new[] { "processes.list", "processes.output" })
                Check.True(all.Any(x => x.Method == m && x.ReadOnly), $"{m} only reads, so it is marked read-only");

            // Every method these two plugins register is listed here, split by the flag it has to carry: a new one
            // cannot slip in unmarked (a read surface no tool can reach, or a write one every tool may call). The
            // fake registry records no owner, so they are grouped by their prefix instead.
            var expected = new Dictionary<string, (string[] ReadOnly, string[] Writable)>
            {
                ["processes."] = (["processes.list", "processes.output"], ["processes.kill"]),
                ["files."] = (["files.commits", "files.git", "files.list", "files.scope", "files.search"], ["files.open"]),
            };
            foreach (var (prefix, (readOnly, writable)) in expected)
            {
                var mine = all.Where(m => m.Method.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                Check.Equal(string.Join(" ", readOnly.Order(StringComparer.Ordinal)), string.Join(" ", mine.Where(m => m.ReadOnly).Select(m => m.Method).Order(StringComparer.Ordinal)),
                    $"{prefix}: the methods that only read");
                Check.Equal(string.Join(" ", writable.Order(StringComparer.Ordinal)), string.Join(" ", mine.Where(m => !m.ReadOnly).Select(m => m.Method).Order(StringComparer.Ordinal)),
                    $"{prefix}: the methods that change something");
            }
        });
    }
}
