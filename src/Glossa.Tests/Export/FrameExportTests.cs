using System.Globalization;
using Glossa.Core.Export;
using Glossa.Core.Library;
using Glossa.Core.Ocr;
using SkiaSharp;

namespace Glossa.Tests.Export;

public sealed class FrameExportTests : IDisposable
{
    private readonly TestFolders _folders = new();
    public void Dispose() => _folders.Dispose();

    private static readonly DateTime At = new(2026, 10, 1, 14, 5, 9, DateTimeKind.Local);

    private static readonly char[] Forbidden = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    [Fact]
    public void A_frame_name_is_game_word_and_time()
    {
        Assert.Equal("Disco Elysium_reconsider_2026-10-01_14-05-09.jpg", FrameNames.File("Disco Elysium", "reconsider", At));
    }

    [Theory]
    [InlineData("..\\..\\x")]
    [InlineData("a/b")]
    [InlineData(":*?\"<>|")]
    [InlineData("tab\tand\nnewline")]
    public void Characters_windows_forbids_and_path_parts_cannot_escape_the_folder(string word)
    {
        var name = FrameNames.File("Game", word, At);
        Assert.Equal(name, Path.GetFileName(name));
        Assert.True(name.IndexOfAny(Forbidden) < 0, name);
        Assert.DoesNotContain(name, c => char.IsControl(c));
        Assert.EndsWith("_2026-10-01_14-05-09.jpg", name);
        Assert.NotEqual("..", FrameNames.Part("..", "x"));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("LPT1")]
    [InlineData("con.txt")]
    [InlineData("Aux.tar.gz")]
    public void Device_names_get_a_prefix(string part)
    {
        var safe = FrameNames.Part(part, "x");
        Assert.StartsWith("_", safe);
        Assert.EndsWith(part, safe);
        Assert.Equal("COMET", FrameNames.Part("COMET", "x"));
    }

    [Fact]
    public void Long_names_are_cut_and_trailing_dots_and_spaces_trimmed()
    {
        var part = FrameNames.Part(new string('a', 100) + " ...", "x");
        Assert.True(part.Length <= FrameNames.MaxPart);
        Assert.Equal("a", part[^1..]);

        var cut = FrameNames.Part(new string('b', FrameNames.MaxPart - 1) + ". tail", "x");
        Assert.False(cut.EndsWith('.') || cut.EndsWith(' '));

        // A surrogate pair is never cut in half.
        var emoji = FrameNames.Part(new string('c', FrameNames.MaxPart - 1) + "\U0001F600", "x");
        Assert.DoesNotContain(emoji, char.IsSurrogate);

        Assert.True(FrameNames.File(new string('g', 300), new string('w', 300), At).Length < 150);
    }

    [Fact]
    public void Invisible_and_direction_controls_never_reach_a_file_name()
    {
        // RLO (reverses the extension on screen), zero-width space, isolates, line and paragraph separators, private use.
        var part = FrameNames.Part("a\u202Eb\u200Bc\u2066d\u2069e\u2028f\u2029g\uE000h", "x");
        Assert.Equal("a_b_c_d_e_f_g_h", part);

        var bad = new[] { UnicodeCategory.Format, UnicodeCategory.PrivateUse, UnicodeCategory.LineSeparator, UnicodeCategory.ParagraphSeparator };
        Assert.All(FrameNames.File("g\u202E", "w\u200B", At), c => Assert.DoesNotContain(CharUnicodeInfo.GetUnicodeCategory(c), bad));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    [InlineData("...", "???")]
    public void An_empty_game_or_word_still_gives_a_name(string? game, string? word)
    {
        var name = FrameNames.File(game, word, At);
        Assert.EndsWith("_2026-10-01_14-05-09.jpg", name);
        Assert.True(name.Length > "_2026-10-01_14-05-09.jpg".Length);
        Assert.StartsWith(FrameNames.NoGame + "_" + FrameNames.NoWord + "_", name);
    }

    [Fact]
    public void Taken_names_get_a_number()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a_b_t.jpg", "A_B_T (2).jpg" };
        Assert.Equal("a_b_t (3).jpg", FrameNames.Unique("a_b_t.jpg", taken.Contains));
        Assert.Equal("free.jpg", FrameNames.Unique("free.jpg", taken.Contains));
    }

    private static string Game(string? exe, string? title) => exe is null ? (title ?? "") : "Game-" + exe;

    private static SavedWord Word(string text, string? shot, PixelRect? box, params WordContext[] contexts) => new()
    {
        Language = "en",
        Word = text,
        ShotFile = shot,
        WordBox = box,
        Contexts = contexts,
    };

    private static WordContext Ctx(string? shot, PixelRect? box, int minute, string? exe = "hades") => new()
    {
        ShotFile = shot,
        WordBox = box,
        AppExe = exe,
        CreatedUtc = new DateTime(2026, 10, 1, 10, minute, 0, DateTimeKind.Utc),
    };

    private static readonly PixelRect BoxA = new(10, 10, 50, 30);
    private static readonly PixelRect BoxB = new(60, 10, 90, 30);
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "glossa-data-fake");

    [Fact]
    public void Every_context_with_a_frame_is_one_job_and_a_shared_frame_is_kept_once()
    {
        var words = new[]
        {
            // Two lookups, different scenes; the word's own frame is the newest context's (not a third job).
            Word("alpha", "shots\\a.jpg", BoxA, Ctx("shots\\a.jpg", BoxA, 30), Ctx("shots\\b.jpg", BoxB, 10)),
            // The same scene under another word: a different word, so its own job.
            Word("beta", "shots\\a.jpg", BoxA, Ctx("shots\\a.jpg", BoxA, 30)),
            // One frame with the same box twice: once. The same frame with another box: separate.
            Word("gamma", null, null, Ctx("shots\\c.jpg", BoxA, 5), Ctx("shots\\c.jpg", BoxA, 6), Ctx("shots\\c.jpg", BoxB, 7)),
            // No contexts: the word's own frame.
            Word("delta", "shots\\d.jpg", BoxA),
            // No frame at all.
            Word("epsilon", null, null, Ctx(null, null, 1)),
            // An own frame that no context has is added too.
            Word("zeta", "shots\\z.jpg", BoxB, Ctx("shots\\y.jpg", BoxA, 2)),
        };

        var jobs = FrameJobs.Collect(words, Root, Game, _ => true);

        var sources = jobs.Select(j => Path.GetFileName(j.Source) + (j.Box is { } b ? b.Left : -1)).ToList();
        Assert.Equal(
            ["a.jpg10", "b.jpg60", "a.jpg10", "c.jpg10", "c.jpg60", "d.jpg10", "y.jpg10", "z.jpg60"],
            sources);
        Assert.All(jobs, j => Assert.StartsWith(Path.GetFullPath(Root), j.Source));
        Assert.Equal(jobs.Count, jobs.Select(j => j.Name.ToLowerInvariant()).Distinct().Count());
        Assert.All(jobs, j => Assert.EndsWith(".jpg", j.Name));
        Assert.StartsWith("Game-hades_alpha_", jobs[0].Name);
    }

    [Fact]
    public void The_own_frame_is_added_only_when_no_context_has_that_file_even_with_another_box()
    {
        // The word's box differs by a hair from the context's (floating point): still the same scene, no near-duplicate.
        var near = new PixelRect(10.0000001, 10, 50, 30);
        var word = Word("alpha", "shots\\a.jpg", near, Ctx("shots\\a.jpg", BoxA, 30));

        var jobs = FrameJobs.Collect([word], Root, Game, _ => true);

        Assert.Equal(BoxA, Assert.Single(jobs).Box);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void The_time_in_the_name_is_the_sighting_time_of_the_context_in_local_time(DateTimeKind kind)
    {
        var seen = new DateTime(2026, 6, 30, 23, 59, 58, kind);
        var word = Word("alpha", "shots\\new.jpg", BoxA, new WordContext { ShotFile = "shots\\a.jpg", WordBox = BoxA, AppExe = "hades", CreatedUtc = seen });
        var expected = DateTime.SpecifyKind(seen, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

        var jobs = FrameJobs.Collect([word with { CreatedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }], Root, Game, _ => true);

        Assert.Equal($"Game-hades_alpha_{expected}.jpg", jobs[0].Name);
    }

    [Fact]
    public void The_game_is_the_resolved_name_then_the_window_title_then_the_exe()
    {
        var resolved = new WordContext { ShotFile = "shots\\1.jpg", AppExe = "a.exe", WindowTitle = "Title A", CreatedUtc = DateTime.UtcNow };
        var title = new WordContext { ShotFile = "shots\\2.jpg", AppExe = "b.exe", WindowTitle = "Title B", CreatedUtc = DateTime.UtcNow.AddMinutes(1) };
        var exe = new WordContext { ShotFile = "shots\\3.jpg", AppExe = "c.exe", WindowTitle = "  ", CreatedUtc = DateTime.UtcNow.AddMinutes(2) };
        var word = Word("w", null, null, resolved, title, exe);

        var jobs = FrameJobs.Collect([word], Root, (e, _) => e == "a.exe" ? "Resolved Game" : null, _ => true);

        Assert.StartsWith("Resolved Game_w_", jobs[0].Name);
        Assert.StartsWith("Title B_w_", jobs[1].Name);
        Assert.StartsWith("c.exe_w_", jobs[2].Name);
    }

    [Fact]
    public void Contexts_of_one_word_in_the_same_second_get_a_number()
    {
        var at = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        var word = Word("alpha", null, null,
            new WordContext { ShotFile = "shots\\1.jpg", AppExe = "hades", CreatedUtc = at },
            new WordContext { ShotFile = "shots\\2.jpg", AppExe = "hades", CreatedUtc = at });

        var jobs = FrameJobs.Collect([word], Root, Game, _ => true);

        Assert.Equal(2, jobs.Count);
        Assert.EndsWith(".jpg", jobs[0].Name);
        Assert.EndsWith(" (2).jpg", jobs[1].Name);
        Assert.Equal(jobs[0].Name[..^4] + " (2).jpg", jobs[1].Name);
    }

    [Fact]
    public void A_game_filter_keeps_only_the_contexts_of_that_game()
    {
        var other = Ctx("shots\\o.jpg", BoxA, 4, "other");
        var words = new[]
        {
            // Own frame equals a context of the other game: it must not sneak the other game in.
            Word("alpha", "shots\\o.jpg", BoxA, Ctx("shots\\h.jpg", BoxA, 5), other),
            Word("beta", null, null, other),
            (Word("gamma", "shots\\g.jpg", BoxA) with { AppExe = "other" }),
            (Word("delta", "shots\\d.jpg", BoxA) with { AppExe = "hades" }),
        };
        var filter = FrameJobs.GameFilter(["Game-hades"], [], Game);

        var jobs = FrameJobs.Collect(words, Root, Game, _ => true, filter);

        Assert.Equal(["h.jpg", "d.jpg"], jobs.Select(j => Path.GetFileName(j.Source)));
        Assert.Equal(5, FrameJobs.Collect(words, Root, Game, _ => true).Count); // without the filter every game is exported
    }

    [Fact]
    public void A_game_filter_matches_by_name_exe_title_or_part_of_it()
    {
        var exact = FrameJobs.GameFilter(["hades ii"], [], (e, _) => e == "h.exe" ? "Hades II" : null)!;
        Assert.True(exact("h.exe", "x"));          // resolved name, any case
        Assert.True(exact(null, " Hades II "));    // title
        Assert.True(exact("hades ii", null));      // exe
        Assert.False(exact("other.exe", "Other"));

        var part = FrameJobs.GameFilter([], ["disco"], (_, _) => "Disco Elysium")!;
        Assert.True(part("x.exe", "x"));
        Assert.False(FrameJobs.GameFilter([], ["zzz"], (_, _) => "Disco Elysium")!("x.exe", "x"));

        Assert.Null(FrameJobs.GameFilter([], [], Game));
    }

    [Theory]
    [InlineData("shots\\2026\\10\\a.jpg", true)]
    [InlineData("shots/2026/10/a.JPEG", true)]
    [InlineData("library.db", false)]
    [InlineData("settings.json", false)]
    [InlineData("shots\\..\\library.db", false)]
    [InlineData("shots\\a.png", false)]
    [InlineData("shots\\a.jpg:secret", false)]
    [InlineData("shots\\a.jpg::$DATA", false)]
    [InlineData("quotes\\a.jpg", false)]
    [InlineData("shotsX\\a.jpg", false)]
    [InlineData("shots", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_jpeg_files_inside_the_shots_folder_may_be_read(string? file, bool allowed)
    {
        var resolved = FrameFiles.Resolve(Root, file);
        Assert.Equal(allowed, resolved is not null);
        if (allowed) Assert.StartsWith(Path.Combine(Path.GetFullPath(Root), "shots") + Path.DirectorySeparatorChar, resolved);
    }

    [Fact]
    public void A_path_outside_the_data_folder_is_refused_even_when_absolute()
    {
        Assert.Null(FrameFiles.Resolve(Root, Path.Combine(Path.GetTempPath(), "shots", "a.jpg")));
        Assert.NotNull(FrameFiles.Resolve(Root, Path.Combine(Root, "shots", "a.jpg"))); // the app's own ShotPath form
    }

    [Fact]
    public void A_tampered_row_pointing_at_the_database_is_skipped_by_collect()
    {
        var jobs = FrameJobs.Collect([Word("evil", "library.db", BoxA, Ctx("settings.json", BoxA, 1))], Root, Game, _ => true);
        Assert.Empty(jobs);
    }

    [Fact]
    public void Missing_frames_and_frames_outside_the_data_folder_are_skipped()
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere.jpg");
        var words = new[]
        {
            Word("ok", "shots\\ok.jpg", BoxA),
            Word("gone", "shots\\gone.jpg", BoxA),
            Word("up", "..\\elsewhere.jpg", BoxA),
            Word("abs", outside, BoxA),
            Word("sibling", "..\\glossa-data-fake-2\\x.jpg", BoxA),
        };

        var jobs = FrameJobs.Collect(words, Root, Game, path => !path.EndsWith("gone.jpg"));

        var job = Assert.Single(jobs);
        Assert.EndsWith("ok.jpg", job.Source);
    }

    private static string MakeShot(string folder, int width = 600, int height = 400, string name = "shot.jpg")
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(100, 100, 100));
        var path = Path.Combine(folder, name);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void An_outlined_frame_keeps_its_size_and_rings_the_word()
    {
        var folder = _folders.New();
        var shot = MakeShot(folder);
        var box = new PixelRect(200, 150, 300, 190);

        var jpeg = FrameOutline.Jpeg(shot, box);

        using var result = SKBitmap.Decode(jpeg);
        Assert.Equal(600, result.Width);
        Assert.Equal(400, result.Height);
        // The white ring: 6 px left of the box (scale 1), halfway down it.
        Assert.True(result.GetPixel(194, 170).Red > 200);
        // The word itself and the far corner are untouched.
        Assert.InRange(result.GetPixel(250, 170).Red, 90, 110);
        Assert.InRange(result.GetPixel(10, 10).Red, 90, 110);
        // The saved shot is not locked.
        File.Delete(shot);
    }

    [Fact]
    public void A_wide_frame_gets_a_thicker_ring()
    {
        var folder = _folders.New();
        var shot = MakeShot(folder, 2400, 800);

        var jpeg = FrameOutline.Jpeg(shot, new PixelRect(800, 300, 1000, 380));

        using var result = SKBitmap.Decode(jpeg);
        Assert.Equal(2400, result.Width);
        // Scale 2: the ring is at 800 - 12 = 788 and 6 px wide, so 785 is still white (at scale 1 it would be halo).
        Assert.True(result.GetPixel(785, 340).Red > 200);
    }

    [Fact]
    public void A_frame_without_a_box_is_still_re_encoded_so_metadata_is_not_copied()
    {
        var folder = _folders.New();
        var shot = MakeShot(folder);
        var original = File.ReadAllBytes(shot);
        // An APP1 segment right after the SOI marker, as a camera or tool would leave EXIF.
        var note = System.Text.Encoding.ASCII.GetBytes("SECRET-LOCATION");
        var length = note.Length + 2;
        var withMeta = new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length }
            .Concat(note).Concat(original.Skip(2)).ToArray();
        File.WriteAllBytes(shot, withMeta);

        var jpeg = FrameOutline.Jpeg(shot, null);

        Assert.DoesNotContain("SECRET-LOCATION", System.Text.Encoding.Latin1.GetString(jpeg));
        using var result = SKBitmap.Decode(jpeg);
        Assert.Equal((600, 400), (result.Width, result.Height));
    }

    [Fact]
    public void A_file_that_is_not_a_jpeg_is_refused_with_or_without_a_box()
    {
        var folder = _folders.New();
        var fake = Path.Combine(folder, "library.jpg");
        File.WriteAllText(fake, "SQLite format 3 secret rows");

        Assert.Throws<InvalidDataException>(() => FrameOutline.Jpeg(fake, null));
        Assert.Throws<InvalidDataException>(() => FrameOutline.Jpeg(fake, BoxA));
        Assert.False(FrameFiles.IsUsable(fake));
        Assert.False(FrameFiles.IsUsable(Path.Combine(folder, "missing.jpg")));
        Assert.True(FrameFiles.IsUsable(MakeShot(folder)));
    }

    [Fact]
    public void A_frame_with_a_huge_side_is_refused_before_it_is_decoded()
    {
        var folder = _folders.New();
        var wide = MakeShot(folder, FrameOutline.MaxSide + 1, 1, "wide.jpg");

        Assert.Throws<InvalidDataException>(() => FrameOutline.Jpeg(wide, BoxA));
        FrameOutline.CheckSize(FrameOutline.MaxSide, 1000);
        Assert.Throws<InvalidDataException>(() => FrameOutline.CheckSize(9000, 8000)); // 72 Mpx
        Assert.Throws<InvalidDataException>(() => FrameOutline.CheckSize(0, 10));
    }

    [Fact]
    public void A_symbolic_link_is_not_followed()
    {
        var folder = _folders.New();
        var shot = MakeShot(folder);
        var link = Path.Combine(folder, "link.jpg");
        try { File.CreateSymbolicLink(link, shot); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; } // no privilege to make one

        Assert.False(FrameFiles.IsUsable(link));
        Assert.Throws<InvalidDataException>(() => FrameOutline.Jpeg(link, BoxA));
    }

    [Fact]
    public void Writing_never_overwrites_a_file_in_the_folder()
    {
        var folder = _folders.New();
        var shot = MakeShot(folder);
        var target = Path.Combine(folder, "out");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.jpg"), "precious");
        var jobs = new[]
        {
            new FrameJob(shot, BoxA, "a.jpg"),
            new FrameJob(shot, BoxA, "a.jpg"), // the same name asked twice
            new FrameJob(Path.Combine(folder, "broken.jpg"), BoxA, "c.jpg"), // not there: skipped, the rest go on
            new FrameJob(shot, null, "d.jpg"),
        };
        var reports = new List<(int, int)>();
        var skipped = new List<FrameJob>();

        var result = FrameOutline.WriteAll(jobs, target, new SyncProgress(reports), CancellationToken.None, (job, _) => skipped.Add(job));

        Assert.Equal(new FrameWriteResult(3, 1, false), result);
        Assert.Equal("c.jpg", Assert.Single(skipped).Name);
        Assert.Equal("precious", File.ReadAllText(Path.Combine(target, "a.jpg")));
        Assert.True(File.Exists(Path.Combine(target, "a (2).jpg")));
        Assert.True(File.Exists(Path.Combine(target, "a (3).jpg")));
        Assert.True(File.Exists(Path.Combine(target, "d.jpg")));
        Assert.False(File.Exists(Path.Combine(target, "c.jpg")));
        Assert.Equal((4, 4), reports[^1]);
    }

    [Fact]
    public void One_bad_frame_of_any_kind_does_not_stop_the_rest()
    {
        var folder = _folders.New();
        var good = MakeShot(folder);
        var huge = MakeShot(folder, FrameOutline.MaxSide + 1, 1, "huge.jpg");
        var fake = Path.Combine(folder, "fake.jpg");
        File.WriteAllText(fake, "not an image");
        var target = Path.Combine(folder, "out");

        var result = FrameOutline.WriteAll(
            [new FrameJob(huge, BoxA, "h.jpg"), new FrameJob(fake, BoxA, "f.jpg"), new FrameJob(good, BoxA, "g.jpg")],
            target, null, CancellationToken.None);

        Assert.Equal(new FrameWriteResult(1, 2, false), result);
        Assert.Equal(["g.jpg"], Directory.GetFiles(target).Select(Path.GetFileName));
    }

    [Fact]
    public void Cancelling_returns_what_was_written_and_creates_the_folder()
    {
        var folder = _folders.New();
        var shot = MakeShot(folder);
        var target = Path.Combine(folder, "new", "deeper");
        using var cts = new CancellationTokenSource();
        var jobs = new[] { new FrameJob(shot, BoxA, "a.jpg"), new FrameJob(shot, BoxA, "b.jpg"), new FrameJob(shot, BoxA, "c.jpg") };

        // Cancel from the progress report after the first frame.
        var result = FrameOutline.WriteAll(jobs, target, new SyncProgress([], () => cts.Cancel()), cts.Token);

        Assert.Equal(new FrameWriteResult(1, 0, true), result);
        Assert.Equal(["a.jpg"], Directory.GetFiles(target).Select(Path.GetFileName));
    }

    [Fact]
    public void A_failed_write_leaves_no_partial_file_and_is_not_taken_for_a_taken_name()
    {
        var folder = _folders.New();
        var target = Path.Combine(folder, "out");
        Directory.CreateDirectory(target);

        var ex = Assert.Throws<IOException>(() => FrameOutline.WriteNew(target, "a.jpg", [1, 2, 3, 4], (stream, bytes) =>
        {
            stream.Write(bytes, 0, 2);
            throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        }));

        Assert.Contains("space", ex.Message);
        Assert.Empty(Directory.GetFiles(target)); // neither a.jpg nor "a (2).jpg" left behind
    }

    [Fact]
    public void Only_a_file_exists_error_means_the_name_is_taken()
    {
        Assert.True(FrameOutline.IsNameTaken(new IOException("x", unchecked((int)0x80070050))));
        Assert.True(FrameOutline.IsNameTaken(new IOException("x", unchecked((int)0x800700B7))));
        Assert.False(FrameOutline.IsNameTaken(new IOException("x", unchecked((int)0x80070070)))); // disk full
        Assert.False(FrameOutline.IsNameTaken(new IOException("x")));
    }

    [Fact]
    public void An_existing_name_is_skipped_and_the_other_file_is_kept()
    {
        var folder = _folders.New();
        File.WriteAllText(Path.Combine(folder, "a.jpg"), "mine");

        var name = FrameOutline.WriteNew(folder, "a.jpg", [9], null);

        Assert.Equal("a (2).jpg", name);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(folder, "a.jpg")));
    }

    private sealed class SyncProgress(List<(int, int)> into, Action? each = null) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value)
        {
            into.Add(value);
            each?.Invoke();
        }
    }
}
