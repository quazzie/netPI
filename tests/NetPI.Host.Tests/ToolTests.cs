using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Tools;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>PLAN §50: tool behavior tests against a temp workspace (fixtures on disk).</summary>
public class ToolTests
{
    private static string MakeWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netpi-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static JsonElement Args(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static string TextOf(ToolResult r)
        => string.Join("\n", r.Parts.Select(p => p is TextPart t ? t.Text : p.Kind));

    // ---- read -------------------------------------------------------------

    [Fact]
    public async Task Read_LineNumbersAndBounded()
    {
        var ws = MakeWorkspace();
        try
        {
            File.WriteAllLines(Path.Combine(ws, "f.txt"), Enumerable.Range(1, 10).Select(i => $"line{i}"));
            var ctx = new ToolContext(Args("""{"path":"f.txt"}"""), ws, "s", null);
            var r = await new ReadTool().ExecuteAsync(ctx, CancellationToken.None);

            Assert.False(r.IsError);
            var text = TextOf(r);
            Assert.Contains("     1│line1", text);
            Assert.Contains("    10│line10", text);
            Assert.Contains("Showing lines 1-10 of 10.", text);

            // offset pages from the requested line
            var r2 = await new ReadTool().ExecuteAsync(ctx with { Arguments = Args("""{"path":"f.txt","offset":4,"limit":3}""") }, CancellationToken.None);
            Assert.Contains("     4│line4", TextOf(r2));
            Assert.Contains("     6│line6", TextOf(r2));
            Assert.DoesNotContain("line7", TextOf(r2));
            Assert.Contains("Use offset=7 to continue.", TextOf(r2));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task Read_MissingFileIsUsefulError()
    {
        var ws = MakeWorkspace();
        try
        {
            var ctx = new ToolContext(Args("""{"path":"nope.txt"}"""), ws, "s", null);
            var r = await new ReadTool().ExecuteAsync(ctx, CancellationToken.None);
            Assert.True(r.IsError);
            Assert.Contains("File not found", TextOf(r));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task Read_BinaryFileRefused()
    {
        var ws = MakeWorkspace();
        try
        {
            File.WriteAllBytes(Path.Combine(ws, "bin.dat"), new byte[] { 1, 2, 0, 3, 4, 0, 5, 6 });
            var ctx = new ToolContext(Args("""{"path":"bin.dat"}"""), ws, "s", null);
            var r = await new ReadTool().ExecuteAsync(ctx, CancellationToken.None);
            Assert.True(r.IsError);
            Assert.Contains("binary", TextOf(r), StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(ws, true); }
    }

    // ---- write ------------------------------------------------------------

    [Fact]
    public async Task Write_CreatesFileAndParents()
    {
        var ws = MakeWorkspace();
        try
        {
            var ctx = new ToolContext(Args("""{"path":"a/b.txt","content":"hello"}"""), ws, "s", null);
            var r = await new WriteTool().ExecuteAsync(ctx, CancellationToken.None);
            Assert.False(r.IsError);
            Assert.Equal("hello", File.ReadAllText(Path.Combine(ws, "a", "b.txt")));
        }
        finally { Directory.Delete(ws, true); }
    }

    // ---- edit -------------------------------------------------------------

    [Fact]
    public async Task Edit_UniqueMatchReplaces()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = Path.Combine(ws, "e.txt");
            File.WriteAllText(path, "alpha\ngamma\ndelta");
            var ctx = new ToolContext(Args("""{"path":"e.txt","oldText":"gamma","newText":"GAMMA"}"""), ws, "s", null);
            var r = await new EditTool().ExecuteAsync(ctx, CancellationToken.None);
            Assert.False(r.IsError);
            Assert.Equal("alpha\nGAMMA\ndelta", File.ReadAllText(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task Edit_ZeroMatchesFails()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = Path.Combine(ws, "e.txt");
            File.WriteAllText(path, "alpha\ngamma\ndelta");
            var ctx = new ToolContext(Args("""{"path":"e.txt","oldText":"nope","newText":"x"}"""), ws, "s", null);
            var r = await new EditTool().ExecuteAsync(ctx, CancellationToken.None);
            Assert.True(r.IsError);
            Assert.Contains("0 matches", TextOf(r));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task Edit_MultipleMatchesFails()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = Path.Combine(ws, "dup.txt");
            File.WriteAllText(path, "same\nsame\n");
            var ctx = new ToolContext(Args("""{"path":"dup.txt","oldText":"same","newText":"x"}"""), ws, "s", null);
            var r = await new EditTool().ExecuteAsync(ctx, CancellationToken.None);
            Assert.True(r.IsError);
            Assert.Contains("matches 2 times", TextOf(r));
        }
        finally { Directory.Delete(ws, true); }
    }

    // ---- grep -------------------------------------------------------------

    [Fact]
    public async Task Grep_MatchesWorkspaceRelativeWithLineNumbers()
    {
        var ws = MakeWorkspace();
        try
        {
            File.WriteAllLines(Path.Combine(ws, "a.txt"), new[] { "one", "needle here", "three" });
            Directory.CreateDirectory(Path.Combine(ws, "sub"));
            File.WriteAllLines(Path.Combine(ws, "sub", "b.txt"), new[] { "also needle" });

            var ctx = new ToolContext(Args("""{"pattern":"needle","path":""}"""), ws, "s", null);
            var r = await new GrepTool().ExecuteAsync(ctx, CancellationToken.None);
            Assert.False(r.IsError);
            var text = TextOf(r);
            Assert.Contains("a.txt:2: needle here", text);
            Assert.Contains("sub/b.txt:1: also needle", text);
            Assert.DoesNotContain(ws.Replace('\\', '/'), text); // workspace-relative, not absolute
        }
        finally { Directory.Delete(ws, true); }
    }
}
