using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Tools;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// docs/plans/file-tool-reliability.md §4: the text-editing engine and the
/// <c>edit</c>/<c>replace</c> tools — newline-tolerant matching, guarded
/// multi-occurrence replace, encoding/BOM preservation, exactness, and the
/// safe (atomic, never-truncating) write path. Run against real files in a
/// temp workspace, not just the runtime's mocked executors.
/// </summary>
public class FileToolReliabilityTests
{
    private static string MakeWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netpi-filetool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string F(string ws, string name) => Path.Combine(ws, name);

    private static string TextOf(ToolResult r)
        => string.Join("\n", r.Parts.Select(p => p is TextPart t ? t.Text : p.Kind));

    private static JsonElement Args(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static ToolContext Ctx(string json, string ws)
        => new(Args(json), ws, "s", null);

    /// <summary>Context from a .NET object (carries real CRLF/LF chars in strings).</summary>
    private static ToolContext CtxObj(object args, string ws)
        => new(JsonSerializer.SerializeToElement(args), ws, "s", null);

    // ====================================================================
    // CRLF/LF — all four file/search newline combinations, both tools,
    // including multiline needles and replacements.
    // ====================================================================

    [Theory]
    [InlineData("\n", "\n")]   // file LF,   search LF
    [InlineData("\n", "\r\n")]// file LF,   search CRLF
    [InlineData("\r\n", "\n")]// file CRLF, search LF
    [InlineData("\r\n", "\r\n")]// file CRLF, search CRLF
    public async Task Edit_LineEndingsAreEquivalent(string fileEol, string searchEol)
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "e.txt");
            File.WriteAllText(path, $"one{fileEol}TWO{fileEol}three", new UTF8Encoding(false));
            // Search for the "TWO<eol>three" join — LF or CRLF — with an LF newText;
            // the tool aligns the replacement to the FILE's line-ending style.
            var r = await new EditTool().ExecuteAsync(
                CtxObj(new { path = "e.txt", oldText = $"TWO{searchEol}three", newText = "TWO2\nTHREE" }, ws), CancellationToken.None);
            Assert.False(r.IsError, TextOf(r));
            Assert.Equal($"one{fileEol}TWO2{fileEol}THREE", File.ReadAllText(path));
            Assert.DoesNotContain("\r\r", File.ReadAllText(path)); // no doubled / stray CR
        }
        finally { Directory.Delete(ws, true); }
    }


    [Theory]
    [InlineData("\n", "\n")]
    [InlineData("\n", "\r\n")]
    [InlineData("\r\n", "\n")]
    [InlineData("\r\n", "\r\n")]
    public async Task Replace_LineEndingsAreEquivalent(string fileEol, string searchEol)
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "r.txt");
            File.WriteAllText(path, $"a{fileEol}x{fileEol}b{fileEol}x{fileEol}c", new UTF8Encoding(false));
            // One occurrence of "x<eol>b" (the first x); the second x is untouched.
            var r = await new ReplaceTool().ExecuteAsync(
                CtxObj(new { path = "r.txt", oldText = $"x{searchEol}b", newText = "X\nB", expectedCount = 1 }, ws), CancellationToken.None);
            Assert.False(r.IsError, TextOf(r));
            Assert.Equal($"a{fileEol}X{fileEol}B{fileEol}x{fileEol}c", File.ReadAllText(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task MixedEndings_LfAndCrlfOccurrencesAreEquivalent()
    {
        // One occurrence is CRLF-terminated, the other LF-terminated — both match
        // the LF-normalized needle, so edit is ambiguous and replace finds 2.
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "m.txt");
            File.WriteAllText(path, "q\r\nmark\r\nz\nmark\nend", new UTF8Encoding(false));
            // edit: 2 matches → ambiguous, unchanged.
            var e = await new EditTool().ExecuteAsync(Ctx("""{"path":"m.txt","oldText":"mark","newText":"MARK"}""", ws), CancellationToken.None);
            Assert.True(e.IsError);
            Assert.Contains("matches 2 times", TextOf(e));
            Assert.Equal("q\r\nmark\r\nz\nmark\nend", File.ReadAllText(path)); // unchanged

            // replace with expectedCount 2 → succeeds on BOTH spans.
            var p = await new ReplaceTool().ExecuteAsync(Ctx("""{"path":"m.txt","oldText":"mark","newText":"MARK","expectedCount":2}""", ws), CancellationToken.None);
            Assert.False(p.IsError, TextOf(p));
            Assert.Equal("q\r\nMARK\r\nz\nMARK\nend", File.ReadAllText(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task MultilineNeedleAndReplacement_AcrossEndings()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "ml.txt");
            File.WriteAllText(path, "head\r\nfoo\r\nbar\r\ntail", new UTF8Encoding(false));
            var r = await new EditTool().ExecuteAsync(Ctx("""{"path":"ml.txt","oldText":"foo\nbar","newText":"foo2\nbar2"}""", ws), CancellationToken.None);
            Assert.False(r.IsError, TextOf(r));
            Assert.Equal("head\r\nfoo2\r\nbar2\r\ntail", File.ReadAllText(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    // ====================================================================
    // Guarded replace
    // ====================================================================

    [Fact]
    public async Task Replace_CountMismatchDoesNotWrite()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "g.txt");
            File.WriteAllText(path, "aa bb aa");
            var before = File.ReadAllBytes(path);
            var r = await new ReplaceTool().ExecuteAsync(Ctx("""{"path":"g.txt","oldText":"aa","newText":"X","expectedCount":3}""", ws), CancellationToken.None);
            Assert.True(r.IsError);
            Assert.Contains("found 2", TextOf(r));
            Assert.Equal(before, File.ReadAllBytes(path)); // untouched
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task Replace_ZeroOccurrenceDoesNotWrite()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "z.txt");
            File.WriteAllText(path, "nothing here");
            var before = File.ReadAllBytes(path);
            var r = await new ReplaceTool().ExecuteAsync(Ctx("""{"path":"z.txt","oldText":"zzz","newText":"X","expectedCount":1}""", ws), CancellationToken.None);
            Assert.True(r.IsError);
            Assert.Contains("found 0", TextOf(r));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task Replace_AllOccurrences()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "all.txt");
            File.WriteAllText(path, "one two one two one");
            var r = await new ReplaceTool().ExecuteAsync(Ctx("""{"path":"all.txt","oldText":"one","newText":"1","expectedCount":3}""", ws), CancellationToken.None);
            Assert.False(r.IsError, TextOf(r));
            Assert.Contains("count=3", TextOf(r));
            Assert.Equal("1 two 1 two 1", File.ReadAllText(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task Replace_NonOverlappingNoCascade()
    {
        // "aaa" searching "aa" → ONE non-overlapping match; and a replacement that
        // contains oldText must NOT be rescanned (no cascade).
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "ov.txt");
            File.WriteAllText(path, "aaa");
            var r = await new ReplaceTool().ExecuteAsync(Ctx("""{"path":"ov.txt","oldText":"aa","newText":"[aa]","expectedCount":1}""", ws), CancellationToken.None);
            Assert.False(r.IsError, TextOf(r));
            Assert.Equal("[aa]a", File.ReadAllText(path)); // one match, no cascade

            // cascade guard: replace "a" with "ab" → the "a" INSIDE the replacement
            // is not re-matched (no cascade); result is exactly "ab".
            File.WriteAllText(path, "a");
            var r2 = await new ReplaceTool().ExecuteAsync(Ctx("""{"path":"ov.txt","oldText":"a","newText":"ab","expectedCount":1}""", ws), CancellationToken.None);
            Assert.False(r2.IsError, TextOf(r2));
            Assert.Equal("ab", File.ReadAllText(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task Replace_EmptyNewTextDeletes()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "del.txt");
            File.WriteAllText(path, "XmidXmidX");
            var r = await new ReplaceTool().ExecuteAsync(Ctx("""{"path":"del.txt","oldText":"Xmid","newText":"","expectedCount":2}""", ws), CancellationToken.None);
            Assert.False(r.IsError, TextOf(r));
            Assert.Equal("X", File.ReadAllText(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    // ====================================================================
    // Validation
    // ====================================================================

    [Theory]
    [InlineData("""{"oldText":"a","newText":"b","expectedCount":1}""", "path")]                 // missing path
    [InlineData("""{"path":"","oldText":"a","newText":"b","expectedCount":1}""", "path")]         // blank path
    [InlineData("""{"path":12,"oldText":"a","newText":"b","expectedCount":1}""", "path")]         // wrong-type path
    [InlineData("""{"path":"f.txt","newText":"b","expectedCount":1}""", "oldText")]               // missing oldText
    [InlineData("""{"path":"f.txt","oldText":"","newText":"b","expectedCount":1}""", "oldText")]  // empty needle
    [InlineData("""{"path":"f.txt","oldText":"a","expectedCount":1}""", "newText")]               // absent newText
    [InlineData("""{"path":"f.txt","oldText":"a","newText":"b"}""", "expectedCount")]             // missing expectedCount
    [InlineData("""{"path":"f.txt","oldText":"a","newText":"b","expectedCount":0}""", "expectedCount")]   // zero
    [InlineData("""{"path":"f.txt","oldText":"a","newText":"b","expectedCount":-2}""", "expectedCount")] // negative
    [InlineData("""{"path":"f.txt","oldText":"a","newText":"b","expectedCount":1.5}""", "expectedCount")]  // fractional
    [InlineData("""{"path":"f.txt","oldText":"a","newText":"b","expectedCount":"1"}""", "expectedCount")] // string
    public async Task Replace_InvalidArgsFailWithoutTouchingFile(string argsJson, string _)
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "f.txt");
            File.WriteAllText(path, "original");
            var before = File.ReadAllBytes(path);
            var r = await new ReplaceTool().ExecuteAsync(Ctx(argsJson, ws), CancellationToken.None);
            Assert.True(r.IsError);
            Assert.Equal(before, File.ReadAllBytes(path)); // malformed call never truncates
        }
        finally { Directory.Delete(ws, true); }
    }

    [Theory]
    [InlineData("""{"path":"e.txt","newText":"b"}""")]      // missing oldText
    [InlineData("""{"path":"e.txt","oldText":""}""")]       // empty needle
    [InlineData("""{"path":"","oldText":"a","newText":"b"}""")] // blank path
    public async Task Edit_InvalidArgsFailWithoutTouchingFile(string argsJson)
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "e.txt");
            File.WriteAllText(path, "original");
            var before = File.ReadAllBytes(path);
            var r = await new EditTool().ExecuteAsync(Ctx(argsJson, ws), CancellationToken.None);
            Assert.True(r.IsError);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task Write_MissingContentCannotOverwrite()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "w.txt");
            File.WriteAllText(path, "keep me");
            var before = File.ReadAllBytes(path);
            // content present but absent argument → must be a preflight error.
            var r = await new WriteTool().ExecuteAsync(Ctx("""{"path":"w.txt"}""", ws), CancellationToken.None);
            Assert.True(r.IsError);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    // ====================================================================
    // Exactness
    // ====================================================================

    [Fact]
    public async Task Matching_IsExact_WhitespaceCaseAndLoneCr()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "x.txt");
            File.WriteAllText(path, "Hello World\nTab\tHere\nLone\rCr", new UTF8Encoding(false));

            // space where a tab is → no match
            var e1 = await new EditTool().ExecuteAsync(Ctx("""{"path":"x.txt","oldText":"Tab Here","newText":"z"}""", ws), CancellationToken.None);
            Assert.True(e1.IsError);

            // case differs → no match
            var e2 = await new EditTool().ExecuteAsync(Ctx("""{"path":"x.txt","oldText":"hello world","newText":"z"}""", ws), CancellationToken.None);
            Assert.True(e2.IsError);

            // a real exact match works
            var e3 = await new EditTool().ExecuteAsync(Ctx("""{"path":"x.txt","oldText":"Hello World","newText":"HELLO WORLD"}""", ws), CancellationToken.None);
            Assert.False(e3.IsError, TextOf(e3));

            // a literal backslash-n in the search text stays literal (not a newline).
            File.WriteAllText(path, "a\\nb");
            var e4 = await new EditTool().ExecuteAsync(Ctx("""{"path":"x.txt","oldText":"a\\nb","newText":"Y"}""", ws), CancellationToken.None);
            Assert.False(e4.IsError, TextOf(e4));
            Assert.Equal("Y", File.ReadAllText(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task Unicode_SupplementaryPlaneSurvives()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "u.txt");
            var emoji = "\U0001F600"; // 😀 surrogate pair
            File.WriteAllText(path, $"pre {emoji} mid {emoji} post", new UTF8Encoding(false));
            var r = await new ReplaceTool().ExecuteAsync(
                CtxObj(new { path = "u.txt", oldText = emoji, newText = "!", expectedCount = 2 }, ws), CancellationToken.None);
            Assert.False(r.IsError, TextOf(r));
            Assert.Equal("pre ! mid ! post", File.ReadAllText(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    // ====================================================================
    // Encoding / BOM / line-ending preservation (byte-identical outside edits)
    // ====================================================================

    [Fact]
    public async Task Utf8Bom_Preserved()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "bom.txt");
            var body = "line1\nline2\nline3";
            File.WriteAllBytes(path, new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(body)).ToArray());
            var r = await new EditTool().ExecuteAsync(Ctx("""{"path":"bom.txt","oldText":"line2","newText":"LINE2"}""", ws), CancellationToken.None);
            Assert.False(r.IsError, TextOf(r));
            var after = File.ReadAllBytes(path);
            var expect = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("line1\nLINE2\nline3")).ToArray();
            Assert.Equal(expect, after); // BOM + surrounding bytes identical
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task Utf8NoBom_BytesOutsideReplacementsIdentical()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "nb.txt");
            File.WriteAllText(path, "AAA middle BBB", new UTF8Encoding(false));
            await new EditTool().ExecuteAsync(Ctx("""{"path":"nb.txt","oldText":"middle","newText":"MIDDLE"}""", ws), CancellationToken.None);
            Assert.Equal("AAA MIDDLE BBB", File.ReadAllText(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task Utf16LeBom_Preserved()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "u16.txt");
            File.WriteAllBytes(path, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("old new")).ToArray());
            var r = await new EditTool().ExecuteAsync(Ctx("""{"path":"u16.txt","oldText":"old","newText":"OLD"}""", ws), CancellationToken.None);
            Assert.False(r.IsError, TextOf(r));
            var expect = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("OLD new")).ToArray();
            Assert.Equal(expect, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task Utf32LeBom_Preserved()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "u32.txt");
            var bom = new byte[] { 0xFF, 0xFE, 0x00, 0x00 };
            var body = NetPI.Tools.TextFileEditor.U32FromChars("hi there", littleEndian: true);
            File.WriteAllBytes(path, bom.Concat(body).ToArray());
            var r = await new EditTool().ExecuteAsync(Ctx("""{"path":"u32.txt","oldText":"hi","newText":"HELLO"}""", ws), CancellationToken.None);
            Assert.False(r.IsError, TextOf(r));
            var after = File.ReadAllBytes(path);
            Assert.Equal(bom, after[..4]); // BOM intact
            Assert.Equal("HELLO there", NetPI.Tools.TextFileEditor.U32ToChars(after[4..], littleEndian: true));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task NoFinalNewline_StaysAbsent()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "nf.txt");
            File.WriteAllText(path, "no trailing newline", new UTF8Encoding(false));
            await new EditTool().ExecuteAsync(Ctx("""{"path":"nf.txt","oldText":"trailing","newText":"TAIL"}""", ws), CancellationToken.None);
            var raw = File.ReadAllBytes(path);
            Assert.NotEqual((byte)'\n', raw[^1]); // still no trailing newline
            Assert.Equal("no TAIL newline", File.ReadAllText(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task NoOp_AvoidsRewrite()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "noop.txt");
            File.WriteAllText(path, "stable");
            var before = File.ReadAllBytes(path);
            var r = await new EditTool().ExecuteAsync(Ctx("""{"path":"noop.txt","oldText":"stable","newText":"stable"}""", ws), CancellationToken.None);
            Assert.False(r.IsError, TextOf(r));
            Assert.Contains("no change", TextOf(r));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public async Task UnsupportedBinary_IsRefusedNotSubstituted()
    {
        var ws = MakeWorkspace();
        try
        {
            var path = F(ws, "bin.dat");
            File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, (byte)0xFF, 0x7F });
            var r = await new EditTool().ExecuteAsync(Ctx("""{"path":"bin.dat","oldText":"a","newText":"b"}""", ws), CancellationToken.None);
            Assert.True(r.IsError);
            Assert.Contains("Refusing", TextOf(r));
        }
        finally { Directory.Delete(ws, true); }
    }

    // ====================================================================
    // Registration / schema
    // ====================================================================

    [Fact]
    public void ReplaceTool_ExposesRequiredSchemaAndGuidelines()
    {
        var t = new ReplaceTool();
        Assert.Equal("replace", t.Name);
        var schema = t.Parameters;
        Assert.True(schema.TryGetProperty("required", out var req));
        var reqs = req.EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("path", reqs);
        Assert.Contains("oldText", reqs);
        Assert.Contains("newText", reqs);
        Assert.Contains("expectedCount", reqs);
        Assert.NotEmpty(t.Guidelines);
    }

    [Fact]
    public void FileTargetTools_DeclareTheirTargetPath()
    {
        var ws = MakeWorkspace();
        try
        {
            var abs = F(ws, "t.txt");
            var rt = new ReplaceTool();
            Assert.Equal(abs, rt.GetTargetPath(CtxObj(new { path = abs, oldText = "a", newText = "b", expectedCount = 1 }, ws)));
            // relative path resolves against the workspace
            var rel = rt.GetTargetPath(CtxObj(new { path = "t.txt", oldText = "a", newText = "b", expectedCount = 1 }, ws));
            Assert.Equal(abs, rel);
        }
        finally { Directory.Delete(ws, true); }
    }

    
}
