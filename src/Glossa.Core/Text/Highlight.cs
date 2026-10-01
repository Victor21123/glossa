using System.Globalization;

namespace Glossa.Core.Text;

/// <summary>A piece of a line: plain text, or the word that is marked.</summary>
public readonly record struct HighlightPart(string Text, bool IsWord);

public static class Highlight
{
    /// <summary>
    /// Splits a line into the text before the word, the word and the text after it (empty parts left out). The offset and
    /// length are UTF-16 code units (<see cref="string"/> indexes). Anything that does not fit the line gives the whole line
    /// unmarked: a negative offset, one past the end, an empty word, a cut inside a surrogate pair or before a combining mark.
    /// </summary>
    public static IReadOnlyList<HighlightPart> Split(string? text, int offset, int length)
    {
        if (string.IsNullOrEmpty(text)) return [];
        if (offset < 0 || length <= 0 || offset > text.Length || length > text.Length - offset
            || CutsInside(text, offset) || CutsInside(text, offset + length))
            return [new(text, false)];
        var parts = new List<HighlightPart>(3);
        if (offset > 0) parts.Add(new(text[..offset], false));
        parts.Add(new(text.Substring(offset, length), true));
        if (offset + length < text.Length) parts.Add(new(text[(offset + length)..], false));
        return parts;
    }

    /// <summary>True when a cut before <paramref name="at"/> would split a surrogate pair or part a mark from its letter.</summary>
    private static bool CutsInside(string text, int at)
    {
        if (at <= 0 || at >= text.Length) return false;
        if (char.IsLowSurrogate(text[at]) && char.IsHighSurrogate(text[at - 1])) return true;
        var category = char.IsHighSurrogate(text[at]) && at + 1 < text.Length
            ? CharUnicodeInfo.GetUnicodeCategory(text, at)
            : CharUnicodeInfo.GetUnicodeCategory(text[at]);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }
}
