using Glossa.Core.Export;
using Glossa.Core.Library;
using Glossa.Core.Ocr;
using Microsoft.Data.Sqlite;
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

    private static readonly byte[] Jpeg = MakeJpeg();

    private static byte[] MakeJpeg()
    {
        using var bitmap = new SKBitmap(16, 16);
        bitmap.Erase(SKColors.Gray);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    private static QuoteFrame Frame(string name, PixelRect? box = null) => new($"quotes/{name}.jpg", Jpeg, box);

    private bool Exists(string name) => File.Exists(Path.Combine(_root, "quotes", name + ".jpg"));

    [Fact]
    public void The_same_line_again_is_the_same_quote_counted_with_its_first_translation_and_frame()
    {
        var id = _store.RecordQuote(Line("I'd reconsider the offer.", "На твоём месте я бы передумал."), Frame("f1", new PixelRect(1, 2, 3, 4)));
        Assert.True(_store.QuoteHasFrame("i'd  reconsider the offer", "game.exe"));
        var again = _store.RecordQuote(Line("i'd  reconsider the offer", "Я бы пересмотрел."), Frame("f2"));

        Assert.Equal(id, again);
        Assert.True(Exists("f1"));
        Assert.False(Exists("f2")); // a quote with a frame takes no other: nothing written for it
        var quote = Assert.Single(_store.ListQuotes());
        Assert.Equal((2, "На твоём месте я бы передумал.", "quotes/f1.jpg"), (quote.Seen, quote.Translation, quote.ShotFile));
        Assert.Equal(new PixelRect(1, 2, 3, 4), quote.Box);

        // Another game's line is its own quote.
        _store.RecordQuote(Line("I'd reconsider the offer.", app: "other.exe"));
        Assert.Equal(2, _store.ListQuotes().Count);
        Assert.False(_store.QuoteHasFrame("I'd reconsider the offer.", "other.exe"));
    }

    [Fact]
    public void A_quote_without_a_frame_gets_one_when_it_comes_again()
    {
        var id = _store.RecordQuote(Line("Hello there."));
        _store.RecordQuote(Line("Hello there."), Frame("later"));
        Assert.Equal("quotes/later.jpg", _store.ListQuotes().Single(q => q.Id == id).ShotFile);
        Assert.True(Exists("later"));
    }

    [Fact]
    public void A_frame_shared_by_paragraphs_goes_only_when_no_quote_shows_it()
    {
        var first = _store.RecordQuote(Line("New game") with { Source = QuoteSource.Screen }, Frame("screen"));
        var second = _store.RecordQuote(Line("Load game") with { Source = QuoteSource.Screen }, Frame("screen"));
        var third = _store.RecordQuote(Line("Options"), Frame("own"));

        Assert.Equal(1, _store.DeleteQuotes([first]));
        Assert.True(Exists("screen"));
        _store.DeleteQuotes([second]);
        Assert.False(Exists("screen"));

        // Clearing frames keeps the quotes.
        Assert.Equal(1, _store.ClearQuoteShots([third]));
        Assert.False(Exists("own"));
        var kept = Assert.Single(_store.ListQuotes());
        Assert.Null(kept.ShotFile);
        Assert.Null(kept.Box);
    }

    [Fact]
    public void Clearing_all_frames_empties_the_folder_and_keeps_the_quotes()
    {
        _store.RecordQuote(Line("One"), Frame("a"));
        _store.RecordQuote(Line("Two"), Frame("a"));
        _store.RecordQuote(Line("Three"), Frame("b"));
        _store.RecordQuote(Line("Four"));
        var stray = Path.Combine(_root, "quotes", "2026", "09", "left-from-before.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllBytes(stray, Jpeg);

        Assert.Equal(3, _store.ClearAllQuoteShots());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "quotes"), "*", SearchOption.AllDirectories));
        Assert.All(_store.ListQuotes(), q => Assert.Null(q.ShotFile));
        Assert.Equal(4, _store.ListQuotes().Count);
    }

    [Fact]
    public void A_frame_written_for_a_quote_that_could_not_be_saved_goes_again()
    {
        Assert.ThrowsAny<SqliteException>(() => _store.RecordQuote(Line("Broken") with { Language = null! }, Frame("orphan")));
        Assert.False(Exists("orphan"));
        Assert.Empty(_store.ListQuotes());
    }

    [Fact]
    public void A_quote_frame_is_the_whole_screen_at_1280_named_by_its_pixels()
    {
        const int w = 2560, h = 1440;
        var bgra = new byte[w * h * 4];
        Array.Fill(bgra, (byte)200);

        var (file, jpeg, scale) = ShotStore.EncodeQuoteFrame(bgra, w, h, w * 4);

        Assert.StartsWith(ShotStore.QuotesFolder + Path.DirectorySeparatorChar, file);
        Assert.Equal(0.5, scale);
        using var decoded = SKBitmap.Decode(jpeg);
        Assert.Equal((1280, 720), (decoded.Width, decoded.Height));
        // The same frame again (another paragraph of it) is the same file.
        Assert.Equal(file, ShotStore.EncodeQuoteFrame(bgra, w, h, w * 4).File);
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
