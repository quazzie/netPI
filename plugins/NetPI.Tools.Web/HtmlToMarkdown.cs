using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NetPI.Tools.Web;

internal sealed record HtmlPage(string Title, string Content);

/// <summary>
/// HTML → Markdown (or plain text) for the model: a tolerant tokenizer plus a streaming renderer, no DOM and no
/// dependencies. The content root is <c>&lt;main&gt;</c>, else the longest <c>&lt;article&gt;</c>, else <c>&lt;body&gt;</c>;
/// navigation, scripts, forms, hidden elements and obvious page chrome (cookie banners, share bars…) are dropped.
/// </summary>
internal static partial class HtmlToMarkdown
{
    // ------------------------------------------------------------------ tokens

    private enum Kind { Start, End, Text }

    private sealed class Token
    {
        public Kind Kind;
        public string Name = "";      // lower-case tag name
        public string Text = "";      // decoded text (Text tokens; raw for <pre> is kept as-is)
        public Dictionary<string, string>? Attrs;
        public bool SelfClosing;
        public string? Attr(string name) => Attrs is not null && Attrs.TryGetValue(name, out var v) ? v : null;
    }

    private static readonly HashSet<string> Void = ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr", "param"];
    private static readonly HashSet<string> RawText = ["script", "style", "textarea", "title", "xmp", "noscript", "template"];
    private static readonly HashSet<string> Skip =
    [
        "script", "style", "noscript", "template", "svg", "math", "canvas", "iframe", "object", "embed", "audio", "video", "picture",
        "nav", "aside", "footer", "form", "button", "select", "option", "input", "textarea", "dialog", "menu", "head", "map",
    ];
    private static readonly HashSet<string> Block =
    [
        "p", "div", "section", "article", "main", "header", "footer", "aside", "address", "center", "figure", "figcaption",
        "details", "summary", "fieldset", "legend", "hgroup", "body", "html", "dl", "dt", "dd", "table", "thead", "tbody", "tfoot",
        "caption", "blockquote", "pre", "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6", "hr",
    ];
    private static readonly string[] NoiseWords =
        ["cookie", "cookies", "consent", "gdpr", "advert", "advertisement", "ads", "newsletter", "popup", "modal", "share", "sharing", "social", "sr-only", "visually-hidden", "skip-link", "sidebar"];

    private static List<Token> Tokenize(string html)
    {
        var tokens = new List<Token>(html.Length / 20);
        var text = new StringBuilder();
        int i = 0, n = html.Length;

        void FlushText()
        {
            if (text.Length == 0) return;
            tokens.Add(new Token { Kind = Kind.Text, Text = WebUtility.HtmlDecode(text.ToString()) });
            text.Clear();
        }

        while (i < n)
        {
            var c = html[i];
            if (c != '<' || i + 1 >= n) { text.Append(c); i++; continue; }
            var next = html[i + 1];
            if (next == '!' || next == '?')
            {
                FlushText();
                if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
                {
                    var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 3;
                }
                else
                {
                    var end = html.IndexOf('>', i + 2);
                    i = end < 0 ? n : end + 1;
                }
                continue;
            }
            var closing = next == '/';
            var nameStart = closing ? i + 2 : i + 1;
            if (nameStart >= n || !char.IsAsciiLetter(html[nameStart])) { text.Append(c); i++; continue; }
            var j = nameStart;
            while (j < n && (char.IsAsciiLetterOrDigit(html[j]) || html[j] is '-' or ':' or '_')) j++;
            var name = html[nameStart..j].ToLowerInvariant();
            FlushText();

            if (closing)
            {
                var end = html.IndexOf('>', j);
                i = end < 0 ? n : end + 1;
                tokens.Add(new Token { Kind = Kind.End, Name = name });
                continue;
            }

            var tok = new Token { Kind = Kind.Start, Name = name };
            i = ParseAttributes(html, j, tok);
            tokens.Add(tok);
            if (Void.Contains(name) || tok.SelfClosing) continue;

            if (RawText.Contains(name))
            {
                // raw text runs to the matching end tag; keep it for <title> and <textarea>, drop it otherwise
                var close = IndexOfEndTag(html, name, i);
                var raw = close < 0 ? html[i..] : html[i..close];
                if (name is "title" or "textarea") tokens.Add(new Token { Kind = Kind.Text, Text = WebUtility.HtmlDecode(raw) });
                tokens.Add(new Token { Kind = Kind.End, Name = name });
                if (close < 0) { i = n; break; }
                var gt = html.IndexOf('>', close);
                i = gt < 0 ? n : gt + 1;
            }
        }
        FlushText();
        return tokens;
    }

    private static int IndexOfEndTag(string html, string name, int from)
    {
        var at = from;
        while (true)
        {
            var k = html.IndexOf("</", at, StringComparison.Ordinal);
            if (k < 0) return -1;
            if (k + 2 + name.Length <= html.Length && string.Compare(html, k + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                var after = k + 2 + name.Length;
                if (after >= html.Length || !char.IsAsciiLetterOrDigit(html[after])) return k;
            }
            at = k + 2;
        }
    }

    private static int ParseAttributes(string html, int i, Token tok)
    {
        var n = html.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(html[i])) i++;
            if (i >= n) break;
            if (html[i] == '>') return i + 1;
            if (html[i] == '/')
            {
                if (i + 1 < n && html[i + 1] == '>') { tok.SelfClosing = true; return i + 2; }
                i++;
                continue;
            }
            var ns = i;
            while (i < n && !char.IsWhiteSpace(html[i]) && html[i] is not ('=' or '>' or '/')) i++;
            var name = html[ns..i].ToLowerInvariant();
            while (i < n && char.IsWhiteSpace(html[i])) i++;
            var value = "";
            if (i < n && html[i] == '=')
            {
                i++;
                while (i < n && char.IsWhiteSpace(html[i])) i++;
                if (i < n && html[i] is '"' or '\'')
                {
                    var q = html[i];
                    var end = html.IndexOf(q, i + 1);
                    if (end < 0) end = n;
                    value = html[(i + 1)..end];
                    i = Math.Min(n, end + 1);
                }
                else
                {
                    var vs = i;
                    while (i < n && !char.IsWhiteSpace(html[i]) && html[i] != '>') i++;
                    value = html[vs..i];
                }
            }
            if (name.Length > 0)
            {
                tok.Attrs ??= new Dictionary<string, string>(StringComparer.Ordinal);
                tok.Attrs.TryAdd(name, WebUtility.HtmlDecode(value));
            }
        }
        return n;
    }

    // ------------------------------------------------------------------ structure

    /// <summary>Index of the end token that closes the start token at <paramref name="start"/> (or the last token).</summary>
    private static int MatchEnd(List<Token> t, int start)
    {
        var name = t[start].Name;
        var depth = 0;
        for (var k = start; k < t.Count; k++)
        {
            if (t[k].Name != name || t[k].Kind == Kind.Text) continue;
            if (t[k].Kind == Kind.Start && !t[k].SelfClosing && !Void.Contains(name)) depth++;
            else if (t[k].Kind == Kind.End && --depth == 0) return k;
        }
        return t.Count;
    }

    private static (int From, int To, bool Body) ContentRange(List<Token> t)
    {
        int? main = null;
        (int From, int To, int Chars)? best = null;
        for (var k = 0; k < t.Count; k++)
        {
            if (t[k].Kind != Kind.Start) continue;
            if (t[k].Name == "main" && main is null) main = k;
            else if (t[k].Name == "article")
            {
                var end = MatchEnd(t, k);
                var chars = 0;
                for (var m = k; m < end && m < t.Count; m++) if (t[m].Kind == Kind.Text) chars += t[m].Text.Length;
                if (best is null || chars > best.Value.Chars) best = (k, end, chars);
            }
        }
        if (main is { } mi)
        {
            var end = MatchEnd(t, mi);
            var chars = 0;
            for (var m = mi; m < end && m < t.Count; m++) if (t[m].Kind == Kind.Text) chars += t[m].Text.Length;
            if (chars > 200 || best is null) return (mi, end, false);
        }
        if (best is { Chars: > 200 } b) return (b.From, b.To, false);
        var body = t.FindIndex(x => x.Kind == Kind.Start && x.Name == "body");
        return body >= 0 ? (body, MatchEnd(t, body), true) : (0, t.Count, true);
    }

    private static bool IsNoise(Token tok)
    {
        if (tok.Attrs is null) return false;
        if (tok.Attrs.ContainsKey("hidden")) return true;
        if (tok.Attr("aria-hidden") == "true") return true;
        if (tok.Attr("role") is "navigation" or "banner" or "contentinfo" or "complementary" or "search" or "dialog") return true;
        if (tok.Attr("style") is { } style && DisplayNone().IsMatch(style)) return true;
        foreach (var attr in (string?[])[tok.Attr("class"), tok.Attr("id")])
        {
            if (string.IsNullOrEmpty(attr)) continue;
            foreach (var word in attr.ToLowerInvariant().Split([' ', '_', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
                if (NoiseWords.Contains(word) || NoiseWords.Any(w => word.StartsWith(w + "-", StringComparison.Ordinal) || word.EndsWith("-" + w, StringComparison.Ordinal)))
                    return true;
        }
        return false;
    }

    [GeneratedRegex(@"display\s*:\s*none|visibility\s*:\s*hidden", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayNone();

    // ------------------------------------------------------------------ rendering

    public static HtmlPage Convert(string html, Uri? baseUri, bool markdown = true)
    {
        var t = Tokenize(html);
        var title = "";
        for (var k = 0; k < t.Count; k++)
        {
            if (t[k].Kind == Kind.Start && t[k].Name == "title" && k + 1 < t.Count && t[k + 1].Kind == Kind.Text)
            {
                title = Collapse(t[k + 1].Text).Trim();
                break;
            }
            if (t[k].Kind == Kind.Start && t[k].Name == "base" && t[k].Attr("href") is { } b && Uri.TryCreate(baseUri, b, out var bu)) baseUri = bu;
        }
        var (from, to, body) = ContentRange(t);
        var r = new Renderer(baseUri, markdown);
        r.Render(t, from, Math.Min(to + 1, t.Count), body);
        var content = r.Finish();
        if (title.Length == 0 && FirstHeading().Match(content) is { Success: true } h) title = h.Groups[1].Value.Trim();
        return new HtmlPage(title, content);
    }

    [GeneratedRegex(@"^#{1,2} (.+)$", RegexOptions.Multiline)]
    private static partial Regex FirstHeading();

    private static string Collapse(string s) => Whitespace().Replace(s, " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed class Renderer(Uri? baseUri, bool md)
    {
        private readonly StringBuilder _out = new();
        private readonly List<(bool Ordered, int Count)> _lists = [];
        private readonly Stack<(int Start, string? Href)> _links = new();
        private readonly List<List<string>> _rows = [];
        private int _quote;
        private int _pre;
        private int _cellStart = -1;
        private bool _rowHasHeader;
        private bool _tableHeaderDone;
        private int _table;

        public void Render(List<Token> t, int from, int to, bool bodyRoot)
        {
            for (var k = from; k < to; k++)
            {
                var tok = t[k];
                switch (tok.Kind)
                {
                    case Kind.Text:
                        Text(tok.Text);
                        break;
                    case Kind.Start:
                        if (Skip.Contains(tok.Name) || (bodyRoot && tok.Name == "header") || IsNoise(tok))
                        {
                            if (!Void.Contains(tok.Name) && !tok.SelfClosing) k = MatchEnd(t, k);
                            continue;
                        }
                        Open(tok, t, k);
                        break;
                    case Kind.End:
                        Close(tok.Name);
                        break;
                }
            }
        }

        public string Finish()
        {
            var lines = _out.ToString().Replace("\r", "").Split('\n').Select(l => l.TrimEnd());
            var sb = new StringBuilder();
            var blank = 0;
            var fence = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("```", StringComparison.Ordinal)) fence = !fence;
                if (line.Length == 0 && !fence)
                {
                    if (++blank > 1 || sb.Length == 0) continue;
                }
                else blank = 0;
                sb.Append(line).Append('\n');
            }
            return sb.ToString().Trim();
        }

        private bool AtLineStart => _out.Length == 0 || _out[^1] == '\n';

        private void EnsureLine()
        {
            if (!AtLineStart) _out.Append('\n');
        }

        private void EnsureBlock()
        {
            if (_cellStart >= 0) { if (!AtLineStart && _out.Length > _cellStart && _out[^1] != ' ') _out.Append(' '); return; }
            if (_out.Length == 0) return;
            EnsureLine();
            if (_out.Length >= 2 && _out[^2] != '\n' && _lists.Count == 0) _out.Append('\n');
        }

        private string Prefix()
        {
            var sb = new StringBuilder();
            for (var q = 0; q < _quote; q++) sb.Append("> ");
            if (_lists.Count > 0) sb.Append(' ', (_lists.Count - 1) * 2 + 2);
            return sb.ToString();
        }

        private void Write(string s)
        {
            if (s.Length == 0) return;
            if (AtLineStart && _cellStart < 0 && (_quote > 0 || _lists.Count > 0)) _out.Append(Prefix());
            _out.Append(s);
        }

        private void Text(string s)
        {
            if (_pre > 0)
            {
                _out.Append(s.Replace("\r\n", "\n"));
                return;
            }
            s = Collapse(s);
            if (AtLineStart || (_out.Length > 0 && _out[^1] is ' ' or '(' or '[')) s = s.TrimStart();
            Write(s);
        }

        private void Open(Token tok, List<Token> t, int k)
        {
            switch (tok.Name)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    EnsureBlock();
                    if (md && _cellStart < 0) Write(new string('#', tok.Name[1] - '0') + " ");
                    break;
                case "br":
                    if (_cellStart >= 0) _out.Append(' ');
                    else { _out.Append('\n'); }
                    break;
                case "hr":
                    EnsureBlock();
                    Write(md ? "---" : "");
                    EnsureBlock();
                    break;
                case "ul" or "ol":
                    if (_lists.Count == 0) EnsureBlock(); else EnsureLine();
                    _lists.Add((tok.Name == "ol", int.TryParse(tok.Attr("start"), out var st) ? st - 1 : 0));
                    break;
                case "li":
                    EnsureLine();
                    if (_lists.Count == 0) { Write(md ? "- " : "• "); break; }
                    var (ordered, count) = _lists[^1];
                    _lists[^1] = (ordered, count + 1);
                    var indent = new string(' ', (_lists.Count - 1) * 2);
                    for (var q = 0; q < _quote; q++) _out.Append("> ");
                    _out.Append(indent).Append(ordered ? $"{count + 1}. " : md ? "- " : "• ");
                    break;
                case "pre":
                    EnsureBlock();
                    _pre++;
                    if (_pre == 1)
                    {
                        var lang = Language(tok) ?? (k + 1 < t.Count && t[k + 1] is { Kind: Kind.Start, Name: "code" } c ? Language(c) : null);
                        if (md) _out.Append("```").Append(lang ?? "").Append('\n');
                    }
                    break;
                case "code" or "kbd" or "samp" or "tt":
                    if (_pre == 0 && md) Write("`");
                    break;
                case "strong" or "b":
                    if (md && _pre == 0) Write("**");
                    break;
                case "em" or "i" or "cite":
                    if (md && _pre == 0) Write("*");
                    break;
                case "del" or "s" or "strike":
                    if (md && _pre == 0) Write("~~");
                    break;
                case "blockquote":
                    EnsureBlock();
                    _quote++;
                    break;
                case "a":
                    _links.Push((_out.Length, tok.Attr("href")));
                    break;
                case "img":
                    var alt = Collapse(tok.Attr("alt") ?? "").Trim();
                    if (alt.Length > 0 && _pre == 0)
                    {
                        var src = Absolute(tok.Attr("src"));
                        Write(md && src is not null ? $"![{alt}]({src})" : $"[image: {alt}]");
                    }
                    break;
                case "thead" or "tbody" or "tfoot":
                    break; // rows only: a paragraph break here would split the Markdown table
                case "table":
                    EnsureBlock();
                    _table++;
                    if (_table == 1) { _rows.Clear(); _tableHeaderDone = false; }
                    break;
                case "tr":
                    if (_table == 1) { _rows.Add([]); _rowHasHeader = false; }
                    break;
                case "td" or "th":
                    if (_table == 1)
                    {
                        if (tok.Name == "th") _rowHasHeader = true;
                        if (_rows.Count == 0) _rows.Add([]);
                        _cellStart = _out.Length;
                    }
                    break;
                case "dt":
                    EnsureBlock();
                    if (md) Write("**");
                    break;
                case "dd":
                    EnsureLine();
                    Write(": ");
                    break;
                default:
                    if (Block.Contains(tok.Name)) EnsureBlock();
                    break;
            }
        }

        private void Close(string name)
        {
            switch (name)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    EnsureBlock();
                    break;
                case "ul" or "ol":
                    if (_lists.Count > 0) _lists.RemoveAt(_lists.Count - 1);
                    if (_lists.Count == 0) EnsureBlock(); else EnsureLine();
                    break;
                case "li":
                    EnsureLine();
                    break;
                case "pre":
                    if (_pre == 0) break;
                    _pre--;
                    if (_pre == 0)
                    {
                        EnsureLine();
                        if (md) _out.Append("```\n");
                        EnsureBlock();
                    }
                    break;
                case "code" or "kbd" or "samp" or "tt":
                    if (_pre == 0 && md) Write("`");
                    break;
                case "strong" or "b":
                    if (md && _pre == 0) Write("**");
                    break;
                case "em" or "i" or "cite":
                    if (md && _pre == 0) Write("*");
                    break;
                case "del" or "s" or "strike":
                    if (md && _pre == 0) Write("~~");
                    break;
                case "blockquote":
                    if (_quote > 0) _quote--;
                    EnsureBlock();
                    break;
                case "a":
                    if (_links.Count == 0) break;
                    var (start, href) = _links.Pop();
                    if (!md || start > _out.Length) break;
                    var text = _out.ToString(start, _out.Length - start).Trim();
                    var url = Absolute(href);
                    if (text.Length == 0 || url is null || text == url) break;
                    _out.Length = start;
                    if (_out.Length > 0 && !char.IsWhiteSpace(_out[^1]) && _out[^1] is not ('(' or '[')) _out.Append(' ');
                    _out.Append('[').Append(text.Replace("\n", " ")).Append("](").Append(url).Append(')');
                    break;
                case "td" or "th":
                    if (_table == 1 && _cellStart >= 0 && _rows.Count > 0)
                    {
                        var cell = _cellStart <= _out.Length ? _out.ToString(_cellStart, _out.Length - _cellStart) : "";
                        _out.Length = Math.Min(_out.Length, _cellStart);
                        _rows[^1].Add(Collapse(cell).Trim().Replace("|", "\\|"));
                        _cellStart = -1;
                    }
                    break;
                case "tr":
                    if (_table == 1 && _rows.Count > 0 && _rows[^1].Count > 0) WriteRow(_rows[^1], _rowHasHeader);
                    break;
                case "thead" or "tbody" or "tfoot":
                    break;
                case "table":
                    if (_table > 0) _table--;
                    EnsureBlock();
                    break;
                case "dt":
                    if (md) Write("**");
                    EnsureLine();
                    break;
                default:
                    if (Block.Contains(name)) EnsureBlock();
                    break;
            }
        }

        private void WriteRow(List<string> cells, bool header)
        {
            EnsureLine();
            if (!md)
            {
                Write(string.Join("  |  ", cells));
                _out.Append('\n');
                return;
            }
            Write("| " + string.Join(" | ", cells) + " |");
            _out.Append('\n');
            if (!_tableHeaderDone)
            {
                _tableHeaderDone = true;
                // Markdown tables need a header separator after the first row (a real header row or not)
                if (header || _rows.Count == 1) Write("|" + string.Concat(cells.Select(_ => " --- |")) + "\n");
            }
        }

        private string? Absolute(string? href)
        {
            if (string.IsNullOrWhiteSpace(href)) return null;
            href = href.Trim();
            if (href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            if (Uri.TryCreate(href, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https" or "mailto" or "ftp") return abs.ToString();
            if (baseUri is not null && Uri.TryCreate(baseUri, href, out var rel)) return rel.ToString();
            return null;
        }

        private static string? Language(Token tok)
        {
            foreach (var attr in (string?[])[tok.Attr("class"), tok.Attr("data-lang"), tok.Attr("data-language")])
            {
                if (string.IsNullOrEmpty(attr)) continue;
                foreach (var cls in attr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (cls.StartsWith("language-", StringComparison.Ordinal)) return cls[9..];
                    if (cls.StartsWith("lang-", StringComparison.Ordinal)) return cls[5..];
                    if (cls.StartsWith("highlight-source-", StringComparison.Ordinal)) return cls[17..];
                }
                if (attr.IndexOf(' ') < 0 && attr.Length < 20 && !attr.Contains('-')) return tok.Attr("class") is null ? attr : null;
            }
            return null;
        }
    }
}
