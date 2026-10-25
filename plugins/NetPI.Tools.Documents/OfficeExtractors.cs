using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NetPI.Tools.Documents;

/// <summary>A document that cannot be extracted (corrupt, unreadable, missing part). The message is the tool's answer.</summary>
public sealed class DocumentException(string message) : Exception(message);

/// <summary>
/// Office Open XML (OOXML) extraction on the BCL: a .docx/.xlsx/.pptx is a ZIP of XML parts, so
/// <see cref="ZipArchive"/> + <see cref="XmlReader"/>/XDocument - no dependencies. Each extractor emits the
/// document's text with its structure: docx headings and table rows, xlsx sheet and row numbers, pptx slide
/// numbers.
/// </summary>
public static class OfficeExtractors
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>The docx body: paragraphs (heading styles marked <c>#</c> by level), table rows as <c>| cell | cell |</c>.</summary>
    public static string ExtractDocx(string path)
    {
        using var zip = OpenZip(path, ".docx");
        using var stream = zip.GetEntry("word/document.xml")?.Open() ?? throw new DocumentException("corrupt .docx: the word/document.xml part is missing");
        var doc = System.Xml.Linq.XDocument.Load(stream, System.Xml.Linq.LoadOptions.None);
        var body = doc.Root?.Element(W + "body") ?? throw new DocumentException("corrupt .docx: no document body");
        return ExtractBody(body);
    }

    private static string ExtractBody(System.Xml.Linq.XElement body)
    {
        var sb = new StringBuilder();
        foreach (var el in body.Elements())
        {
            if (el.Name.LocalName is not ("p" or "tbl")) continue;
            if (el.Name.LocalName == "p")
            {
                var text = ParagraphText(el);
                if (text.Length == 0) continue;
                if (HeadingLevel(el) is { } level)
                    sb.Append(new string('#', Math.Clamp(level, 1, 6))).Append(' ').AppendLine(text);
                else
                    sb.AppendLine(text);
            }
            else
            {
                // a table: one line per row, cells separated by " | "
                foreach (var row in el.Elements(W + "tr"))
                {
                    var cells = row.Elements(W + "tc").Select(ParagraphText).Where(c => c.Length > 0).ToList();
                    if (cells.Count == 0) continue;
                    sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
                }
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string ParagraphText(System.Xml.Linq.XElement p)
    {
        // every w:t in the paragraph, in order (runs, tabs, breaks, hyperlinks)
        var sb = new StringBuilder();
        foreach (var t in p.Descendants(W + "t")) sb.Append(t.Value);
        return sb.ToString().Trim();
    }

    /// <summary>The heading level of a styled paragraph (Heading1-9, Title), null for body text.</summary>
    private static int? HeadingLevel(System.Xml.Linq.XElement p)
    {
        var style = p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
        if (style is null) return null;
        if (style.Equals("Title", StringComparison.OrdinalIgnoreCase)) return 1;
        if (style.Length > 7 && style[..7].Equals("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(style[7..], out var n) && n is >= 1 and <= 9)
            return n;
        return null;
    }

    /// <summary>The xlsx sheets, in workbook order: <c>== Sheet: name ==</c>, then <c>row: c1 | c2 | …</c> (empty rows skipped).</summary>
    public static string ExtractXlsx(string path)
    {
        using var zip = OpenZip(path, ".xlsx");
        var workbook = ZipXml(zip, "xl/workbook.xml", "the workbook part (xl/workbook.xml) is missing");
        var sheets = workbook.Root?.Element(X + "sheets")?.Elements(X + "sheet")
            .Select(s => (Name: s.Attribute("name")?.Value ?? "", Index: s.Attribute("sheetId")?.Value ?? ""))
            .ToList() ?? [];
        if (sheets.Count == 0) throw new DocumentException("corrupt .xlsx: the workbook names no sheets");

        string? sharedPath = "xl/sharedStrings.xml";
        List<string>? shared = null;
        if (zip.GetEntry(sharedPath) is { } sse)
        {
            var sdoc = System.Xml.Linq.XDocument.Load(sse.Open(), System.Xml.Linq.LoadOptions.None);
            shared = sdoc.Root?.Elements(X + "si")
                .Select(si => string.Join("", si.Descendants(X + "t").Select(t => t.Value)))
                .ToList() ?? [];
        }

        var sb = new StringBuilder();
        for (var i = 0; i < sheets.Count; i++)
        {
            // sheet1.xml, sheet2.xml, … in workbook order
            var entry = zip.GetEntry($"xl/worksheets/sheet{i + 1}.xml");
            if (entry is null) continue;
            var sheet = System.Xml.Linq.XDocument.Load(entry.Open(), System.Xml.Linq.LoadOptions.None);
            var data = sheet.Root?.Element(X + "sheetData");
            if (data is null) continue;
            sb.Append("== Sheet: ").Append(sheets[i].Name).Append(" ==\n");
            foreach (var row in data.Elements(X + "row"))
            {
                var rowNum = int.Parse(row.Attribute("r")?.Value ?? "0");
                var cells = new List<string>();
                foreach (var c in row.Elements(X + "c"))
                {
                    var value = CellText(c, shared);
                    cells.Add(value);
                }
                if (cells.All(string.IsNullOrEmpty)) continue;
                sb.Append(rowNum).Append(": ").AppendLine(string.Join(" | ", cells));
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string CellText(System.Xml.Linq.XElement c, List<string>? shared)
    {
        var type = c.Attribute("t")?.Value;
        var v = c.Element(X + "v")?.Value;
        return type switch
        {
            "s" => v is not null && int.TryParse(v, out var i) && i >= 0 && (shared?.Count ?? 0) > i ? shared![i] : "",
            "inlineStr" => string.Join("", c.Element(X + "is")?.Descendants(X + "t").Select(t => t.Value) ?? []),
            "b" => v == "1" ? "TRUE" : v == "0" ? "FALSE" : v ?? "",
            _ => v ?? "",
        };
    }

    /// <summary>The pptx slides in number order: <c>== Slide N ==</c>, then the slide's text, paragraph by paragraph.</summary>
    public static string ExtractPptx(string path)
    {
        using var zip = OpenZip(path, ".pptx");
        // ZipArchiveEntry.Name is the file part only ("slide1.xml"); the folder travels in FullName
        var slides = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Entry: e, Number: int.TryParse(e.FullName["ppt/slides/slide".Length..^4], out var n) ? n : 0))
            .OrderBy(s => s.Number)
            .ToList();
        if (slides.Count == 0) throw new DocumentException("corrupt .pptx: no slides found (ppt/slides/slideN.xml)");

        var sb = new StringBuilder();
        foreach (var s in slides)
        {
            var doc = System.Xml.Linq.XDocument.Load(s.Entry.Open(), System.Xml.Linq.LoadOptions.None);
            var paragraphs = doc.Descendants(A + "p")
                .Select(p => string.Join("", p.Descendants(A + "t").Select(t => t.Value)).Trim())
                .Where(t => t.Length > 0)
                .ToList();
            sb.Append("== Slide ").Append(s.Number).Append(" ==\n");
            sb.AppendLine(string.Join('\n', paragraphs));
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ------------------------------------------------------------------ plumbing

    private static ZipArchive OpenZip(string path, string ext)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (Exception ex) when (ex is not DocumentException)
        {
            throw new DocumentException($"corrupt {ext}: it is not a readable ZIP archive ({Short(ex)})");
        }
    }

    private static System.Xml.Linq.XDocument ZipXml(ZipArchive zip, string entry, string missing)
    {
        if (zip.GetEntry(entry) is not { } e) throw new DocumentException($"corrupt .xlsx: {missing}");
        try
        {
            return System.Xml.Linq.XDocument.Load(e.Open(), System.Xml.Linq.LoadOptions.None);
        }
        catch (Exception ex) when (ex is not DocumentException)
        {
            throw new DocumentException($"corrupt .xlsx: {entry} is not readable XML ({Short(ex)})");
        }
    }

    private static string Short(Exception ex) =>
        ex.Message.Length > 80 ? ex.Message[..80] + "…" : ex.Message;
}
