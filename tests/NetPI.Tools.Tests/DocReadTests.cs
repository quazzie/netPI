using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NetPI.Tools.Documents;

namespace NetPI.Tools.Tests;

/// <summary>
/// Fixture documents for <c>doc.read</c>: minimal but genuine .docx/.xlsx/.pptx (a ZIP of the parts the
/// extractor reads), handcrafted PDFs (plain and Flate-compressed streams), an encrypted PDF (R=2, 40-bit
/// RC4) whose padding constant matches PdfPig's implementation, and the corrupt cases. Everything is
/// generated in-process, so the suite needs no binary fixtures.
/// </summary>
public static class DocFixtures
{
    // ------------------------------------------------------------------ Office (ZIP + XML)

    private static byte[] Zip(params (string Name, string Content)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in parts)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        return ms.ToArray();
    }

    /// <summary>A docx with a title, a heading-2, two-run paragraph and a 2x2 table.</summary>
    public static string Docx(string dir) => T.WriteBytes(dir, "notes.docx", DocxBytes());

    /// <summary>The docx's bytes, with an optional extra paragraph (the changed-file cache test).</summary>
    public static byte[] DocxBytes(string? extra = null)
    {
        var extraP = extra is null ? "" : $"<w:p><w:r><w:t>{extra}</w:t></w:r></w:p>";
        const string body = """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:pPr><w:pStyle w:val="Title"/></w:pPr><w:r><w:t>Quarterly report</w:t></w:r></w:p>
                <w:p><w:r><w:t>First paragraph of the body.</w:t></w:r></w:p>
                <w:p><w:pPr><w:pStyle w:val="Heading2"/></w:pPr><w:r><w:t>Results</w:t></w:r></w:p>
                <w:p><w:r><w:t>Second paragraph</w:t></w:r><w:r><w:t> with two runs.</w:t></w:r></w:p>
                <w:tbl>
                  <w:tr><w:tc><w:p><w:r><w:t>Q1</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>100</w:t></w:r></w:p></w:tc></w:tr>
                  <w:tr><w:tc><w:p><w:r><w:t>Q2</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>120</w:t></w:r></w:p></w:tc></w:tr>
                </w:tbl>
              {EXTRA}
              </w:body>
            </w:document>
            """;
        return Zip(("word/document.xml", body.Replace("{EXTRA}", extraP)));
    }

    /// <summary>A docx with many paragraphs (the paging test).</summary>
    public static string LongDocx(string dir)
    {
        var ps = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"<w:p><w:r><w:t>Line {i:D2}</w:t></w:r></w:p>"));
        var document = $"""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{ps}</w:body></w:document>""";
        return T.WriteBytes(dir, "long.docx", Zip(("word/document.xml", document)));
    }

    /// <summary>An xlsx: two sheets, shared strings, plain numbers and an inline string.</summary>
    public static string Xlsx(string dir)
    {
        const string workbook = """
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheets><sheet name="Data" sheetId="1"/><sheet name="Meta" sheetId="2"/></sheets>
            </workbook>
            """;
        const string shared = """
            <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="2" uniqueCount="2">
              <si><t>alpha</t></si><si><t>beta</t></si>
            </sst>
            """;
        const string sheet1 = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
                <row r="3"><c r="A3"><v>10</v></c><c r="B3"><v>20</v></c></row>
              </sheetData>
            </worksheet>
            """;
        const string sheet2 = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>a note</t></is></c></row>
              </sheetData>
            </worksheet>
            """;
        return T.WriteBytes(dir, "book.xlsx", Zip(("xl/workbook.xml", workbook), ("xl/sharedStrings.xml", shared), ("xl/worksheets/sheet1.xml", sheet1), ("xl/worksheets/sheet2.xml", sheet2)));
    }

    /// <summary>A pptx: two slides, the second with two paragraphs.</summary>
    public static string Pptx(string dir)
    {
        string Slide(string title, params string[] more)
        {
            var all = new List<string> { title };
            all.AddRange(more);
            var ps = string.Join("", all.Select(t => $"<a:p><a:r><a:t>{t}</a:t></a:r></a:p>"));
            return $"""<p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:cSld><p:spTree>{ps}</p:spTree></p:cSld></p:sld>""";
        }
        return T.WriteBytes(dir, "deck.pptx", Zip(("ppt/slides/slide1.xml", Slide("Hello slide one")), ("ppt/slides/slide2.xml", Slide("Line A", "Line B"))));
    }

    // ------------------------------------------------------------------ PDF

    /// <summary>A two-page PDF with plain (uncompressed) content streams.</summary>
    public static string PdfPlain(string dir) =>
        T.WriteBytes(dir, "plain.pdf", Pdf(false, [
            "BT /F1 24 Tf 72 720 Td (Hello PDF page one) Tj 0 -40 Td (Second line of text) Tj ET",
            "BT /F1 12 Tf 72 700 Td (Page two text) Tj ET"]));

    /// <summary>The same document with Flate-compressed content streams (the /Filter /FlateDecode path).</summary>
    public static string PdfCompressed(string dir) =>
        T.WriteBytes(dir, "comp.pdf", Pdf(true, [
            "BT /F1 24 Tf 72 720 Td (Hello PDF page one) Tj 0 -40 Td (Second line of text) Tj ET",
            "BT /F1 12 Tf 72 700 Td (Page two text) Tj ET"]));

    /// <summary>A one-page PDF with no content stream at all: the "no text layer" answer.</summary>
    public static string PdfNoText(string dir)
    {
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        };
        return T.WriteBytes(dir, "notext.pdf", PdfAssemble(objs));
    }

    /// <summary>
    /// An encrypted PDF (R=2, 40-bit RC4) PdfPig can open: its padding constant is PdfPig's own
    /// (EncryptionHandler.PaddingBytes, v0.1.16) - a fixture using the spec's bytes would not verify.
    /// </summary>
    public static string PdfEncrypted(string dir, string password)
    {
        // PdfPig's padding constant, verbatim from v0.1.16
        var padding = new byte[] { 0x28,0xBF,0x4E,0x5E,0x4E,0x75,0x8A,0x41,0x64,0x00,0x4E,0x56,0xFF,0xFA,0x01,0x08,0x2E,0x2E,0x00,0xB6,0xD0,0x68,0x3E,0x80,0x2F,0x0C,0xA9,0xFE,0x64,0x53,0x69,0x7A };
        byte[] P = [0xFC, 0xFF, 0xFF, 0xFF];
        byte[] fileId = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

        byte[] Rc4(byte[] key, byte[] data)
        {
            var s = new byte[256]; for (var i = 0; i < 256; i++) s[i] = (byte)i;
            var j = 0;
            for (var i = 0; i < 256; i++) { j = (j + s[i] + key[i % key.Length]) & 0xFF; (s[i], s[j]) = (s[j], s[i]); }
            var i2 = 0; j = 0; var outp = new byte[data.Length];
            for (var k = 0; k < data.Length; k++) { i2 = (i2 + 1) & 0xFF; j = (j + s[i2]) & 0xFF; (s[i2], s[j]) = (s[j], s[i2]); outp[k] = (byte)(data[k] ^ s[(s[i2] + s[j]) & 0xFF]); }
            return outp;
        }
        // the same padding PdfPig uses when it verifies a password
        byte[] Pad(string p)
        {
            var b = Encoding.ASCII.GetBytes(p);
            var r = new byte[32];
            b.CopyTo(r, 0);
            Array.Copy(padding, 0, r, b.Length, 32 - b.Length);
            return r;
        }
        static byte[] Concat(params byte[][] parts) { var n = parts.Sum(p => p.Length); var r = new byte[n]; var o = 0; foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; } return r; }
        string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

        var pw = Pad(password);
        // R=2: the owner key is MD5 of the padded owner password only; O = RC4(ownerKey, padding)
        var ownerKey = MD5.HashData(pw).AsSpan(0, 5).ToArray();
        var o = Rc4(ownerKey, padding);
        // user key: MD5(paddedUserPw + O + P(LE) + ID[0]); U = RC4(userKey, padding)
        var userKey = MD5.HashData(Concat(pw, o, P, fileId)).AsSpan(0, 5).ToArray();
        var u = Rc4(userKey, padding);
        // the content stream is object 4, generation 0: the per-object RC4 key is the first 10 bytes of
        // MD5(key + objectNumber(3 LE) + generation(2 LE)) (PDF 32000 Algorithm 3, as implemented by PdfPig)
        var content = Encoding.ASCII.GetBytes("BT /F1 24 Tf 72 720 Td (Encrypted secret text) Tj ET");
        var streamKey = MD5.HashData(Concat(userKey, [0x04, 0x00, 0x00, 0x00, 0x00])).AsSpan(0, 10).ToArray();
        content = Rc4(streamKey, content);

        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{Encoding.Latin1.GetString(content)}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Filter /Standard /V 1 /R 2 /O <{Hex(o)}> /U <{Hex(u)}> /P -4 >>",
        };
        var trailer = $"/Encrypt 6 0 R /ID [<{Hex(fileId)}> <{Hex(fileId)}>]";
        return T.WriteBytes(dir, "enc.pdf", PdfAssemble(objs, trailer));
    }

    /// <summary>A real PDF cut in half: starts with %PDF, then is garbage - the "corrupt PDF" answer.</summary>
    public static string PdfCorrupt(string dir)
    {
        var good = Pdf(false, ["BT /F1 24 Tf 72 720 Td (cut here) Tj ET"]);
        return T.WriteBytes(dir, "corrupt.pdf", good.AsSpan(0, good.Length / 2).ToArray());
    }

    /// <summary>A docx ZIP cut in half: the "corrupt .docx" answer.</summary>
    public static string DocxCorrupt(string dir)
    {
        var good = Zip(("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>x</w:t></w:r></w:p></w:body></w:document>"));
        return T.WriteBytes(dir, "corrupt.docx", good.AsSpan(0, good.Length / 2).ToArray());
    }

    /// <summary>A docx ZIP with a .pdf name: the "lying extension" answer.</summary>
    public static string DocxNamedPdf(string dir) =>
        T.WriteBytes(dir, "fake.pdf", Zip(("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>")));

    // ------------------------------------------------------------------ the PDF assembler

    private static byte[] Pdf(bool compressed, string[] pageContents)
    {
        byte[] Deflate(byte[] d)
        {
            using var ms = new MemoryStream();
            using (var ds = new ZLibStream(ms, CompressionLevel.Optimal))
                ds.Write(d);
            return ms.ToArray();
        }
        string PageStream(byte[] sb, bool flate)
        {
            var dict = flate ? $"<< /Length {sb.Length} /Filter /FlateDecode >>" : $"<< /Length {sb.Length} >>";
            return dict + "\nstream\n" + Encoding.Latin1.GetString(sb) + "\nendstream";
        }
        // object layout: 1 catalog, 2 pages, 3 font, then page i at 4 + 2i and its content at 5 + 2i
        string PageHdr(int contentObj) => $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contentObj} 0 R /Resources << /Font << /F1 3 0 R >> >> >>";

        var n = pageContents.Length;
        var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{4 + 2 * i} 0 R"));
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {n} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        foreach (var i in Enumerable.Range(0, n))
        {
            var raw = Encoding.ASCII.GetBytes(pageContents[i]);
            objs.Add(PageHdr(5 + 2 * i));
            objs.Add(PageStream(compressed ? Deflate(raw) : raw, compressed));
        }
        return PdfAssemble(objs);
    }

    private static byte[] PdfAssemble(List<string> objs, string extraTrailer = "")
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objs.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj ").Append(objs[i]).Append(" endobj\n");
        }
        var xrefPos = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (var off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer << /Size ").Append(objs.Count + 1).Append(" /Root 1 0 R ").Append(extraTrailer).Append(">>\nstartxref\n").Append(xrefPos).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}

/// <summary>The <c>doc.read</c> tests: the formats, the bounded output, the clear failures, the cache.</summary>
public static class DocReadTests
{
    public static void Register(TestRunner r)
    {
        r.Add("doc.read: docx headings, paragraphs and table rows", async () =>
        {
            var dir = T.TempDir("docdocx");
            var path = DocFixtures.Docx(dir);
            var res = await T.Run(new DocReadTool(), dir, new { path = "notes.docx" });
            ToolCheck.Ok(res);
            Check.Contains(res.Content, "# Quarterly report");
            Check.Contains(res.Content, "## Results");
            Check.Contains(res.Content, "First paragraph of the body.");
            Check.Contains(res.Content, "Second paragraph with two runs.");
            Check.Contains(res.Content, "| Q1 | 100 |");
            Check.Contains(res.Content, "| Q2 | 120 |");
            var d = T.D(res);
            Check.Equal("docx", d.Str("format"));
            Check.False(d.Bool("cacheHit"), "a first read is a miss");
        });

        r.Add("doc.read: xlsx sheets, rows and shared strings", async () =>
        {
            var dir = T.TempDir("docxlsx");
            DocFixtures.Xlsx(dir);
            var res = await T.Run(new DocReadTool(), dir, new { path = "book.xlsx" });
            ToolCheck.Ok(res);
            Check.Contains(res.Content, "== Sheet: Data ==");
            Check.Contains(res.Content, "1: alpha | beta");
            Check.Contains(res.Content, "3: 10 | 20");
            Check.Contains(res.Content, "== Sheet: Meta ==");
            Check.Contains(res.Content, "1: a note");
            Check.Equal("xlsx", T.D(res).Str("format"));
        });

        r.Add("doc.read: pptx slides", async () =>
        {
            var dir = T.TempDir("docpptx");
            DocFixtures.Pptx(dir);
            var res = await T.Run(new DocReadTool(), dir, new { path = "deck.pptx" });
            ToolCheck.Ok(res);
            Check.Contains(res.Content, "== Slide 1 ==");
            Check.Contains(res.Content, "Hello slide one");
            Check.Contains(res.Content, "== Slide 2 ==");
            Check.Contains(res.Content, "Line A");
            Check.Contains(res.Content, "Line B");
            Check.Equal("pptx", T.D(res).Str("format"));
        });

        r.Add("doc.read: PDF pages and text", async () =>
        {
            var dir = T.TempDir("docpdf");
            DocFixtures.PdfPlain(dir);
            var res = await T.Run(new DocReadTool(), dir, new { path = "plain.pdf" });
            ToolCheck.Ok(res);
            Check.Contains(res.Content, "== Page 1 ==");
            Check.Contains(res.Content, "Hello PDF page one");
            Check.Contains(res.Content, "Second line of text");
            Check.Contains(res.Content, "== Page 2 ==");
            Check.Contains(res.Content, "Page two text");
            Check.Equal("pdf", T.D(res).Str("format"));
        });

        r.Add("doc.read: compressed-stream PDF", async () =>
        {
            var dir = T.TempDir("docpdfc");
            DocFixtures.PdfCompressed(dir);
            var res = await T.Run(new DocReadTool(), dir, new { path = "comp.pdf" });
            ToolCheck.Ok(res);
            Check.Contains(res.Content, "Hello PDF page one");
            Check.Contains(res.Content, "Page two text");
        });

        r.Add("doc.read: a PDF with no text layer says so", async () =>
        {
            var dir = T.TempDir("docnotext");
            DocFixtures.PdfNoText(dir);
            var res = await T.Run(new DocReadTool(), dir, new { path = "notext.pdf" });
            ToolCheck.Error(res, "no extractable text");
        });

        r.Add("doc.read: an encrypted PDF asks for the password", async () =>
        {
            var dir = T.TempDir("docenc");
            DocFixtures.PdfEncrypted(dir, "s3cret");
            var res = await T.Run(new DocReadTool(), dir, new { path = "enc.pdf" });
            ToolCheck.Error(res, "password");
            Check.NotContains(res.Content, "Encrypted secret text", "the text must not leak without the password");
        });

        r.Add("doc.read: an encrypted PDF with the password", async () =>
        {
            var dir = T.TempDir("docenc2");
            DocFixtures.PdfEncrypted(dir, "s3cret");
            // one tool: the cache is per plugin instance, so a hit needs the same instance
            var tool = new DocReadTool();
            var res = await T.Run(tool, dir, new { path = "enc.pdf", password = "s3cret" });
            ToolCheck.Ok(res);
            Check.Contains(res.Content, "Encrypted secret text");
            Check.False(T.D(res).Bool("cacheHit"), "a first read is a miss");
            // the same file with the same password is a cache hit
            var again = await T.Run(tool, dir, new { path = "enc.pdf", password = "s3cret" });
            ToolCheck.Ok(again);
            Check.True(T.D(again).Bool("cacheHit"), "a repeat read is a hit");
        });

        r.Add("doc.read: an encrypted PDF with the wrong password", async () =>
        {
            var dir = T.TempDir("docenc3");
            DocFixtures.PdfEncrypted(dir, "s3cret");
            var res = await T.Run(new DocReadTool(), dir, new { path = "enc.pdf", password = "nope" });
            ToolCheck.Error(res, "password");
        });

        r.Add("doc.read: a corrupt PDF is a clear answer", async () =>
        {
            var dir = T.TempDir("doccorrupt");
            DocFixtures.PdfCorrupt(dir);
            var res = await T.Run(new DocReadTool(), dir, new { path = "corrupt.pdf" });
            ToolCheck.Error(res, "corrupt");
        });

        r.Add("doc.read: a corrupt docx is a clear answer", async () =>
        {
            var dir = T.TempDir("doccorrupt2");
            DocFixtures.DocxCorrupt(dir);
            var res = await T.Run(new DocReadTool(), dir, new { path = "corrupt.docx" });
            ToolCheck.Error(res, "corrupt");
        });

        r.Add("doc.read: a lying extension is answered by the content", async () =>
        {
            var dir = T.TempDir("doclying");
            DocFixtures.DocxNamedPdf(dir);
            var res = await T.Run(new DocReadTool(), dir, new { path = "fake.pdf" });
            ToolCheck.Error(res, ".docx");
        });

        r.Add("doc.read: an unsupported format is a clear answer", async () =>
        {
            var dir = T.TempDir("docsupp");
            T.WriteText(dir, "plain.txt", "just text");
            var res = await T.Run(new DocReadTool(), dir, new { path = "plain.txt" });
            ToolCheck.Error(res, "not a supported document format");
        });

        r.Add("doc.read: a missing file is a clear answer", async () =>
        {
            var dir = T.TempDir("docmissing");
            var res = await T.Run(new DocReadTool(), dir, new { path = "nope.pdf" });
            ToolCheck.Error(res, "not found");
        });

        r.Add("doc.read: a directory is not a document", async () =>
        {
            var dir = T.TempDir("docdir");
            var res = await T.Run(new DocReadTool(), dir, new { path = "." });
            ToolCheck.Error(res, "directory");
        });

        r.Add("doc.read: paging with offset and limit", async () =>
        {
            var dir = T.TempDir("docpage");
            DocFixtures.LongDocx(dir);
            var tool = new DocReadTool();
            var res = await T.Run(tool, dir, new { path = "long.docx", limit = 10 });
            ToolCheck.Ok(res);
            Check.Contains(res.Content, "Line 01");
            Check.Contains(res.Content, "Line 10");
            Check.NotContains(res.Content, "Line 11");
            Check.Contains(res.Content, "Use offset=11 to continue");
            Check.True(T.D(res).Bool("truncated"));

            var tail = await T.Run(tool, dir, new { path = "long.docx", offset = 55 });
            ToolCheck.Ok(tail);
            Check.Contains(tail.Content, "Line 55");
            Check.Contains(tail.Content, "Line 60");
            Check.NotContains(tail.Content, "to continue");
            Check.False(T.D(tail).Bool("truncated"));

            var past = await T.Run(tool, dir, new { path = "long.docx", offset = 999 });
            ToolCheck.Error(past, "past the end");
        });

        r.Add("doc.read: a changed file misses the cache", async () =>
        {
            var dir = T.TempDir("doccache");
            var path = DocFixtures.Docx(dir);
            var tool = new DocReadTool();
            var first = await T.Run(tool, dir, new { path = "notes.docx" });
            Check.False(T.D(first).Bool("cacheHit"));
            var second = await T.Run(tool, dir, new { path = "notes.docx" });
            Check.True(T.D(second).Bool("cacheHit"), "an unchanged file is a hit");
            // the file changes (new mtime and size, new content): the old extraction must not be served.
            // (Appending to a docx would break its ZIP, so the file is re-written as a different document.)
            File.WriteAllBytes(path, DocFixtures.DocxBytes("An extra paragraph."));
            var third = await T.Run(tool, dir, new { path = "notes.docx" });
            ToolCheck.Ok(third);
            Check.False(T.D(third).Bool("cacheHit"), "a changed file is a miss");
            Check.Contains(third.Content, "An extra paragraph.");
        });

        r.Add("doc.read: the cache evicts least-recently-used entries at the byte cap", async () =>
        {
            var dir = T.TempDir("doclru");
            var settings = new FakeSettings();
            settings.SetQuiet("documents.cacheMaxBytes", 2048);
            var cache = new NetPI.Tools.Documents.DocCache(settings);
            Check.Equal(2048, cache.MaxBytes, "the cap comes from the setting");
            cache.Add("a", new string('x', 1500));
            cache.Add("b", new string('y', 1500)); // 3000 > 2048: "a" (the LRU entry) goes
            Check.True(cache.Get("a") is null, "the LRU entry was evicted");
            Check.True(cache.Get("b") is not null, "the MRU entry survived");
            Check.Equal(1, cache.Entries);
        });
    }
}
