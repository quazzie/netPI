using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace NetPI.Tools.Documents;

/// <summary>
/// PDF text extraction on PdfPig (pure-managed, Apache-2.0): one page section per PDF page
/// (<c>== Page N ==</c>), the page's text beneath it. An encrypted document without (a correct) password is a
/// clear "cannot extract" answer; a page with images but no text layer is a clear "scanned" answer.
/// </summary>
public static class PdfExtractor
{
    public static string Extract(string path, string? password)
    {
        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(path, new ParsingOptions { Password = password ?? string.Empty });
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new DocumentException("encrypted PDF: it cannot be extracted without its password. Call doc.read again with { path, password }.");
        }
        catch (Exception ex)
        {
            throw new DocumentException($"corrupt PDF: PdfPig cannot read it ({Short(ex)})");
        }

        using (doc)
        {
            var pages = doc.GetPages().ToList();
            if (pages.Count == 0)
                throw new DocumentException("the PDF has no pages");

            var sb = new StringBuilder();
            var empty = 0;
            for (var i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                var text = (page.Text ?? "").Trim();
                sb.Append("== Page ").Append(i + 1).Append(" ==\n");
                if (text.Length > 0)
                    sb.AppendLine(text);
                else
                    empty++;
                sb.Append('\n');
            }

            // A document (or page) with images but no text layer is a scan: say so, clearly, instead of
            // returning blank pages.
            if (empty == pages.Count)
            {
                var images = pages.Sum(p => p.NumberOfImages);
                if (images > 0)
                    throw new DocumentException($"no text layer: the PDF's {pages.Count} page{(pages.Count == 1 ? "" : "s")} are scanned image{(images == 1 ? "" : "s")} ({images} image{(images == 1 ? "" : "s")}); OCR is not supported.");
                throw new DocumentException("the PDF has no extractable text (no text layer on any page)");
            }
            return sb.ToString().TrimEnd('\n');
        }
    }

    private static string Short(Exception ex) =>
        ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;
}
