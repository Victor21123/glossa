namespace Glossa.Core.Text;

public enum Script
{
    Other,
    Latin,
    Cyrillic,
    Kana,
    Han,
    Hangul,
    Digit,
    Punctuation,
    Space,
}

public static class Scripts
{
    public static Script Of(char c)
    {
        if (char.IsWhiteSpace(c)) return Script.Space;
        if (char.IsDigit(c)) return Script.Digit;
        if (c is >= '぀' and <= 'ヿ' or >= 'ㇰ' and <= 'ㇿ' or >= 'ｦ' and <= 'ﾟ' || c == 'ー')
            return Script.Kana;
        if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or >= '豈' and <= '﫿' || c == '々')
            return Script.Han;
        if (c is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏')
            return Script.Hangul;
        if (c is >= 'Ѐ' and <= 'ӿ' or >= 'Ԁ' and <= 'ԯ')
            return Script.Cyrillic;
        if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 'À' and <= 'ɏ' or >= 'Ａ' and <= 'ｚ')
            return Script.Latin;
        if (char.IsPunctuation(c) || char.IsSymbol(c)) return Script.Punctuation;
        return Script.Other;
    }

    public static bool IsCjk(Script s) => s is Script.Kana or Script.Han or Script.Hangul;

    public static bool IsCjk(char c) => IsCjk(Of(c));

    /// <summary>Letters that can belong to a space-delimited word (incl. apostrophes and hyphens inside it).</summary>
    public static bool IsWordChar(char c) =>
        Of(c) is Script.Latin or Script.Cyrillic or Script.Digit || c is '\'' or '’' or '-';

    public static bool ContainsCjk(string s)
    {
        foreach (var c in s) if (IsCjk(c)) return true;
        return false;
    }

    /// <summary>Dominant script of a text sample, ignoring spaces, digits and punctuation.</summary>
    public static Script Dominant(string text)
    {
        Span<int> counts = stackalloc int[Enum.GetValues<Script>().Length];
        foreach (var c in text) counts[(int)Of(c)]++;
        // Any kana means Japanese even when Han characters dominate.
        if (counts[(int)Script.Kana] > 0) return Script.Kana;
        var best = Script.Other;
        var bestCount = 0;
        foreach (var s in new[] { Script.Latin, Script.Cyrillic, Script.Han, Script.Hangul })
        {
            if (counts[(int)s] > bestCount) { best = s; bestCount = counts[(int)s]; }
        }
        return best;
    }

    public static bool IsSentenceEnd(char c) => c is '.' or '!' or '?' or '…' or '。' or '！' or '？' or '｡';
}
