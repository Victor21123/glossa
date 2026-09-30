using Glossa.Core.Export;
using Glossa.Core.Library;
using Glossa.Core.Ocr;
using SkiaSharp;

namespace Glossa.Tests.Library;

public sealed class QuoteTests : IDisposable
{
    private readonly TestFolders _folders = new();
    private readonly string _root;
    private readonly LibraryStore _store;

    public QuoteTests()
    {
        _root = _folders.New();
        _store = new LibraryStore(Path.Combine(_root, "library.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _folders.Dispose();
    }

    private static Quote Line(string text, string? translation = "перевод", string app = "game.exe") =>
        new() { Language = "en", Text = text, Translation = translation, AppExe = app, WindowTitle = "Night Harbor" };

    [Fact]
    public void The_same_line_again_is_the_same_quote_counted_with_its_first_translation_and_frame()
    {
        var frames = 0;
        (string, PixelRect?)? Frame() => ($"quotes/f{++frames}.jpg", new PixelRect(1, 2, 3, 4));

        var id = _store.RecordQuote(Line("I'd reconsider the offer.", "На твоём месте я бы передумал."), Frame);
        var again = _store.RecordQuote(Line("i'd  reconsider the offer", "Я бы пересмотрел."), Frame);

        Assert.Equal(id, again);
        Assert.Equal(1, frames); // the second time no frame is written at all
        var quote = Assert.Single(_store.ListQuotes());
        Assert.Equal((2, "На твоём месте я бы передумал.", "quotes/f1.jpg"), (quote.Seen, quote.Translation, quote.ShotFile));
        Assert.Equal(new PixelRect(1, 2, 3, 4), quote.Box);

        // Another game's line is its own quote.
        _store.RecordQuote(Line("I'd reconsider the offer.", app: "other.exe"));
        Assert.Equal(2, _store.ListQuotes().Count);
    }

    [Fact]
    public void A_quote_without_a_frame_gets_one_when_it_comes_again()
    {
        var id = _store.RecordQuote(Line("Hello there."));
        _store.RecordQuote(Line("Hello there."), () => ("quotes/later.jpg", null));
        Assert.Equal("quotes/later.jpg", _store.ListQuotes().Single(q => q.Id == id).ShotFile);
    }

    [Fact]
    public void A_frame_shared_by_paragraphs_goes_only_when_no_quote_shows_it()
    {
        (string, PixelRect?)? Shared() => ("quotes/screen.jpg", null);
        var first = _store.RecordQuote(Line("New game") with { Source = QuoteSource.Screen }, Shared);
        var second = _store.RecordQuote(Line("Load game") with { Source = QuoteSource.Screen }, Shared);
        var third = _store.RecordQuote(Line("Options"), () => ("quotes/own.jpg", null));

        Assert.Empty(_store.DeleteQuotes([first]));
        Assert.Equal(new[] { "quotes/screen.jpg" }, _store.DeleteQuotes([second]));

        // Clearing frames keeps the quotes.
        Assert.Equal(new[] { "quotes/own.jpg" }, _store.ClearQuoteShots([third]));
        var kept = Assert.Single(_store.ListQuotes());
        Assert.Null(kept.ShotFile);
        Assert.Null(kept.Box);
    }

    [Fact]
    public void Clearing_all_frames_returns_each_file_once()
    {
        _store.RecordQuote(Line("One"), () => ("quotes/a.jpg", null));
        _store.RecordQuote(Line("Two"), () => ("quotes/a.jpg", null));
        _store.RecordQuote(Line("Three"), () => ("quotes/b.jpg", null));
        _store.RecordQuote(Line("Four"));

        Assert.Equal(new[] { "quotes/a.jpg", "quotes/b.jpg" }, _store.ClearQuoteShots(null).Order());
        Assert.All(_store.ListQuotes(), q => Assert.Null(q.ShotFile));
        Assert.Equal(4, _store.ListQuotes().Count);
    }

    [Fact]
    public void A_quote_frame_is_the_whole_screen_at_1280_in_its_own_folder()
    {
        const int w = 2560, h = 1440;
        var bgra = new byte[w * h * 4];
        Array.Fill(bgra, (byte)200);

        var (file, scale) = ShotStore.SaveQuoteFrame(_root, bgra, w, h, w * 4);

        Assert.StartsWith(ShotStore.QuotesFolder + Path.DirectorySeparatorChar, file);
        Assert.Equal(0.5, scale);
        using var saved = SKBitmap.Decode(Path.Combine(_root, file));
        Assert.Equal((1280, 720), (saved.Width, saved.Height));
        // The same frame again (another paragraph of it) is the same file.
        Assert.Equal(file, ShotStore.SaveQuoteFrame(_root, bgra, w, h, w * 4).File);
    }

    [Fact]
    public void Quotes_export_to_csv()
    {
        _store.RecordQuote(Line("She said \"no\", then left.", "Она сказала \"нет\" и ушла."));
        var csv = Path.Combine(_root, "quotes.csv");

        TextExport.WriteQuotesCsv(csv, _store.ListQuotes());

        var lines = File.ReadAllLines(csv);
        Assert.Equal("language,text,translation,app,seen,first,last", lines[0].TrimStart('﻿'));
        Assert.StartsWith("en,\"She said \"\"no\"\", then left.\",\"Она сказала \"\"нет\"\" и ушла.\",Night Harbor,1,", lines[1]);
    }
}
