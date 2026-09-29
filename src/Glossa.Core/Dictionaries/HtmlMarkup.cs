using System.Net;
using System.Text;

namespace Glossa.Core.Dictionaries;

/// <summary>
/// Dictionary HTML (MDX, StarDict "h"), XDXF (StarDict "x") and Pango markup to <see cref="DictMarkup"/>.
/// Keeps what reads well in a small pop-up — lines, indents, bold, italics, labels, examples, links — and drops
/// scripts, styles, media and layout.
/// </summary>
public static class HtmlMarkup
{
    private static readonly HashSet<string> Void = new(StringComparer.Ordinal)
    {
        "br", "hr", "img", "meta", "link", "input", "source", "wbr", "area", "col", "embed", "param", "track",
    };

    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "script", "style", "audio", "video", "object", "rt", "rp", "head", "title", "button", "svg",
    };

    private static readonly HashSet<string> Blocks = new(StringComparer.Ordinal)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "tr", "dt", "dd", "section", "article", "header",
        "footer", "table", "dl", "center", "details", "summary", "def",
    };

    /// <param name="xdxf">XDXF meaning of tags: &lt;tr&gt; is a transcription, &lt;k&gt; repeats the headword.</param>
    /// <param name="newlinesAreBreaks">Source lines are meaningful (Warodai, plain StarDict text).</param>
    public static string Convert(string html, bool xdxf = false, bool newlinesAreBreaks = false)
    {
        var w = new MarkupBuilder();
        var stack = new List<Open>();
        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            var textEnd = lt < 0 ? html.Length : lt;
            if (textEnd > i) Text(w, html[i..textEnd], newlinesAreBreaks);
            if (lt < 0) break;

            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }
            var gt = html.IndexOf('>', lt + 1);
            if (gt < 0)
            {
                Text(w, html[lt..], newlinesAreBreaks);
                break;
            }
            i = gt + 1;

            var tag = html.AsSpan(lt + 1, gt - lt - 1).Trim();
            if (tag.Length == 0 || tag[0] == '!' || tag[0] == '?') continue;
            var closing = tag[0] == '/';
            if (closing) tag = tag[1..].TrimStart();
            var selfClosing = tag.Length > 0 && tag[^1] == '/';
            var nameEnd = 0;
            while (nameEnd < tag.Length && (char.IsLetterOrDigit(tag[nameEnd]) || tag[nameEnd] is '-' or ':')) nameEnd++;
            if (nameEnd == 0) continue;
            var name = tag[..nameEnd].ToString().ToLowerInvariant();
            var attrs = tag[nameEnd..].ToString();

            if (!closing && Skipped.Contains(name) || xdxf && !closing && name is "k" or "rref")
            {
                if (selfClosing) continue;
                var close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                if (close >= 0)
                {
                    var closeEnd = html.IndexOf('>', close);
                    i = closeEnd < 0 ? html.Length : closeEnd + 1;
                }
                continue;
            }

            if (closing)
            {
                var at = stack.FindLastIndex(o => o.Name == name);
                if (at < 0) continue;
                for (var k = stack.Count - 1; k >= at; k--) CloseEffects(w, stack[k]);
                stack.RemoveRange(at, stack.Count - at);
                continue;
            }

            if (name is "br" or "hr") { w.NewLine(); continue; }
            if (Void.Contains(name)) continue;

            var open = OpenEffects(w, name, attrs, xdxf);
            if (!selfClosing) stack.Add(open);
            else CloseEffects(w, open);
        }
        for (var k = stack.Count - 1; k >= 0; k--) CloseEffects(w, stack[k]);
        return w.Finish();
    }

    private sealed record Open(string Name, string? Markup, bool Block, bool Indent, string? Suffix);

    private static Open OpenEffects(MarkupBuilder w, string name, string attrs, bool xdxf)
    {
        string? markup = null;
        string? suffix = null;
        var block = false;
        var indent = false;

        switch (name)
        {
            case "b" or "strong": markup = "b"; break;
            case "i" or "em" or "cite" or "var": markup = "i"; break;
            case "u": markup = "u"; break;
            case "a": markup = attrs.Contains("href", StringComparison.OrdinalIgnoreCase) ? "ref" : null; break;
            case "kref": markup = "ref"; break;
            case "ul" or "ol" or "blockquote": block = true; indent = true; break;
            case "li": block = true; break;
            case "td" or "th": w.Text(" "); break;
            case "abr" or "abbr" or "gr": markup = "p"; break;
            case "c" or "co": markup = "c"; break;
            case "ex": markup = "ex"; break;
            case "tr" when xdxf:
                markup = "c";
                w.Text("[");
                suffix = "]";
                break;
            default:
                if (Blocks.Contains(name)) block = true;
                if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6") markup = "b";
                markup ??= FromClass(attrs);
                break;
        }

        if (block) w.NewLine();
        if (indent) w.Indent++;
        if (name == "li") w.Text("• ");
        if (markup is not null) w.OpenTag(markup);
        return new Open(name, markup, block, indent, suffix);
    }

    private static void CloseEffects(MarkupBuilder w, Open o)
    {
        if (o.Markup is not null) w.CloseTag(o.Markup);
        if (o.Suffix is not null) w.Text(o.Suffix);
        if (o.Block) w.NewLine();
        if (o.Indent) w.Indent = Math.Max(0, w.Indent - 1);
    }

    /// <summary>Common class names in dictionary HTML: examples, grammar labels, comments.</summary>
    private static string? FromClass(string attrs)
    {
        var at = attrs.IndexOf("class", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var q1 = attrs.IndexOfAny(['"', '\''], at);
        if (q1 < 0) return null;
        var q2 = attrs.IndexOf(attrs[q1], q1 + 1);
        if (q2 < 0) return null;
        foreach (var cls in attrs[(q1 + 1)..q2].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (cls.ToLowerInvariant())
            {
                case "ex" or "example" or "examples" or "exm": return "ex";
                case "p" or "pos" or "gram" or "label" or "abbr" or "abr": return "p";
                case "c" or "com" or "comment" or "note": return "c";
            }
        }
        return null;
    }

    private static void Text(MarkupBuilder w, string raw, bool newlinesAreBreaks)
    {
        var text = raw.Contains('&') ? WebUtility.HtmlDecode(raw) : raw;
        if (!newlinesAreBreaks)
        {
            w.Text(text);
            return;
        }
        var parts = text.Split('\n');
        for (var p = 0; p < parts.Length; p++)
        {
            if (p > 0) w.NewLine();
            w.Text(parts[p].TrimEnd('\r'));
        }
    }
}

/// <summary>
/// Writes <see cref="DictMarkup"/> line by line. Open styles are closed at each line end and reopened on the
/// next line, because markup lines are parsed independently.
/// </summary>
internal sealed class MarkupBuilder
{
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _line = new();
    private readonly List<string> _open = [];
    private bool _hasText;
    private bool _space;

    public int Indent { get; set; }

    public void Text(string text)
    {
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) && c != '　')
            {
                _space = _hasText;
                continue;
            }
            FlushSpace();
            if (c is '[' or ']' or '\\') _line.Append('\\');
            _line.Append(c);
            _hasText = true;
        }
    }

    public void OpenTag(string tag)
    {
        FlushSpace();
        _line.Append('[').Append(tag).Append(']');
        _open.Add(tag);
    }

    public void CloseTag(string tag)
    {
        var at = _open.LastIndexOf(tag);
        if (at < 0) return;
        // Close the inner tags too and reopen them, so markup stays properly nested.
        for (var k = _open.Count - 1; k >= at; k--) _line.Append("[/").Append(_open[k]).Append(']');
        var reopen = _open.Skip(at + 1).ToList();
        _open.RemoveRange(at, _open.Count - at);
        foreach (var t in reopen)
        {
            _line.Append('[').Append(t).Append(']');
            _open.Add(t);
        }
    }

    public void NewLine()
    {
        _space = false;
        if (!_hasText)
        {
            // Nothing visible yet: keep only the reopened tags.
            _line.Clear();
            foreach (var t in _open) _line.Append('[').Append(t).Append(']');
            return;
        }
        for (var k = _open.Count - 1; k >= 0; k--) _line.Append("[/").Append(_open[k]).Append(']');
        if (_out.Length > 0) _out.Append('\n');
        _out.Append('\t', Math.Min(Indent, 3)).Append(_line);
        _line.Clear();
        foreach (var t in _open) _line.Append('[').Append(t).Append(']');
        _hasText = false;
    }

    public string Finish()
    {
        NewLine();
        return _out.ToString();
    }

    private void FlushSpace()
    {
        if (!_space) return;
        _line.Append(' ');
        _space = false;
    }
}
