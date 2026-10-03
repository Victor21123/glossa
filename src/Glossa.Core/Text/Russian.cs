using System.Globalization;
using System.Text.RegularExpressions;

namespace Glossa.Core.Text;

/// <summary>Russian on screen: the culture (decimal comma), noun forms after a number, where a line may break.</summary>
public static partial class Russian
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>1 день, 2 дня, 5 дней, 11 дней, 21 день.</summary>
    public static string Plural(long n, string one, string few, string many) => (Math.Abs(n) % 10, Math.Abs(n) % 100) switch
    {
        (1, not 11) => one,
        (2 or 3 or 4, not (12 or 13 or 14)) => few,
        _ => many,
    };

    /// <summary>
    /// Where a line may not break: a number or a word of one or two letters stays with the word after it ("6 карточек",
    /// "в гильдии"), a dash with the word before it - a row never starts with it. The spaces become no-break ones.
    /// </summary>
    public static string NoBreaks(string text) => ShortWord().Replace(text, "$1\u00A0").Replace(" - ", "\u00A0- ");

    [GeneratedRegex(@"(?<=^|\s)(\d+|\p{L}{1,2}) +(?=\S)")]
    private static partial Regex ShortWord();
}
