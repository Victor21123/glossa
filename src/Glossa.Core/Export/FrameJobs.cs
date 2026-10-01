using Glossa.Core.Library;
using Glossa.Core.Ocr;

namespace Glossa.Core.Export;

/// <summary>One frame to export: the stored JPEG, where the word is in it, and the file name to give the copy.</summary>
public sealed record FrameJob(string Source, PixelRect? Box, string Name);

public static class FrameJobs
{
    /// <summary>
    /// The frames of the words: every sentence is its own scene, so each context with a frame is a job, plus the word's
    /// own frame when no sentence has that file. One frame with one word box is listed once per word (frames are shared
    /// by content). Only frames inside the data folder's shots folder count (<see cref="FrameFiles.Resolve"/>); a file
    /// that is missing is skipped. Names are unique in the list; the time in a name is when the sentence was met.
    /// </summary>
    /// <param name="gameName">Game name for (exe, window title); null or empty falls back to the title, then the exe.</param>
    /// <param name="exists">File check, replaceable in tests.</param>
    /// <param name="gameFilter">
    /// When the list on screen is filtered by a game, only the sentences of that game (see <see cref="GameFilter"/>);
    /// null exports every sentence.
    /// </param>
    public static IReadOnlyList<FrameJob> Collect(IEnumerable<SavedWord> words, string dataRoot, Func<string?, string?, string?> gameName,
        Func<string, bool> exists, Func<string?, string?, bool>? gameFilter = null)
    {
        var jobs = new List<FrameJob>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var word in words)
        {
            var seen = new HashSet<(string, PixelRect?)>();

            void Add(string? file, PixelRect? box, string? exe, string? title, DateTime createdUtc)
            {
                if (gameFilter is not null && !gameFilter(exe, title)) return;
                if (FrameFiles.Resolve(dataRoot, file) is not { } full) return;
                if (!exists(full) || !seen.Add((full.ToLowerInvariant(), box))) return;
                var game = Blank(gameName(exe, title)) ?? Blank(title) ?? Blank(exe);
                // Stored as UTC; a column read without a kind is UTC too.
                var local = DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc).ToLocalTime();
                var name = FrameNames.Unique(FrameNames.File(game, word.Word, local), names.Contains);
                names.Add(name);
                jobs.Add(new FrameJob(full, box, name));
            }

            foreach (var c in word.Contexts) Add(c.ShotFile, c.WordBox, c.AppExe, c.WindowTitle, c.CreatedUtc);

            // The word's own frame: only for words without a sentence that has the file (older rows). A frame of a
            // sentence the filter dropped is not the word's own either - it would bring the other game back.
            var own = FrameFiles.Resolve(dataRoot, word.ShotFile);
            if (own is not null && !word.Contexts.Any(c => string.Equals(FrameFiles.Resolve(dataRoot, c.ShotFile), own, StringComparison.OrdinalIgnoreCase)))
                Add(word.ShotFile, word.WordBox, word.AppExe, word.WindowTitle, word.CreatedUtc);
        }
        return jobs;
    }

    /// <summary>
    /// The test for "this sentence is of the game the list is filtered by": a name equal (any case) to the game as shown,
    /// the window title or the program; or, for the search box's "app:", containing the text. Null when nothing filters.
    /// </summary>
    public static Func<string?, string?, bool>? GameFilter(IReadOnlyList<string> games, IReadOnlyList<string> containing,
        Func<string?, string?, string?> gameName)
    {
        if (games.Count == 0 && containing.Count == 0) return null;
        return (exe, title) =>
        {
            var shown = gameName(exe, title);
            bool Same(string game) => new[] { shown, title, exe }.Any(s => string.Equals(s?.Trim(), game.Trim(), StringComparison.OrdinalIgnoreCase));
            bool Has(string text) => new[] { shown, title, exe }.Any(s => s?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
            return games.All(Same) && containing.All(Has);
        };
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
