using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Tools.Files;

namespace NetPI.Tools.Tests;

public static class FileTests
{
    private static readonly ReadTool Read = new();
    private static readonly WriteTool Write = new();
    private static readonly EditTool Edit = new();

    public static void Register(TestRunner r)
    {
        // ------------------------------------------------ rpc metadata
        r.Add("rpc: the files read surfaces are read-only; files.open is not (it hands the path to the OS)", async () =>
        {
            var ctx = new FakePluginContext(T.TempDir("files-rpc"));
            await new FilesPlugin().StartAsync(ctx, CancellationToken.None);
            var flags = ctx.RpcFake.List().ToDictionary(m => m.Method, m => m.ReadOnly);
            foreach (var m in new[] { "files.list", "files.search", "files.git", "files.commits" })
                Check.True(flags.TryGetValue(m, out var ro) && ro, $"{m} only reads, so it is marked read-only");
            Check.True(flags.TryGetValue("files.open", out var open) && !open, "files.open opens the file or folder, so it stays unmarked");
        });

        // ------------------------------------------------ codec
        r.Add("codec: EOL detection (majority, tie, none)", () =>
        {
            Check.Equal<EolStyle?>(EolStyle.CrLf, TextCodec.DetectEol("a\r\nb\r\nc\n"));
            Check.Equal<EolStyle?>(EolStyle.Lf, TextCodec.DetectEol("a\nb\nc\r\n"));
            Check.Equal<EolStyle?>(EolStyle.Lf, TextCodec.DetectEol("a\r\nb\n"), "tie → LF");
            Check.Equal<EolStyle?>(null, TextCodec.DetectEol("no breaks"));
            var s = TextCodec.CountEol("a\r\nb\nc\rd");
            Check.Equal(1, s.CrLf); Check.Equal(1, s.Lf); Check.Equal(1, s.Cr); Check.True(s.Mixed);
            Check.Equal("a\nb\nc\nd", TextCodec.NormalizeToLf("a\r\nb\nc\rd"));
            Check.Equal("a\r\nb\r\n", TextCodec.ToEol("a\nb\r\n", EolStyle.CrLf));
        });

        r.Add("codec: BOM, UTF-16, Latin-1 round trip, binary sniffing", () =>
        {
            var bom = TextCodec.Decode([0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i', (byte)'\r', (byte)'\n']);
            Check.True(bom.Bom); Check.Equal("hi\n", bom.Text); Check.Equal<EolStyle?>(EolStyle.CrLf, bom.DetectedEol);
            var re = TextCodec.Encode(bom, EolStyle.Lf);
            Check.Equal("EF-BB-BF-68-69-0D-0A", BitConverter.ToString(re));

            byte[] utf16 = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("héllo\r\n")];
            Check.False(TextCodec.IsBinary(utf16), "UTF-16 with BOM is text");
            var d16 = TextCodec.Decode(utf16);
            Check.Equal("héllo\n", d16.Text);
            Check.Equal(BitConverter.ToString(utf16), BitConverter.ToString(TextCodec.Encode(d16, EolStyle.Lf)));

            byte[] latin = [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n']; // "café" in Windows-1252
            var dl = TextCodec.Decode(latin);
            Check.True(dl.Legacy);
            Check.Equal(BitConverter.ToString(latin), BitConverter.ToString(TextCodec.Encode(dl, EolStyle.Lf)));

            Check.True(TextCodec.IsBinary([1, 2, 0, 3]));
            Check.False(TextCodec.IsBinary(Encoding.UTF8.GetBytes("plain ✓ text")));
        });

        // ------------------------------------------------ edit: EOL handling
        r.Add("edit: CRLF file edited with LF oldText keeps CRLF", async () =>
        {
            var dir = T.TempDir("edit");
            var f = T.WriteText(dir, "a.cs", "class A\r\n{\r\n    int x = 1;\r\n    int y = 2;\r\n}\r\n");
            var res = await T.Run(Edit, dir, new { path = "a.cs", oldText = "    int x = 1;\n    int y = 2;", newText = "    int x = 10;\n    int y = 20;\n    int z = 30;" });
            Check.Ok(res);
            var raw = T.ReadRaw(f);
            Check.Equal("class A\r\n{\r\n    int x = 10;\r\n    int y = 20;\r\n    int z = 30;\r\n}\r\n", raw);
            var d = T.D(res);
            Check.Equal(3, d.Int("added")); Check.Equal(2, d.Int("removed")); Check.Equal("crlf", d.Str("eol"));
            Check.Contains(res.Content, "Applied 1 edit to a.cs (+3 −2)");
            Check.Contains(res.Content, "@@ -1,5 +1,6 @@");
        });

        r.Add("edit: LF file edited with CRLF oldText/newText stays LF", async () =>
        {
            var dir = T.TempDir("edit");
            var f = T.WriteText(dir, "a.txt", "one\ntwo\nthree\n");
            var res = await T.Run(Edit, dir, new { path = "a.txt", oldText = "one\r\ntwo", newText = "uno\r\ndos" });
            Check.Ok(res);
            Check.Equal("uno\ndos\nthree\n", T.ReadRaw(f));
        });

        r.Add("edit: mixed EOL file is written with the majority style", async () =>
        {
            var dir = T.TempDir("edit");
            var f = T.WriteText(dir, "m.txt", "a\r\nb\r\nc\nd\r\n");
            var res = await T.Run(Edit, dir, new { path = "m.txt", oldText = "c\nd", newText = "C\nD" });
            Check.Ok(res);
            Check.Equal("a\r\nb\r\nC\r\nD\r\n", T.ReadRaw(f));
            Check.Contains(res.Content, "mixed line endings");
        });

        r.Add("edit: UTF-8 BOM is preserved", async () =>
        {
            var dir = T.TempDir("edit");
            var f = T.WriteText(dir, "b.txt", "hello\r\nworld\r\n", bom: true);
            Check.Ok(await T.Run(Edit, dir, new { path = "b.txt", oldText = "world", newText = "there" }));
            var bytes = File.ReadAllBytes(f);
            Check.Equal("EF-BB-BF", BitConverter.ToString(bytes, 0, 3));
            Check.Equal("hello\r\nthere\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
            Check.True(T.D(await T.Run(Read, dir, new { path = "b.txt" })).Bool("bom"));
        });

        r.Add("edit: a non-UTF-8 (Latin-1) file is not re-encoded to UTF-8 by an edit", async () =>
        {
            var dir = T.TempDir("edit");
            // "café\nend\n" in Latin-1: the é is byte 0xE9, which is not valid UTF-8.
            var f = T.WriteText(dir, "latin.txt", "café\nend\n", Encoding.Latin1);
            Check.Equal("63-61-66-E9-0A-65-6E-64-0A", BitConverter.ToString(File.ReadAllBytes(f)));
            var before = File.ReadAllBytes(f);
            // An edit that introduces a character beyond U+00FF refuses, keeping every byte.
            var res = await T.Run(Edit, dir, new { path = "latin.txt", oldText = "end", newText = "end — done" });
            Check.Error(res, "re-encode");
            Check.Contains(res.Content, "U+2014");
            Check.Contains(res.Content, "write tool");
            Check.Equal(BitConverter.ToString(before), BitConverter.ToString(File.ReadAllBytes(f)), "the file keeps every byte");
            // An edit that stays within Latin-1 is applied with every unchanged byte intact.
            Check.Ok(await T.Run(Edit, dir, new { path = "latin.txt", oldText = "end", newText = "fin" }));
            Check.Equal("63-61-66-E9-0A-66-69-6E-0A", BitConverter.ToString(File.ReadAllBytes(f)));
        });

        // ------------------------------------------------ edit: semantics
        r.Add("edit: multi-edit applies sequentially", async () =>
        {
            var dir = T.TempDir("edit");
            var f = T.WriteText(dir, "s.txt", "alpha\nbeta\ngamma\n");
            var res = await T.Run(Edit, dir, new
            {
                path = "s.txt",
                edits = new object[]
                {
                    new { oldText = "alpha", newText = "ALPHA" },
                    new { oldText = "ALPHA\nbeta", newText = "ALPHA\nBETA" }, // depends on the first edit
                },
            });
            Check.Ok(res);
            Check.Equal("ALPHA\nBETA\ngamma\n", T.ReadRaw(f));
            Check.Equal(2, T.D(res).Int("edits"));
        });

        r.Add("edit: multi-edit is all-or-nothing", async () =>
        {
            var dir = T.TempDir("edit");
            var original = "alpha\nbeta\ngamma\n";
            var f = T.WriteText(dir, "s.txt", original);
            var res = await T.Run(Edit, dir, new
            {
                path = "s.txt",
                edits = new object[] { new { oldText = "alpha", newText = "ALPHA" }, new { oldText = "delta", newText = "DELTA" } },
            });
            Check.Error(res, "Edit 2 of 2 failed");
            Check.Contains(res.Content, "not changed");
            Check.Equal(original, T.ReadRaw(f));
        });

        r.Add("edit: ambiguous match reports count and line numbers; replaceAll replaces all", async () =>
        {
            var dir = T.TempDir("edit");
            var f = T.WriteText(dir, "d.txt", "x = 1\nfoo()\ny = 2\nz = 3\nfoo()\n");
            var res = await T.Run(Edit, dir, new { path = "d.txt", oldText = "foo()", newText = "bar()" });
            Check.Error(res, "matches 2 locations");
            Check.Contains(res.Content, "lines 2, 5");
            Check.Contains(res.Content, "replaceAll");
            res = await T.Run(Edit, dir, new { path = "d.txt", oldText = "foo()", newText = "bar()", replaceAll = true });
            Check.Ok(res);
            Check.Equal("x = 1\nbar()\ny = 2\nz = 3\nbar()\n", T.ReadRaw(f));
            Check.Contains(res.Content, "replaced 2 occurrences");
        });

        r.Add("edit: aliases old_string/new_string, replace_all as string, edits as JSON string", async () =>
        {
            var dir = T.TempDir("edit");
            var f = T.WriteText(dir, "a.txt", "a a a\n");
            Check.Ok(await T.Run(Edit, dir, new { file_path = "a.txt", old_string = "a", new_string = "b", replace_all = "true" }));
            Check.Equal("b b b\n", T.ReadRaw(f));
            Check.Ok(await T.Run(Edit, dir, "{\"filePath\":\"a.txt\",\"edits\":\"[{\\\"oldText\\\":\\\"b b b\\\",\\\"newText\\\":\\\"c\\\"}]\"}"));
            Check.Equal("c\n", T.ReadRaw(f));
        });

        r.Add("edit: fuzzy (a) trailing whitespace", async () =>
        {
            var dir = T.TempDir("edit");
            var f = T.WriteText(dir, "t.py", "def f():   \n    return 1  \n\nprint(f())\n");
            var res = await T.Run(Edit, dir, new { path = "t.py", oldText = "def f():\n    return 1", newText = "def f():\n    return 2" });
            Check.Ok(res);
            Check.Equal("def f():\n    return 2\n\nprint(f())\n", T.ReadRaw(f));
            var fz = T.D(res).GetProperty("fuzzy")[0];
            Check.Equal("trailing-whitespace", fz.Str("strategy"));
            Check.Equal(1, fz.Int("line"));
        });

        r.Add("edit: fuzzy (b) unicode quotes/dashes/NBSP", async () =>
        {
            var dir = T.TempDir("edit");
            var f = T.WriteText(dir, "u.md", "Title\nHe said “hello” — it’s fine.\nEnd\n");
            var res = await T.Run(Edit, dir, new { path = "u.md", oldText = "He said \"hello\" - it's fine.", newText = "He said goodbye." });
            Check.Ok(res);
            Check.Equal("Title\nHe said goodbye.\nEnd\n", T.ReadRaw(f));
            Check.Equal("unicode", T.D(res).GetProperty("fuzzy")[0].Str("strategy"));
        });

        r.Add("edit: fuzzy (c) indentation re-indents newText", async () =>
        {
            var dir = T.TempDir("edit");
            var f = T.WriteText(dir, "i.cs", "class C\n{\n        void M()\n        {\n            Run();\n        }\n}\n");
            // Model dropped the base indentation.
            var res = await T.Run(Edit, dir, new { path = "i.cs", oldText = "void M()\n{\n    Run();\n}", newText = "void M()\n{\n    Run();\n    Stop();\n}" });
            Check.Ok(res);
            Check.Equal("class C\n{\n        void M()\n        {\n            Run();\n            Stop();\n        }\n}\n", T.ReadRaw(f));
            Check.Equal("indentation", T.D(res).GetProperty("fuzzy")[0].Str("strategy"));

            // Tabs in the file, spaces in oldText.
            var g = T.WriteText(dir, "tab.go", "func main() {\n\tif ok {\n\t\tgo()\n\t}\n}\n");
            res = await T.Run(Edit, dir, new { path = "tab.go", oldText = "    if ok {\n        go()\n    }", newText = "    if ok {\n        go()\n        done()\n    }" });
            Check.Ok(res);
            Check.Equal("func main() {\n\tif ok {\n\t\tgo()\n\t\tdone()\n\t}\n}\n", T.ReadRaw(g));

            // Unit-level: a new, deeper level converts spaces to the file's tabs.
            var re = EditMatcher.Reindent(["if x {", "    if y {", "        z()", "    }", "}"], ["if x {", "}"], ["\tif x {", "\t}"]);
            Check.Equal("\tif x {|\t\tif y {|\t\t\tz()|\t\t}|\t}", string.Join("|", re));
        });

        r.Add("edit: errors (empty oldText, identical, not found with hint, missing file, binary)", async () =>
        {
            var dir = T.TempDir("edit");
            T.WriteText(dir, "e.txt", "public void Start()\n{\n}\n");
            Check.Error(await T.Run(Edit, dir, new { path = "e.txt", oldText = "", newText = "x" }), "oldText is empty");
            Check.Error(await T.Run(Edit, dir, new { path = "e.txt", oldText = "{", newText = "{" }), "identical");
            var nf = await T.Run(Edit, dir, new { path = "e.txt", oldText = "public void Stop()", newText = "x" });
            Check.Error(nf, "not found");
            Check.Contains(nf.Content, "most similar line is 1: `public void Start()`");
            Check.Error(await T.Run(Edit, dir, new { path = "missing.txt", oldText = "a", newText = "b" }), "write tool");
            T.WriteBytes(dir, "bin.dat", [1, 2, 0, 4]);
            Check.Error(await T.Run(Edit, dir, new { path = "bin.dat", oldText = "a", newText = "b" }), "binary");
            Check.Error(await T.Run(Edit, dir, new { path = "e.txt" }), "Missing edits");
        });

        r.Add("edit: empty file + empty oldText acts as write", async () =>
        {
            var dir = T.TempDir("edit");
            var f = T.WriteText(dir, "empty.txt", "");
            Check.Ok(await T.Run(Edit, dir, new { path = "empty.txt", oldText = "", newText = "first line\n" }));
            Check.Equal("first line\n", T.ReadRaw(f));
        });

        r.Add("edit: diff has line numbers and 3 lines of context", async () =>
        {
            var dir = T.TempDir("edit");
            var lines = Enumerable.Range(1, 20).Select(i => $"line {i}").ToList();
            T.WriteText(dir, "n.txt", string.Join("\n", lines) + "\n");
            var res = await T.Run(Edit, dir, new { path = "n.txt", oldText = "line 10\n", newText = "line ten\n" });
            Check.Ok(res);
            var diff = T.D(res).Str("diff");
            Check.Contains(diff, "--- a/n.txt\n+++ b/n.txt\n@@ -7,7 +7,7 @@\n line 7\n line 8\n line 9\n-line 10\n+line ten\n line 11\n line 12\n line 13\n");
            Check.Equal(10, T.D(res).Int("firstChangedLine"));
        });

        // ------------------------------------------------ write
        r.Add("write: new file uses files.newFileEol (lf default, crlf, auto) and creates parents", async () =>
        {
            var dir = T.TempDir("write");
            var res = await T.Run(Write, dir, new { path = "sub/deep/new.txt", content = "a\r\nb\n" });
            Check.Ok(res);
            Check.Equal("a\nb\n", T.ReadRaw(Path.Combine(dir, "sub/deep/new.txt")));
            var d = T.D(res);
            Check.True(d.Bool("created")); Check.Equal(2, d.Int("lines")); Check.Equal(4, d.Int("bytes")); Check.False(d.Has("diff"));

            var settings = new FakeSettings();
            settings.Set("files.newFileEol", "crlf");
            Check.Ok(await T.Run(new WriteTool(settings), dir, new { path = "crlf.txt", content = "a\nb\n" }));
            Check.Equal("a\r\nb\r\n", T.ReadRaw(Path.Combine(dir, "crlf.txt")));

            settings.Set("files.newFileEol", "auto");
            Check.Ok(await T.Run(new WriteTool(settings), dir, new { path = "auto.txt", content = "a\nb\n" }));
            Check.Equal(OperatingSystem.IsWindows() ? "a\r\nb\r\n" : "a\nb\n", T.ReadRaw(Path.Combine(dir, "auto.txt")));
        });

        r.Add("write: existing CRLF+BOM file keeps style; diff vs old content", async () =>
        {
            var dir = T.TempDir("write");
            var f = T.WriteText(dir, "x.txt", "one\r\ntwo\r\n", bom: true);
            var res = await T.Run(Write, dir, new { path = "x.txt", content = "one\n2\nthree\n" });
            Check.Ok(res);
            var bytes = File.ReadAllBytes(f);
            Check.Equal("EF-BB-BF", BitConverter.ToString(bytes, 0, 3));
            Check.Equal("one\r\n2\r\nthree\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
            var d = T.D(res);
            Check.False(d.Bool("created"));
            Check.Contains(d.Str("diff"), "-two\n+2\n+three");
            Check.Equal(2, d.Int("added")); Check.Equal(1, d.Int("removed"));
            // Same content again: no change
            Check.Contains((await T.Run(Write, dir, new { path = "x.txt", content = "one\n2\nthree\n" })).Content, "No changes");
        });

        r.Add("write: atomic (no temp files), keeps unix mode, missing args", async () =>
        {
            var dir = T.TempDir("write");
            var f = T.WriteText(dir, "run.sh", "#!/bin/sh\necho hi\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f, (UnixFileMode)Convert.ToInt32("755", 8));
            Check.Ok(await T.Run(Write, dir, new { path = "run.sh", content = "#!/bin/sh\necho bye\n" }));
            Check.Equal(1, Directory.GetFiles(dir).Length, "no temp files left");
            if (!OperatingSystem.IsWindows()) Check.True((File.GetUnixFileMode(f) & UnixFileMode.UserExecute) != 0, "executable bit kept");
            Check.Error(await T.Run(Write, dir, new { path = "x.txt" }), "content");
            Check.Error(await T.Run(Write, dir, new { content = "x" }), "path");
            Check.Error(await T.Run(Write, dir, new { path = ".", content = "x" }), "directory");
        });

        // ------------------------------------------------ read
        r.Add("read: truncation footer, offset/limit, negative offset, past end", async () =>
        {
            var dir = T.TempDir("read");
            T.WriteText(dir, "big.txt", string.Join("\r\n", Enumerable.Range(1, 5230).Select(i => $"row {i}")) + "\r\n");
            var res = await T.Run(Read, dir, new { path = "big.txt" });
            Check.Ok(res);
            Check.Contains(res.Content, "[Showing lines 1-2000 of 5230. Use offset=2001 to continue.]");
            Check.NotContains(res.Content, "\r");
            Check.True(res.Content.StartsWith("row 1\nrow 2\n"), "no line number prefixes");
            var d = T.D(res);
            Check.Equal(1, d.Int("startLine")); Check.Equal(2000, d.Int("endLine")); Check.Equal(5230, d.Int("totalLines"));
            Check.True(d.Bool("truncated")); Check.Equal("crlf", d.Str("eol"));

            res = await T.Run(Read, dir, new { path = "big.txt", offset = "5229", limit = 10 });
            Check.Equal("row 5229\nrow 5230", res.Content);
            Check.False(T.D(res).Bool("truncated"));

            res = await T.Run(Read, dir, new { path = "big.txt", offset = 10, limit = 2 });
            Check.Contains(res.Content, "row 10\nrow 11\n\n[Showing lines 10-11 of 5230. Use offset=12 to continue.]");

            res = await T.Run(Read, dir, new { path = "big.txt", offset = -2 });
            Check.Equal("row 5229\nrow 5230", res.Content);

            Check.Error(await T.Run(Read, dir, new { path = "big.txt", offset = 6000 }), "past the end");
        });

        r.Add("read: a huge file is paged without reading it to the end", async () =>
        {
            var dir = T.TempDir("read-stream");
            // The streaming path with a production-sized file is 32 MiB; the threshold is a field so this test can put a
            // small file on it and assert what it does.
            var was = ReadTool.StreamingThresholdBytes;
            ReadTool.StreamingThresholdBytes = 1024;
            try
            {
                var all = Enumerable.Range(1, 400).Select(i => $"row {i}").ToArray();
                T.WriteText(dir, "huge.txt", string.Join("\n", all) + "\n");

                // A page in the middle: the count is not known (it would take reading the rest of the file), and the
                // note says more lines follow because one extra line proved it.
                var res = await T.Run(Read, dir, new { path = "huge.txt", offset = 100, limit = 5 });
                Check.Ok(res);
                Check.Equal("row 100\nrow 101\nrow 102\nrow 103\nrow 104", res.Content.Split("\n\n[Showing")[0]);
                Check.Contains(res.Content, "[Showing lines 100-104; more lines follow. Use offset=105 to continue.]");
                var d = T.D(res);
                Check.Equal(100, d.Int("startLine"));
                Check.Equal(104, d.Int("endLine"));
                Check.True(d.Bool("truncated"));
                Check.True(!d.TryGetProperty("totalLines", out var tl) || tl.ValueKind == JsonValueKind.Null,
                    "no line count: the file was not read to the end");

                // The last page reaches the end of the file, so it knows the exact total (and says nothing about
                // continuing, because there is nothing to continue to).
                res = await T.Run(Read, dir, new { path = "huge.txt", offset = 398, limit = 5 });
                Check.Equal("row 398\nrow 399\nrow 400", res.Content);
                Check.Equal(400, T.D(res).Int("totalLines"));
                Check.False(T.D(res).Bool("truncated"), "and nothing is truncated");

                // A negative offset within one page: one pass, the exact count, the last lines.
                res = await T.Run(Read, dir, new { path = "huge.txt", offset = -3 });
                Check.Equal("row 398\nrow 399\nrow 400", res.Content);
                Check.Equal(400, T.D(res).Int("totalLines"));
                res = await T.Run(Read, dir, new { path = "huge.txt", offset = -2, limit = 1 });
                Check.Equal("row 399", res.Content.Split("\n\n[Showing")[0], "fewer lines than asked for: the page starts at the line the offset names");
                Check.Contains(res.Content, "[Showing lines 399-399 of 400. Use offset=400 to continue.]");
                Check.Equal(400, T.D(res).Int("totalLines"));
                Check.True(T.D(res).Bool("truncated"), "and there is more after it");
                // More lines asked for than a page holds: the window ends before the last line, so the count comes first
                // and the page is read after it.
                res = await T.Run(Read, dir, new { path = "huge.txt", offset = -10, limit = 3 });
                Check.Equal("row 391\nrow 392\nrow 393", res.Content.Split("\n\n[Showing")[0]);
                Check.Contains(res.Content, "[Showing lines 391-393 of 400. Use offset=394 to continue.]");
                res = await T.Run(Read, dir, new { path = "huge.txt", offset = -1000 });
                Check.Contains(res.Content, "row 1\nrow 2\n", "a negative offset past the start starts at line 1");

                // Past the end is still an error, with the count the pass found. A negative offset past the start is not
                // an error and never was: it clamps to line 1.
                Check.Error(await T.Run(Read, dir, new { path = "huge.txt", offset = 500 }), "past the end");
            }
            finally { ReadTool.StreamingThresholdBytes = was; }
        });

        r.Add("read: a huge file is decoded with its own encoding, not assumed to be UTF-8", async () =>
        {
            var dir = T.TempDir("read-encoding");
            var was = ReadTool.StreamingThresholdBytes;
            ReadTool.StreamingThresholdBytes = 1024;
            try
            {
                var lines = Enumerable.Range(1, 200).Select(i => $"row {i} café").ToArray();
                // Latin-1 (not valid UTF-8) and UTF-16 LE with a BOM, both over the threshold.
                T.WriteText(dir, "latin.txt", string.Join("\n", lines) + "\n", Encoding.Latin1);
                var utf16 = new UnicodeEncoding(false, true);
                T.WriteBytes(dir, "utf16.txt", [.. utf16.GetPreamble(), .. utf16.GetBytes(string.Join("\n", lines) + "\n")]);
                foreach (var name in new[] { "latin.txt", "utf16.txt" })
                {
                    var res = await T.Run(Read, dir, new { path = name, offset = 1, limit = 2 });
                    Check.Equal("row 1 café\nrow 2 café", res.Content.Split("\n\n[Showing")[0], name);
                    var d = T.D(res);
                    Check.True(d.Bool("truncated"), name);
                    if (name == "latin.txt") Check.Equal("latin1", d.Str("encoding"), "a Latin-1 file says so, as the small path does");
                    else Check.True(d.Bool("bom"), "the UTF-16 BOM is reported");
                    // The last line is reachable by a negative offset, so the whole file decodes, not just the sample.
                    res = await T.Run(Read, dir, new { path = name, offset = -1 });
                    Check.Equal("row 200 café", res.Content, name);
                }
            }
            finally { ReadTool.StreamingThresholdBytes = was; }
        });

        r.Add("read: pages fit the tool result limit (at most 50KB), huge single line, empty file", async () =>
        {
            var dir = T.TempDir("read");
            T.WriteText(dir, "wide.txt", string.Join("\n", Enumerable.Range(1, 300).Select(i => new string('x', 500))) + "\n");
            // the default tool result limit (20000 chars): a page ends with the offset to continue instead of being cut
            var res = await T.Run(Read, dir, new { path = "wide.txt" });
            var d = T.D(res);
            Check.True(d.Int("endLine") < 45 && d.Int("endLine") >= 30, $"page fits the limit (endLine={d.Int("endLine")})");
            Check.Contains(res.Content, $"Use offset={d.Int("endLine") + 1} to continue");
            Check.True(res.Content.Length <= ToolResultLimit.Default, $"{res.Content.Length} chars");
            // a higher limit: pages of up to 50KB
            var roomy = new FakeSettings();
            roomy.Set(ToolResultLimit.Setting, 200_000);
            res = await T.Run(new ReadTool(roomy), dir, new { path = "wide.txt" });
            d = T.D(res);
            Check.True(d.Int("endLine") < 300 && d.Int("endLine") >= 90, $"byte cap applied (endLine={d.Int("endLine")})");
            Check.True(Encoding.UTF8.GetByteCount(res.Content) < ReadTool.MaxBytes + 200);

            T.WriteText(dir, "min.js", new string('y', 200_000));
            res = await T.Run(Read, dir, new { path = "min.js" });
            Check.Contains(res.Content, "[Line 1 is");
            Check.True(res.Content.Length < 60_000);

            T.WriteText(dir, "empty.txt", "");
            Check.Equal("(empty file)", (await T.Run(Read, dir, new { path = "empty.txt" })).Content);
        });

        // Windows regression: a /tmp/… path copied from Git Bash output resolved to C:\tmp\… in the file tools.
        r.Add("paths: relative, ~, and Git Bash's /c/… and /tmp/… on Windows", () =>
        {
            var cwd = T.TempDir("paths");
            var ctx = T.Ctx(cwd);
            Check.Equal(cwd, ctx.ResolvePath(""));
            Check.Equal(Path.Combine(cwd, "a", "b.txt"), ctx.ResolvePath("a/b.txt"));
            Check.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "x.txt"), ctx.ResolvePath("~/x.txt"));
            if (!OperatingSystem.IsWindows())
            {
                Check.Equal("/tmp/x.txt", ctx.ResolvePath("/tmp/x.txt"));
                return;
            }
            Check.Equal(@"C:\Users\me\x.txt", ctx.ResolvePath("/c/Users/me/x.txt"));
            Check.Equal(@"D:\", ctx.ResolvePath("/d"));
            Check.Equal(@"C:\", ctx.ResolvePath("/c/"));
            var temp = Path.GetTempPath();
            Check.Equal(Path.Combine(temp, "netpi", "x.txt"), ctx.ResolvePath("/tmp/netpi/x.txt"));
            Check.Equal(Path.GetFullPath(temp), ctx.ResolvePath("/tmp"));
            Check.Equal(Path.GetFullPath(Path.Combine(cwd, "tmpfile")), ctx.ResolvePath("tmpfile"), "only the /tmp mount, not names starting with tmp");
            Check.Equal(@"C:\tmpdir\x", ctx.ResolvePath("C:/tmpdir/x"));
        });

        r.Add("read: directory, missing file suggestion, binary, images", async () =>
        {
            var dir = T.TempDir("read");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            Check.Error(await T.Run(Read, dir, new { path = "sub" }), "ls");
            T.WriteText(dir, "Program.cs", "x");
            Check.Error(await T.Run(Read, dir, new { path = "Progam.cs" }), "Did you mean");
            T.WriteBytes(dir, "blob.bin", [0x7F, 0x45, 0x4C, 0x46, 0, 0, 1]);
            Check.Error(await T.Run(Read, dir, new { path = "blob.bin" }), "binary");

            byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
            T.WriteBytes(dir, "pic.png", png);
            var vision = new ModelInfo { Provider = "p", Id = "m", InputModalities = ["text", "image"] };
            var res = await T.Run(Read, dir, new { path = "pic.png" }, vision);
            Check.Ok(res);
            Check.Equal(1, res.Images!.Count);
            Check.Equal("image/png", res.Images[0].MediaType);
            Check.Equal(Convert.ToBase64String(png), res.Images[0].Data);
            res = await T.Run(Read, dir, new { path = "pic.png" }, new ModelInfo { Provider = "p", Id = "t" });
            Check.Ok(res);
            Check.True(res.Images is null);
            Check.Contains(res.Content, "cannot view images");
        });

        // ------------------------------------------------ diff
        r.Add("diff: Myers edit script reconstructs both sides (randomized)", () =>
        {
            var rnd = new Random(42);
            for (var iter = 0; iter < 300; iter++)
            {
                var a = Enumerable.Range(0, rnd.Next(0, 30)).Select(_ => ((char)('a' + rnd.Next(5))).ToString()).ToList();
                var b = Enumerable.Range(0, rnd.Next(0, 30)).Select(_ => ((char)('a' + rnd.Next(5))).ToString()).ToList();
                var at = string.Join("\n", a) + (a.Count > 0 && rnd.Next(2) == 0 ? "\n" : "");
                var bt = string.Join("\n", b) + (b.Count > 0 && rnd.Next(2) == 0 ? "\n" : "");
                var script = LineDiff.Compute(at, bt);
                var oldSide = script.Where(l => l.Op != DiffOp.Insert).Select(l => l.Text).ToList();
                var newSide = script.Where(l => l.Op != DiffOp.Delete).Select(l => l.Text).ToList();
                Check.Equal(string.Join("|", TextCodec.SplitLines(at)), string.Join("|", oldSide), $"old side (iter {iter})");
                Check.Equal(string.Join("|", TextCodec.SplitLines(bt)), string.Join("|", newSide), $"new side (iter {iter})");
            }
            var u = LineDiff.Unified("a\nb", "a\nb\n", "f");
            Check.Contains(u.Text, "-b\n\\ No newline at end of file\n+b\n");
            Check.Equal("", LineDiff.Unified("same\n", "same\n").Text);
        });

        r.Add("diff: large files stay fast", () =>
        {
            var a = string.Join("\n", Enumerable.Range(0, 20000).Select(i => $"line {i}"));
            var b = string.Join("\n", Enumerable.Range(0, 20000).Select(i => $"LINE {i}"));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var d = LineDiff.Unified(a, b, "big", maxLines: 100);
            Check.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds}ms");
            Check.Equal(20000, d.Added); Check.Equal(20000, d.Removed); Check.True(d.Truncated);
        });

        // ------------------------------------------------ glob / ignore
        r.Add("glob: *, **, ?, {a,b}, [..], name-only, negation, SplitBase", () =>
        {
            Check.True(new Glob("*.cs").IsMatch("src/deep/File.cs"));
            Check.False(new Glob("*.cs").IsMatch("src/File.csx"));
            Check.True(new Glob("**/*.cs").IsMatch("File.cs"));
            Check.True(new Glob("**/*.cs").IsMatch("a/b/File.cs"));
            Check.True(new Glob("src/**/test_?.py").IsMatch("src/test_1.py"));
            Check.True(new Glob("src/**/test_?.py").IsMatch("src/a/b/test_x.py"));
            Check.False(new Glob("src/**/test_?.py").IsMatch("lib/test_1.py"));
            Check.False(new Glob("src/*.py").IsMatch("src/a/x.py"));
            Check.True(new Glob("*.{ts,tsx}").IsMatch("ui/App.tsx"));
            Check.True(new Glob("{src,lib}/**/*.{js,mjs}").IsMatch("lib/x/y.mjs"));
            Check.True(new Glob("file[0-9].txt").IsMatch("file7.txt"));
            Check.False(new Glob("file[!0-9].txt").IsMatch("file7.txt"));
            Check.True(new Glob("!*.log").Negated);
            Check.Equal(("src/a", "**/*.cs"), Glob.SplitBase("src/a/**/*.cs"));
            Check.Equal(("", "*.cs"), Glob.SplitBase("*.cs"));
            var filter = new GlobFilter(["*.cs *.md", "!**/obj/**"]);
            Check.True(filter.IsMatch("a/b.cs"));
            Check.True(filter.IsMatch("README.md"));
            Check.False(filter.IsMatch("x/obj/y.cs"));
            Check.False(filter.IsMatch("x.txt"));
        });

        r.Add("ignore: .gitignore rules (anchored, dir-only, negation, nested) + built-in skips", () =>
        {
            var dir = MakeTree();
            var files = FileWalker.Walk(dir, new WalkOptions { IncludeDirs = false }).Select(e => e.RelPath).ToList();
            var joined = string.Join(",", files);
            Check.True(files.Contains("src/app.cs"), joined);
            Check.True(files.Contains("src/keep.log"), "negated rule re-includes: " + joined);
            Check.False(files.Contains("debug.log"), joined);
            Check.False(files.Contains("root-only.txt"), "anchored rule: " + joined);
            Check.True(files.Contains("src/root-only.txt"), "anchored rule applies to root only: " + joined);
            Check.False(files.Any(f => f.StartsWith("out/")), "dir-only rule: " + joined);
            Check.True(files.Contains("docs/out"), "dir-only rule does not match files: " + joined);
            Check.False(files.Contains("src/gen/x.g.cs"), "nested .gitignore: " + joined);
            Check.False(files.Any(f => f.StartsWith("node_modules/") || f.StartsWith("bin/") || f.Contains("/obj/")), joined);
            Check.True(files.Contains(".github/ci.yml"), "hidden files are searched: " + joined);
        });

        // ------------------------------------------------ grep
        r.Add("grep: content mode format, ignore rules, CRLF `$`, glob", async () =>
        {
            var dir = MakeTree();
            var grep = new GrepTool();
            var res = await T.Run(grep, dir, new { pattern = "TODO" });
            Check.Ok(res);
            Check.Contains(res.Content, "src/app.cs:3:     // TODO: fix");
            Check.Contains(res.Content, "src/crlf.txt:2: TODO crlf");
            Check.NotContains(res.Content, "node_modules");
            Check.NotContains(res.Content, "debug.log");
            Check.NotContains(res.Content, "\r");

            res = await T.Run(grep, dir, new { pattern = "crlf$" });
            Check.Contains(res.Content, "src/crlf.txt:2: TODO crlf");

            res = await T.Run(grep, dir, new { pattern = "todo", ignoreCase = true, glob = "*.cs" });
            Check.Contains(res.Content, "src/app.cs:3:");
            Check.NotContains(res.Content, "crlf.txt");
            var d = T.D(res);
            Check.Equal(1, d.Int("matches")); Check.Equal(1, d.Int("files"));

            res = await T.Run(grep, dir, new { pattern = "nothing-here-xyz" });
            Check.Ok(res);
            Check.Contains(res.Content, "No matches");
        });

        r.Add("grep: files/count modes, context groups, literal, invalid regex, maxResults", async () =>
        {
            var dir = T.TempDir("grep");
            T.WriteText(dir, "a.txt", string.Join("\n", Enumerable.Range(1, 30).Select(i => i is 5 or 7 or 25 ? $"hit {i}" : $"line {i}")) + "\n");
            T.WriteText(dir, "b.txt", "hit (x)\n");
            var grep = new GrepTool();
            var res = await T.Run(grep, dir, new { pattern = "hit", outputMode = "files" });
            Check.Equal("a.txt\nb.txt", res.Content);
            res = await T.Run(grep, dir, new { pattern = "hit", output_mode = "count" });
            Check.Equal("a.txt: 3\nb.txt: 1", res.Content);

            res = await T.Run(grep, dir, new { pattern = "hit", path = "a.txt", context = 1 });
            Check.Equal("a.txt-4- line 4\na.txt:5: hit 5\na.txt-6- line 6\na.txt:7: hit 7\na.txt-8- line 8\n--\na.txt-24- line 24\na.txt:25: hit 25\na.txt-26- line 26", res.Content);

            res = await T.Run(grep, dir, new { pattern = "(x)", literal = true });
            Check.Equal("b.txt:1: hit (x)", res.Content);
            res = await T.Run(grep, dir, new { pattern = "hit (x" });
            Check.Contains(res.Content, "searched for it literally");
            Check.Contains(res.Content, "b.txt:1: hit (x)");

            res = await T.Run(grep, dir, new { pattern = "line", maxResults = 5 });
            Check.Contains(res.Content, "Results truncated at 5 matches");
            Check.Equal(5, res.Content.Split('\n').Count(l => l.StartsWith("a.txt:")));
            Check.True(T.D(res).Bool("truncated"));
        });

        r.Add("grep: multiline, binary skipped, single file path, path as glob", async () =>
        {
            var dir = T.TempDir("grep");
            T.WriteText(dir, "m.cs", "void A()\r\n{\r\n    return;\r\n}\r\n");
            T.WriteBytes(dir, "x.bin", [(byte)'v', (byte)'o', (byte)'i', (byte)'d', 0, 1]);
            var grep = new GrepTool();
            var res = await T.Run(grep, dir, new { pattern = @"A\(\)\n\{", multiline = true });
            Check.Equal("m.cs:1: void A()\nm.cs:2: {", res.Content);
            res = await T.Run(grep, dir, new { pattern = "void" });
            Check.Equal("m.cs:1: void A()", res.Content);
            res = await T.Run(grep, dir, new { pattern = "zzz-nothing" });
            Check.Contains(res.Content, "1 binary file skipped");
            res = await T.Run(grep, dir, new { pattern = "return", path = Path.Combine(dir, "m.cs") });
            Check.Equal("m.cs:3:     return;", res.Content);
            res = await T.Run(grep, dir, new { pattern = "return", path = "**/*.cs" });
            Check.Equal("m.cs:3:     return;", res.Content);
        });

        // ------------------------------------------------ find / ls
        r.Add("find: globs, dirs suffixed, ignore rules, maxResults", async () =>
        {
            var dir = MakeTree();
            var find = new FindTool();
            var res = await T.Run(find, dir, new { pattern = "**/*.cs" });
            Check.Equal("src/app.cs", res.Content);
            res = await T.Run(find, dir, new { pattern = "*.{txt,yml}" });
            Check.Equal(".github/ci.yml\nsrc/crlf.txt\nsrc/root-only.txt", res.Content);
            res = await T.Run(find, dir, new { pattern = "src" });
            Check.Equal("src/", res.Content);
            res = await T.Run(find, dir, new { pattern = "src/*" });
            Check.Contains(res.Content, "src/app.cs");
            res = await T.Run(find, dir, new { pattern = "*", maxResults = 2 });
            Check.Contains(res.Content, "Showing the first 2 results");
            res = await T.Run(find, dir, new { pattern = Path.Combine(dir, "src").Replace('\\', '/') + "/*.cs" });
            Check.Equal("src/app.cs", res.Content);
            res = await T.Run(find, dir, new { pattern = "*.zzz" });
            Check.Contains(res.Content, "No files matching");
        });

        r.Add("ls: dirs first, sizes, hidden ignored entries, all=true", async () =>
        {
            var dir = MakeTree();
            var ls = new LsTool();
            var res = await T.Run(ls, dir, new { });
            Check.Ok(res);
            var lines = res.Content.Split('\n');
            Check.Equal(".github/", lines[0]);
            Check.Contains(res.Content, "src/");
            Check.NotContains(res.Content, "node_modules");
            Check.Contains(res.Content, ".gitignore");
            Check.Contains(res.Content, "ignored entries hidden");
            var d = T.D(res);
            Check.True(d.Int("hidden") >= 4, $"hidden={d.Int("hidden")}");
            res = await T.Run(ls, dir, new { all = true });
            Check.Contains(res.Content, "node_modules/");
            Check.Contains(res.Content, "debug.log");
            Check.True(new LsTool().Definition.ReadOnly);
        });

        r.Add("ls / files.list: a big directory is only materialised to the cap, and the rest is counted", async () =>
        {
            var dir = T.TempDir("bigdir");
            for (var i = 0; i < 1200; i++) File.WriteAllText(Path.Combine(dir, $"f{i:D4}.txt"), "x\n");

            // a capped listing materialises only the cap; the directory's totals come from a streamed pass
            var (entries, visible, ignored) = FileWalker.ListDirectory(dir, maxEntries: 100);
            Check.Equal(100, entries.Count, "only the cap is materialised");
            Check.Equal(1200, visible, "the total is counted, not the materialised subset");
            Check.Equal(0, ignored);
            var names = entries.Select(e => e.Name).ToList();
            Check.True(names.SequenceEqual(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)), "the materialised part stays sorted");

            // an uncapped listing is unchanged: the whole directory
            var (full, fv, fi) = FileWalker.ListDirectory(dir);
            Check.Equal(1200, full.Count);
            Check.Equal(1200, fv);

            // ls: shows the cap, names the total, flags truncation — without materialising the rest
            var res = await T.Run(new LsTool(), dir, new { });
            Check.Ok(res);
            Check.Contains(res.Content, "[Showing 1000 of 1200 entries", res.Content);
            var d = T.D(res);
            Check.Equal(1200, d.Int("entries"), "entries is the total, not the materialised count");
            Check.True(d.Bool("truncated"));

            // files.list: the same bound at the RPC level, reported in the payload
            var ctx = new FakePluginContext(dir);
            await new FilesPlugin().StartAsync(ctx, CancellationToken.None);
            var old = FileIndex.MaxListedEntries;
            FileIndex.MaxListedEntries = 500;
            try
            {
                var list = (FileIndex.ListResult)await ctx.RpcFake.InvokeAsync("files.list", new { })!;
                Check.Equal(500, list.Entries.Count, "the listing stops at the cap");
                Check.True(list.Truncated);
                Check.Equal(1200, list.Total);
            }
            finally { FileIndex.MaxListedEntries = old; }
        });

        // ------------------------------------------------ RPC
        r.Add("rpc: files.search fuzzy ranking and files.list", async () =>
        {
            var dir = MakeTree();
            var ctx = new FakePluginContext(dir);
            var plugin = new FilesPlugin();
            await plugin.StartAsync(ctx, CancellationToken.None);
            Check.Equal("read,write,edit,grep,find,ls", string.Join(",", ctx.ToolsFake.Tools.Select(t => t.Definition.Name)));

            var hits = (List<FileIndex.SearchHit>)(await ctx.RpcFake.InvokeAsync("files.search", new { query = "app" }))!;
            Check.Equal("src/app.cs", hits[0].Rel);
            Check.False(hits[0].IsDir);
            hits = (List<FileIndex.SearchHit>)(await ctx.RpcFake.InvokeAsync("files.search", new { query = "srcrlf", cwd = dir, limit = 5 }))!;
            Check.Equal("src/crlf.txt", hits[0].Rel);
            hits = (List<FileIndex.SearchHit>)(await ctx.RpcFake.InvokeAsync("files.search", new { query = "" }))!;
            Check.True(hits.Count > 0 && !hits[0].Rel.Contains('/'));

            var list = (FileIndex.ListResult)(await ctx.RpcFake.InvokeAsync("files.list", new { dir = "" }))!;
            Check.Equal("", list.Dir);
            Check.True(list.Entries[0].IsDir);
            Check.True(list.Entries.Any(e => e.Name == "node_modules" && e.Ignored == true));
            Check.False(list.Entries.Any(e => e.Name == ".git"));
            list = (FileIndex.ListResult)(await ctx.RpcFake.InvokeAsync("files.list", new { dir = "src" }))!;
            Check.Equal("src", list.Dir);
            Check.True(list.Entries.Any(e => e.Rel == "src/app.cs" && e.Size > 0 && e.Mtime is not null));
            var json = NetPiJson.Serialize(list);
            Check.Contains(json, "\"isDir\":");
        });

        // ------------------------------------------------ fileindex: the per-root gate lifecycle
        r.Add("fileindex: the gate table stays within the cache's bound across many roots", async () =>
        {
            var index = new FileIndex();
            for (var i = 0; i < 40; i++)
            {
                var dir = T.TempDir("fileidx");
                T.WriteText(dir, "note.txt", "hello\n");
                try
                {
                    var hits = await index.SearchAsync(dir, "note", 10);
                    Check.Equal(1, hits.Count, $"root {i} answers its own file");
                    Check.Equal("note.txt", hits[0].Rel);
                }
                finally
                {
                    Directory.Delete(dir, recursive: true);
                }
                Check.True(GateProbe.Count(index) <= 16, $"gates bounded while growing (after {i + 1} roots)");
            }
            Check.True(GateProbe.Count(index) > 0, "the recent roots are still tracked");
            Check.True(GateProbe.Count(index) <= 16, "the gate table does not grow without bound");
        });

        r.Add("fileindex: concurrent searches share one gate; a gate in use is never disposed", async () =>
        {
            var dir = T.TempDir("fileidx-lock");
            T.WriteText(dir, "hello.txt", "hi\n");
            var index = new FileIndex();

            // A burst of searches on the same cold root: they must all share the single gate and none
            // may disappear mid-run (an ObjectDisposedException would surface from Task.WhenAll).
            var results = await Task.WhenAll(Enumerable.Repeat(0, 8).Select(_ => index.SearchAsync(dir, "hello", 10)));
            foreach (var hits in results)
            {
                Check.Equal(1, hits.Count, "every concurrent search sees the file");
                Check.Equal("hello.txt", hits[0].Rel);
            }
            Check.Equal(1, GateProbe.Count(index), "one gate for the one root, after the burst");
            var held = GateProbe.Gate(index, dir)!;

            // Hold the gate the way an in-flight search does, then overflow the bound: the pruner must
            // keep it (it is in use) instead of removing and disposing it.
            GateProbe.Enter(index, dir);
            try
            {
                for (var i = 0; i < 20; i++)
                    await StormRoot(index, $"fileidx-storm-{i}");
                Check.True(ReferenceEquals(GateProbe.Gate(index, dir), held), "an in-use gate is neither pruned nor disposed");
            }
            finally
            {
                GateProbe.Exit(index, dir);
            }

            // Once released, the next prune retires and disposes it; a fresh search of the root gets a
            // new gate, and the removed one refuses new users without throwing.
            for (var i = 0; i < 20; i++)
                await StormRoot(index, $"fileidx-storm2-{i}");
            Check.True(GateProbe.Gate(index, dir) is null, "the idle gate was retired and removed");
            Check.False(GateProbe.TryEnter(held), "a retired gate refuses new users without throwing");
            index.Invalidate();
            var fresh = await index.SearchAsync(dir, "hello", 10);
            Check.Equal(1, fresh.Count);
            Check.True(!ReferenceEquals(GateProbe.Gate(index, dir), held), "the root's gate is a fresh instance");

            async Task StormRoot(FileIndex index, string name)
            {
                var d = T.TempDir(name);
                T.WriteText(d, "other.txt", "x\n");
                try
                {
                    Check.Equal(1, (await index.SearchAsync(d, "other", 10)).Count, $"storm root {name}");
                }
                finally
                {
                    Directory.Delete(d, recursive: true);
                }
            }
        });

        r.Add("rpc: files.commits — the history newest first, only what came after a hash, null outside a repository", async () =>
        {
            var dir = T.TempDir("commits");
            if (!await Git(dir, "init", "-q", "-b", "main"))
            {
                Console.WriteLine("    (no git on PATH: skipped)");
                return;
            }
            var ctx = new FakePluginContext(dir);
            await new FilesPlugin().StartAsync(ctx, CancellationToken.None);
            async Task<JsonArray?> Commits(object? p)
            {
                var r = NetPiJson.ToNode(await ctx.RpcFake.InvokeAsync("files.commits", p)) as JsonObject;
                return r?["commits"] as JsonArray;
            }

            await Git(dir, "config", "user.email", "test@example.com");
            await Git(dir, "config", "user.name", "Test");
            Check.Equal(0, (await Commits(new { cwd = dir }))?.Count, "a repository without commits answers with none (not null: it is a repository)");

            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "one");
            await Git(dir, "add", "-A");
            await Git(dir, "commit", "-q", "-m", "first: the nudge counter");
            var first = (JsonArray)(await Commits(new { cwd = dir }))!;
            Check.Equal(1, first.Count);
            Check.Equal("first: the nudge counter", first[0]!["subject"]!.GetValue<string>());
            Check.Equal(40, first[0]!["hash"]!.GetValue<string>().Length, "the full hash");
            Check.Equal(7, first[0]!["short"]!.GetValue<string>().Length, "and the short one");
            Check.Equal("Test", first[0]!["author"]!.GetValue<string>());

            await File.WriteAllTextAsync(Path.Combine(dir, "b.txt"), "two");
            await Git(dir, "add", "-A");
            await Git(dir, "commit", "-q", "-m", "second (idea-c7xyem)");
            var both = (JsonArray)(await Commits(new { cwd = dir }))!;
            Check.Equal(2, both.Count, "newest first");
            Check.Equal("second (idea-c7xyem)", both[0]!["subject"]!.GetValue<string>());

            var after = (JsonArray)(await Commits(new { cwd = dir, since = first[0]!["hash"]!.GetValue<string>() }))!;
            Check.Equal(1, after.Count, "only what came after the hash the caller last saw");
            Check.Equal("second (idea-c7xyem)", after[0]!["subject"]!.GetValue<string>());

            Check.Equal(1, (await Commits(new { cwd = dir, limit = 1 }))!.Count, "limit");
            Check.Equal(2, (await Commits(new { cwd = dir, since = "not-a-hash" }))!.Count, "a hash the repository does not know is ignored, not an error");
            var until = (JsonArray)(await Commits(new { cwd = dir, until = both[0]!["hash"]!.GetValue<string>() }))!; // both[0] is the newest
            Check.Equal(1, until.Count, "until: only what came before that hash");
            Check.Equal("first: the nudge counter", until[0]!["subject"]!.GetValue<string>());
            var gone = NetPiJson.ToNode(await ctx.RpcFake.InvokeAsync("files.commits", new { cwd = dir, since = new string('a', 40) })) as JsonObject;
            Check.Equal(0, gone!["commits"]!.AsArray().Count, "a cursor the repository does not have answers empty");
            Check.Equal(false, gone["reachable"]!.GetValue<bool>(), "and says so, so the caller can re-anchor instead of waiting forever");
            Check.True(gone["gitDir"]!.GetValue<string>().Length > 0, "the git directory is answered, so a worktree can be watched");
            Check.Equal(null, await Commits(new { cwd = T.TempDir("nogit") }), "outside a repository: null");

            static async Task<bool> Git(string cwd, params string[] args)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                try
                {
                    using var p = System.Diagnostics.Process.Start(psi)!;
                    await p.WaitForExitAsync();
                    return p.ExitCode == 0;
                }
                catch (System.ComponentModel.Win32Exception) { return false; }
            }
        });

        r.Add("rpc: files.git — branch and the changes since the last commit, staged or not, new files counted", async () =>
        {
            var dir = T.TempDir("git");
            if (!await Git(dir, "init", "-q", "-b", "main"))
            {
                Console.WriteLine("    (no git on PATH: skipped)");
                return;
            }
            await Git(dir, "config", "user.email", "test@example.com");
            await Git(dir, "config", "user.name", "Test");
            T.WriteText(dir, "a.txt", "1\n2\n3\n");
            T.WriteText(dir, "b.txt", "keep\n");
            T.WriteText(dir, "old name.txt", "x\ny\n");
            await Git(dir, "add", "-A");
            await Git(dir, "commit", "-q", "-m", "first");
            T.WriteText(dir, "a.txt", "1\nchanged\n3\n4\n"); // +2 −1, not staged
            File.Delete(Path.Combine(dir, "b.txt")); // −1
            await Git(dir, "mv", "old name.txt", "new name.txt"); // staged rename
            T.WriteText(dir, "sub/fresh.md", "one\ntwo\nthree"); // new: 3 lines
            File.WriteAllBytes(Path.Combine(dir, "bin.dat"), [0, 1, 2]); // new, binary: no lines

            var ctx = new FakePluginContext(dir);
            await new FilesPlugin().StartAsync(ctx, CancellationToken.None);
            var r = (GitStatus.Result?)await ctx.RpcFake.InvokeAsync("files.git", new { cwd = Path.Combine(dir, "sub") })
                    ?? throw new AssertException("no git status");
            Check.Equal("main", r.Branch);
            var byName = r.Files.ToDictionary(f => Path.GetFileName(f.Path));
            Check.Equal("modified 2 1", $"{byName["a.txt"].Status} {byName["a.txt"].Added} {byName["a.txt"].Deleted}");
            Check.Equal("deleted 0 1", $"{byName["b.txt"].Status} {byName["b.txt"].Added} {byName["b.txt"].Deleted}");
            Check.Equal("renamed", byName["new name.txt"].Status);
            Check.Equal("new 3", $"{byName["fresh.md"].Status} {byName["fresh.md"].Added}");
            Check.Equal("fresh.md", byName["fresh.md"].Rel, "relative to the workspace (a subfolder of the repository)");
            Check.Equal("../a.txt", byName["a.txt"].Rel);
            Check.True(byName["bin.dat"].Added is null, "a binary file has no lines");
            Check.Equal("5 2", $"{r.Added} {r.Deleted}");

            Check.True(await ctx.RpcFake.InvokeAsync("files.git", new { cwd = T.TempDir("nogit") }) is null, "not a repository");

            static async Task<bool> Git(string cwd, params string[] args)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                try
                {
                    using var p = System.Diagnostics.Process.Start(psi)!;
                    await p.WaitForExitAsync();
                    return p.ExitCode == 0;
                }
                catch (System.ComponentModel.Win32Exception) { return false; }
            }
        });

        r.Add("rpc: files.git — a large tree: the status read is bounded, the files array is capped, and the counting runs off the parse path", async () =>
        {
            var dir = T.TempDir("gitbig");

            // 1) The files array holds at most MaxFiles entries; the rest is only counted.
            var many = string.Join("\0", Enumerable.Range(0, GitStatus.MaxFiles + 3).Select(i => $"? files/f{i}.txt")) + "\0";
            var capped = GitStatus.Build(dir, dir, null, many, "");
            Check.Equal(GitStatus.MaxFiles, capped.Files.Count, "the first entries are kept");
            Check.Equal("files/f4999.txt", capped.Files[^1].Rel, "what is dropped is the tail");
            Check.Equal(3, capped.FilesDropped);
            Check.False(capped.Truncated);

            // 2) A read that stopped at its bound reports it and drops the half-written last entry.
            var cut = GitStatus.Build(dir, dir, null, "# branch.head main\0? whole.txt\0? ha", "", truncated: true);
            Check.True(cut.Truncated);
            Check.Equal("main", cut.Branch);
            Check.Equal("whole.txt", cut.Files[0].Rel);
            Check.Equal(0, cut.FilesDropped);

            // A rename a cut left without its old path ends the list instead of swallowing the next entry.
            var half = GitStatus.Build(dir, dir, null, "? kept.txt\02 M 100 renamed.txt\0ren", "", truncated: true);
            Check.Equal(1, half.Files.Count, "the orphaned rename header is dropped, not parsed into a ghost entry");
            Check.Equal("kept.txt", half.Files[0].Rel);

            // 3) End to end: a real repository, and the bound set below the real status read's size.
            if (!await Git(dir, "init", "-q", "-b", "main"))
            {
                Console.WriteLine("    (no git on PATH: skipped)");
                return;
            }
            await Git(dir, "config", "user.email", "test@example.com");
            await Git(dir, "config", "user.name", "Test");
            T.WriteText(dir, "a.txt", "1\n2\n3\n");
            await Git(dir, "add", "-A");
            await Git(dir, "commit", "-q", "-m", "first");
            T.WriteText(dir, "big.txt", string.Concat(Enumerable.Repeat("line\n", 200)));
            var ctx = new FakePluginContext(dir);
            await new FilesPlugin().StartAsync(ctx, CancellationToken.None);
            var full = (GitStatus.Result?)await ctx.RpcFake.InvokeAsync("files.git", new { cwd = dir }) ?? throw new AssertException("no git status");
            Check.False(full.Truncated);
            Check.Equal(1, full.Files.Count);
            Check.Equal("new 200", $"{full.Files[0].Status} {full.Files[0].Added}", "the new file's lines are counted");

            var old = GitStatus.StatusMaxChars;
            GitStatus.StatusMaxChars = 40; // below the real status read: the bound has to be visible, not fatal
            try
            {
                var cutR = (GitStatus.Result?)await ctx.RpcFake.InvokeAsync("files.git", new { cwd = dir }) ?? throw new AssertException("no git status");
                Check.True(cutR.Truncated, "the status read stopped at the bound");
                Check.Equal(0, cutR.Files.Count, "what the cut kept holds no complete entry");
                Check.Equal(0, cutR.FilesDropped, "dangling entries are dropped by the cut, not counted against the cap");
            }
            finally { GitStatus.StatusMaxChars = old; }

            static async Task<bool> Git(string cwd, params string[] args)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                try
                {
                    using var p = System.Diagnostics.Process.Start(psi)!;
                    await p.WaitForExitAsync();
                    return p.ExitCode == 0;
                }
                catch (System.ComponentModel.Win32Exception) { return false; }
            }
        });

        r.Add("tool definitions: labels, categories, read-only flags, guidelines", () =>
        {
            foreach (var t in FilesPlugin.CreateTools(null))
            {
                var d = t.Definition;
                Check.True(d.Label is { Length: > 0 }, d.Name);
                Check.Equal("files", d.Category);
                Check.True(d.PromptGuidelines is not null, d.Name); // guidelines are deduplicated across tools; some have none
                Check.Equal(d.Name is "read" or "grep" or "find" or "ls", d.ReadOnly, d.Name);
                Check.True(d.Parameters["properties"] is not null, d.Name);
            }
        });
    }

    /// <summary>Reads FileIndex's private gate table; the suite has no other handle into the plugin's internals.</summary>
    private static class GateProbe
    {
        private static readonly FieldInfo GatesField =
            typeof(FileIndex).GetField("_gates", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static System.Collections.IDictionary Gates(FileIndex index) =>
            (System.Collections.IDictionary)GatesField.GetValue(index)!;

        public static int Count(FileIndex index) => Gates(index).Count;

        public static object? Gate(FileIndex index, string root) => Gates(index)[root];

        public static void Enter(FileIndex index, string root) => Touch(index, root, "TryEnter");

        public static void Exit(FileIndex index, string root) => Touch(index, root, "Exit");

        public static bool TryEnter(object gate) =>
            (bool)gate.GetType().GetMethod("TryEnter", BindingFlags.Public | BindingFlags.Instance)!.Invoke(gate, null)!;

        private static void Touch(FileIndex index, string root, string method)
        {
            var gate = Gates(index)[root] ?? throw new AssertException($"no gate for {root}");
            gate.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!.Invoke(gate, null);
        }
    }

    /// <summary>A small repository-like tree with ignore rules.</summary>
    private static string MakeTree()
    {
        var dir = T.TempDir("tree");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        T.WriteText(dir, ".git/config", "TODO inside git");
        T.WriteText(dir, ".gitignore", "*.log\n!keep.log\n/root-only.txt\nout/\n");
        T.WriteText(dir, "debug.log", "TODO in log");
        T.WriteText(dir, "root-only.txt", "TODO root only");
        T.WriteText(dir, "src/root-only.txt", "fine");
        T.WriteText(dir, "src/keep.log", "kept");
        T.WriteText(dir, "src/app.cs", "class App\n{\n    // TODO: fix\n}\n");
        T.WriteText(dir, "src/crlf.txt", "first\r\nTODO crlf\r\nlast\r\n");
        T.WriteText(dir, "src/gen/.gitignore", "*.g.cs\n");
        T.WriteText(dir, "src/gen/x.g.cs", "TODO generated");
        T.WriteText(dir, "out/result.txt", "TODO out");
        T.WriteText(dir, "docs/out", "a file named out");
        T.WriteText(dir, "node_modules/pkg/index.js", "TODO module");
        T.WriteText(dir, "bin/Debug/x.txt", "TODO bin");
        T.WriteText(dir, "src/obj/y.txt", "TODO obj");
        T.WriteText(dir, ".github/ci.yml", "on: push");
        return dir;
    }
}
