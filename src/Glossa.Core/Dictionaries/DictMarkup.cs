using System.Text;

namespace Glossa.Core.Dictionaries;

[Flags]
public enum SpanStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    /// <summary>Comments and notes: shown dimmed.</summary>
    Muted = 8,
    /// <summary>Grammar labels and abbreviations ("n.", "уст.").</summary>
    Label = 16,
    Example = 32,
    /// <summary>Cross-reference to another headword.</summary>
    Ref = 64,
    /// <summary>A translation into the user's language, highlighted.</summary>
    Accent = 128,
}

public sealed record MarkupSpan(string Text, SpanStyle Style);

public sealed record MarkupLine(int Indent, IReadOnlyList<MarkupSpan> Spans)
{
    public string Text => string.Concat(Spans.Select(s => s.Text));
}

/// <summary>
/// The one body format every dictionary is converted to: a small subset of ABBYY DSL. Lines are separated by
/// '\n', leading tabs give the indent (0-3), and the tags are [b] [i] [u] [c] (muted) [p] (label) [ex] [ref]
/// [t] (accent). '\' escapes '[', ']' and '\'. Keeping it DSL-like makes the DSL importer nearly a copy and
/// lets HTML, XDXF and JSON sources map onto the same few styles.
/// </summary>
public static class DictMarkup
{
    private static readonly Dictionary<string, SpanStyle> Tags = new(StringComparer.Ordinal)
    {
        ["b"] = SpanStyle.Bold,
        ["i"] = SpanStyle.Italic,
        ["u"] = SpanStyle.Underline,
        ["c"] = SpanStyle.Muted,
        ["p"] = SpanStyle.Label,
        ["ex"] = SpanStyle.Example,
        ["ref"] = SpanStyle.Ref,
        ["t"] = SpanStyle.Accent,
    };

    public static IReadOnlyList<MarkupLine> Parse(string body)
    {
        var lines = new List<MarkupLine>();
        foreach (var raw in body.Split('\n'))
        {
            var indent = 0;
            while (indent < raw.Length && raw[indent] == '\t') indent++;
            var spans = ParseSpans(raw.AsSpan(indent));
            if (spans.Count > 0) lines.Add(new MarkupLine(Math.Min(indent, 3), spans));
        }
        return lines;
    }

    /// <summary>Text without markup, lines joined by "; " — for AI hints and search snippets.</summary>
    public static string ToPlain(string body, int maxLength = int.MaxValue)
    {
        var sb = new StringBuilder();
        foreach (var line in Parse(body))
        {
            var text = line.Text.Trim().TrimEnd(';', ',').TrimEnd();
            if (text.Length == 0) continue;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(text);
            if (sb.Length >= maxLength) break;
        }
        return sb.Length > maxLength ? sb.ToString(0, maxLength).TrimEnd() + "…" : sb.ToString();
    }

    public static string Escape(string text)
    {
        if (text.IndexOfAny(['[', ']', '\\', '\n', '\t']) < 0) return text;
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '[' or ']' or '\\': sb.Append('\\').Append(c); break;
                case '\n' or '\t': sb.Append(' '); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static List<MarkupSpan> ParseSpans(ReadOnlySpan<char> s)
    {
        var spans = new List<MarkupSpan>();
        var stack = new List<SpanStyle>();
        var style = SpanStyle.None;
        var text = new StringBuilder();

        void Flush()
        {
            if (text.Length == 0) return;
            spans.Add(new MarkupSpan(text.ToString(), style));
            text.Clear();
        }

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                text.Append(s[++i]);
                continue;
            }
            if (c == '[')
            {
                var close = s[i..].IndexOf(']');
                if (close > 1)
                {
                    var tag = s.Slice(i + 1, close - 1);
                    var closing = tag[0] == '/';
                    var name = (closing ? tag[1..] : tag).ToString();
                    if (Tags.TryGetValue(name, out var tagStyle))
                    {
                        Flush();
                        if (closing)
                        {
                            var at = stack.LastIndexOf(tagStyle);
                            if (at >= 0) stack.RemoveAt(at);
                        }
                        else
                        {
                            stack.Add(tagStyle);
                        }
                        style = stack.Aggregate(SpanStyle.None, (a, b) => a | b);
                        i += close;
                        continue;
                    }
                }
            }
            text.Append(c);
        }
        Flush();
        return spans;
    }
}
