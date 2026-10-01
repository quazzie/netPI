using NetPI.Diagnostics;

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
    }
}
