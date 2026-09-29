using System.Globalization;

namespace Glossa.Core.Text;

/// <summary>Russian for numbers on screen: the culture (decimal comma) and noun forms after a number.</summary>
public static class Russian
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>1 день, 2 дня, 5 дней, 11 дней, 21 день.</summary>
    public static string Plural(long n, string one, string few, string many) => (Math.Abs(n) % 10, Math.Abs(n) % 100) switch
    {
        (1, not 11) => one,
        (2 or 3 or 4, not (12 or 13 or 14)) => few,
        _ => many,
    };
}
