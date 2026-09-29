namespace Glossa.Core.Library;

/// <summary>
/// A named group of words. Manual collections hold the words the user put in; a smart one is a saved
/// <see cref="SmartFilter"/> and always shows whatever matches it now.
/// </summary>
public sealed record WordCollection(string Id, string Name, SmartFilter? Filter)
{
    public bool IsSmart => Filter is not null;

    /// <summary>Lists show the name.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// A smart collection: a filter over what Glossa records itself at each lookup (game, register from the AI card,
/// level lists, counts, dates) — nothing is looked up on the internet.
/// </summary>
public sealed record SmartFilter
{
    public string? Language { get; init; }

    /// <summary>Game as shown in the dictionary (window title or program name), case-insensitive.</summary>
    public string? Game { get; init; }

    /// <summary>Register code from the AI card: informal, slang, rude, vulgar, sexual.</summary>
    public string? Register { get; init; }

    /// <summary>Level as the lists give it: B2, N3, HSK 4.</summary>
    public string? Level { get; init; }

    public int? MinLookups { get; init; }

    /// <summary>Only words marked «Не могу запомнить».</summary>
    public bool PinnedOnly { get; init; }

    /// <summary>Met within the last N days.</summary>
    public int? Days { get; init; }

    /// <param name="gameName">How the dictionary names the game of a sentence (program, title) → name: a renamed game
    /// profile still matches the collection made under its new name.</param>
    public bool Matches(SavedWord w, Func<string?, string?, string?>? gameName = null)
    {
        if (Language is { } lang && !string.Equals(w.Language, lang, StringComparison.OrdinalIgnoreCase)) return false;
        if (Register is { } reg && !string.Equals(w.Register, reg, StringComparison.OrdinalIgnoreCase)) return false;
        if (Level is { } level && !string.Equals(w.Level, level, StringComparison.OrdinalIgnoreCase)) return false;
        if (MinLookups is { } min && w.Lookups < min) return false;
        if (PinnedOnly && !w.Pinned) return false;
        if (Days is { } days && w.LastSeenUtc < DateTime.UtcNow.AddDays(-days)) return false;
        if (Game is { } game && !PlayedIn(w, game, gameName)) return false;
        return true;
    }

    private static bool PlayedIn(SavedWord w, string game, Func<string?, string?, string?>? gameName)
    {
        bool Same(string? s) => string.Equals(s?.Trim(), game.Trim(), StringComparison.OrdinalIgnoreCase);
        bool Of(string? exe, string? title) => Same(title) || Same(exe) || (gameName is not null && Same(gameName(exe, title)));
        return w.Contexts.Count > 0
            ? w.Contexts.Any(c => Of(c.AppExe, c.WindowTitle))
            : Of(w.AppExe, w.WindowTitle);
    }
}
