using System.Text;

namespace Glossa.Core.Llm;

/// <summary>
/// Drops a reasoning model's thinking from a streamed answer: &lt;think&gt; and &lt;thinking&gt; blocks and the harmony
/// "analysis" channel with harmony's framing around the final answer. Tags may be split between chunks or come several
/// in one; a tail that could be the start of a tag waits for the next chunk.
/// </summary>
public sealed class ThinkFilter
{
    // An opening tag and the tag that ends its block; null: a marker that is only removed.
    private static readonly (string Open, string? Close)[] Tags =
    [
        ("<think>", "</think>"),
        ("<thinking>", "</thinking>"),
        ("<|channel|>analysis<|message|>", "<|end|>"),
        ("<|start|>assistant", null),
        ("<|channel|>final<|message|>", null),
        ("<|return|>", null),
    ];

    private static readonly int LongestTag = Tags.Max(t => t.Open.Length);

    private readonly StringBuilder _pending = new();
    private string? _close;

    /// <summary>The part of the answer that can be shown now.</summary>
    public string Feed(string chunk)
    {
        _pending.Append(chunk);
        return Drain(final: false);
    }

    /// <summary>What was held back, once the stream ends; a thought that never closed is dropped.</summary>
    public string Flush() => Drain(final: true);

    private string Drain(bool final)
    {
        var text = _pending.ToString();
        var shown = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (_close is { } close)
            {
                var end = text.IndexOf(close, i, StringComparison.Ordinal);
                if (end < 0)
                {
                    // Still thinking: drop it, but keep what may be the start of the closing tag.
                    i = final ? text.Length : Math.Max(i, text.Length - (close.Length - 1));
                    break;
                }
                i = end + close.Length;
                _close = null;
                continue;
            }
            var (at, tag) = NextTag(text, i);
            if (tag is not { } found)
            {
                var safe = final ? text.Length : HeldFrom(text, i);
                shown.Append(text, i, safe - i);
                i = safe;
                break;
            }
            shown.Append(text, i, at - i);
            i = at + found.Open.Length;
            _close = found.Close;
        }
        _pending.Remove(0, i);
        if (final)
        {
            _pending.Clear();
            _close = null;
        }
        return shown.ToString();
    }

    private static (int At, (string Open, string? Close)? Tag) NextTag(string text, int from)
    {
        (int At, (string Open, string? Close)? Tag) best = (-1, null);
        foreach (var tag in Tags)
        {
            var at = text.IndexOf(tag.Open, from, StringComparison.Ordinal);
            if (at >= 0 && (best.At < 0 || at < best.At)) best = (at, tag);
        }
        return best;
    }

    /// <summary>Where the held-back tail starts: the end of the text if nothing there could begin a tag.</summary>
    private static int HeldFrom(string text, int from)
    {
        for (var p = Math.Max(from, text.Length - LongestTag + 1); p < text.Length; p++)
        {
            if (text[p] != '<') continue;
            var tail = text.AsSpan(p);
            foreach (var tag in Tags)
                if (tag.Open.AsSpan().StartsWith(tail, StringComparison.Ordinal)) return p;
        }
        return text.Length;
    }
}
