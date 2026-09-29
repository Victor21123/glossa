using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glossa.App.Theme;
using Glossa.Core.Config;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;

namespace Glossa.App.Views;

/// <summary>
/// Design check without a game: <c>Glossa.exe --render-card &lt;folder&gt;</c> draws the card in every preset and
/// theme with sample words to PNG files and exits. Nothing is shown on screen.
/// </summary>
internal static class CardSnapshots
{
    public static void Render(string folder, ThemeManager theme)
    {
        Directory.CreateDirectory(folder);
        foreach (var themeName in new[] { "dark", "light", "disco" })
        {
            theme.Apply(themeName);
            foreach (var preset in new[] { "less", "standard", "more" })
            foreach (var (name, fill) in Samples)
            {
                var vm = new LookupViewModel();
                var popup = new LookupPopup(vm);
                fill(vm);
                popup.ApplyLook(new PopupSettings { Preset = preset }, theme);
                Save(popup, Path.Combine(folder, $"{themeName}-{preset}-{name}.png"));
                popup.Close();
            }
            // Только перевод: «Реплика» (the card) and «Живой перевод» (the subtitle).
            {
                var vm = new LookupViewModel();
                var popup = new LookupPopup(vm);
                vm.BeginTranslation("GARTE, THE CAFETERIA MANAGER - \"Not so fast.\" He points to you. \"You owe me 130 real.\"", "en");
                vm.ContextTranslation = "ГАРТЕ, ЗАВЕДУЮЩИЙ СТОЛОВОЙ — «Не так быстро». Он указывает на тебя. «Ты должен мне 130 реалов».";
                vm.IsBusy = false;
                vm.Timing = "ИИ 0,6 с · gemma26b";
                popup.ApplyLook(new PopupSettings(), theme);
                Save(popup, Path.Combine(folder, $"{themeName}-translation.png"));
                popup.Close();

                var subtitle = new SubtitleOverlay();
                subtitle.Preview("ПЕРЕВОД", "Я иду прямиком к той девке, у которой самая большая грудь.", 620);
                Save(subtitle, Path.Combine(folder, $"{themeName}-subtitle.png"));
                subtitle.Close();
            }

            // «Свой»: narrower, see-through, tinted, its own accent, part of the lines hidden.
            foreach (var (name, fill) in Samples)
            {
                var vm = new LookupViewModel();
                var popup = new LookupPopup(vm);
                fill(vm);
                popup.ApplyLook(new PopupSettings
                {
                    Preset = "custom", Accent = "custom", AccentHue = 20, Tint = true, TintHue = 200,
                    Custom = new CustomCard { Base = "standard", Width = 480, Transparency = 25, Hidden = ["pos", "forms", "synonyms", "footer"] },
                }, theme);
                Save(popup, Path.Combine(folder, $"{themeName}-custom-{name}.png"));
                popup.Close();
            }
        }
    }

    /// <summary>
    /// <c>Glossa.exe --render-main &lt;folder&gt; [&lt;scenes&gt;]</c>: the main window with sample words, dark and light, viewing
    /// and editing. Uses its own data folder inside <paramref name="folder"/>, never the user's library.
    /// </summary>
    public static void RenderMain(string folder, string? scenes, ThemeManager theme)
    {
        Directory.CreateDirectory(folder);
        var data = Path.Combine(folder, "data");
        if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
        Environment.SetEnvironmentVariable("GLOSSA_DATA", data); // before anything reads DataPaths
        Directory.CreateDirectory(Path.Combine(data, "shots"));

        var shot = scenes is not null && File.Exists(Path.Combine(scenes, "en_disco_05_1440.jpg"))
            ? CopyShot(Path.Combine(scenes, "en_disco_05_1440.jpg"), data) : null;
        var library = new Glossa.Core.Library.LibraryStore(Glossa.Core.Config.DataPaths.Library);
        foreach (var (w, fresh) in SampleWords(shot)) library.Record(w, fresh);
        var pinned = library.List().First(w => w.Headword == "俺様");
        library.SetPinned(pinned.Id, true);
        var exam = library.CreateCollection("К экзамену N3", filter: null);
        library.AddToCollection(exam, pinned.Id);
        library.AddToCollection(exam, library.List().First(w => w.Headword == "reconsider").Id);
        library.CreateCollection("Сленг и мат", new Glossa.Core.Library.SmartFilter { Language = "en", Register = null, MinLookups = null });
        library.CreateCollection("Искал 2+ раза", new Glossa.Core.Library.SmartFilter { MinLookups = 2 });

        // «Открыть кадр»: the copy the viewer gets, with the word outlined.
        if (library.List().FirstOrDefault(w => w.ShotFile is not null && w.WordBox is not null) is { } framed)
            File.Copy(FrameExport.Outlined(Path.Combine(data, framed.ShotFile!), framed.WordBox!.Value, Path.Combine(data, "tmp", "frames")),
                Path.Combine(folder, "frame-outlined.jpg"), overwrite: true);

        var log = new Glossa.Core.Logging.FileLogger(Path.Combine(data, "logs"));
        var http = new HttpClient();
        var settings = new AppSettings { GamepadCombo = "LB+RB" };
        settings.Games.AddRange(SampleGames());
        var dictionaries = new Glossa.Core.Dictionaries.DictionaryService(@"D:\GlossaData\dict\packs");
        dictionaries.Reload(settings.Dictionaries.Order, settings.Dictionaries.Disabled);
        var host = new Ai.LlamaServerHost(log, http);
        var keys = new Ai.KeyStore(Path.Combine(data, "keys.json"));
        var services = new AppServices(() => settings, _ => { }, library, keys,
            new Speech.SpeechService(Path.Combine(data, "audio"), () => settings.Speech),
            new Ai.AiRouter(() => settings, host, http, http, _ => null, log), dictionaries, () => null, () => { }, http, http, http, theme, log);
        services.Games = new Games.GameRegistry(() => settings, _ => { }, log);

        // One lookup as the selftest measured it, and a load sample, so the settings panels show real shapes.
        services.SampleLoad = () => new Diagnostics.LoadSample(DateTime.Now, 520, 2700, 1, 1, 13100, 16311);
        services.Report(new Lookup.LookupReport(DateTime.Now, "Alt+Q", "слово «reconsider» · 3,0 с", true, 3.04, "gemma26b",
            new Lookup.LookupStages(18, 362, 364, 372, 1076, 3040)));

        foreach (var themeName in new[] { "dark", "light" })
        {
            theme.Apply(themeName);
            var window = new MainWindow(services);
            SaveWindow(window, Path.Combine(folder, $"home-{themeName}.png"));
            SaveWindow(window, Path.Combine(folder, $"home-{themeName}-min.png"), 1100, 700); // the smallest window
            window.ShowTab(MainTab.Words);
            SaveWindow(window, Path.Combine(folder, $"main-{themeName}.png"));
            var vm = (ViewModels.LibraryViewModel)window.WordsPage.DataContext;
            vm.Selected = vm.Items.First(i => i.Headword == "reconsider");
            vm.BeginEdit();
            SaveWindow(window, Path.Combine(folder, $"main-{themeName}-edit.png"));
            vm.CancelEdit();
            window.OpenAddWord("合体する");
            SaveWindow(window, Path.Combine(folder, $"main-{themeName}-add.png"));
            window.OpenCollection(null);
            window.CollectionKind.SelectedIndex = 1;
            SaveWindow(window, Path.Combine(folder, $"main-{themeName}-collection.png"));
            window.CloseModal();
            foreach (var section in new[] { "card", "keys", "languages", "ai", "sources", "library", "speech", "games", "load", "app" })
            {
                window.ShowTab(MainTab.Settings);
                window.SettingsPage.Show(section);
                SaveWindow(window, Path.Combine(folder, $"settings-{themeName}-{section}.png"));
            }
            window.Close();

            // Карточка слова with «Свой» chosen: its rows open, own accent and tint on.
            settings.Popup = new PopupSettings
            {
                Preset = "custom", Accent = "custom", AccentHue = 20, Tint = true, TintHue = 200,
                Custom = new CustomCard { Width = 480, Transparency = 25, Hidden = ["pos", "forms", "synonyms", "footer"] },
            };
            var custom = new MainWindow(services);
            custom.ShowTab(MainTab.Settings);
            custom.SettingsPage.Show("card");
            SaveWindow(custom, Path.Combine(folder, $"settings-{themeName}-card-custom.png"));
            custom.Close();
            settings.Popup = new PopupSettings();

            // Вызов и клавиши with «Только перевод» chosen: the way of translating and what it does.
            settings.Purpose = "translate";
            settings.TranslateMode = "live";
            var translate = new MainWindow(services);
            SaveWindow(translate, Path.Combine(folder, $"home-{themeName}-translate.png"));
            translate.ShowTab(MainTab.Settings);
            translate.SettingsPage.Show("keys");
            SaveWindow(translate, Path.Combine(folder, $"settings-{themeName}-keys-translate.png"));
            translate.Close();
            settings.Purpose = "dictionary";
            settings.TranslateMode = "line";

            // The still frame as the gamepad sees it, through the real path: the picture recognized as a whole, the
            // words of its biggest block, two steps to the right from the first one.
            if (shot is not null && StillWords(Path.Combine(data, shot), folder) is { } walk)
            {
                var still = new FrozenFrame();
                still.Preview(walk.Picture, walk.Word, Lookup.LookupSessions.PadHint);
                // The sample words that are on this frame get their frames, as over a game.
                var known = new Glossa.Core.Library.KnownWords(library.List());
                still.ShowKnown(walk.Words.All.Select(w => (w.Box, Pinned: known.Find(w.Text)))
                    .Where(m => m.Pinned is not null).Select(m => (m.Box, m.Pinned!.Value)).ToList());
                SaveWindow(still, Path.Combine(folder, $"still-{themeName}.png"), walk.Picture.PixelWidth, walk.Picture.PixelHeight);
                still.Close();
            }

            var tray = new TrayMenu();
            tray.Fill(new TrayState("ИИ выгружена · загрузится при поиске", false, "auto", "Alt+Q", true));
            Save(tray, Path.Combine(folder, $"tray-{themeName}.png"));
            tray.Close();
        }
    }

    private static string CopyShot(string source, string data)
    {
        var rel = Path.Combine("shots", Path.GetFileName(source));
        File.Copy(source, Path.Combine(data, rel), overwrite: true);
        return rel;
    }

    private static IEnumerable<(Glossa.Core.Library.SavedWord Word, bool Fresh)> SampleWords(string? shot)
    {
        var now = DateTime.UtcNow;
        Glossa.Core.Library.SavedWord W(string lang, string word, string tr, string? level, string? pos, string ctx, string ctxTr, string here,
            string game, DateTime at, string? reading = null, string? register = null, string? usageTr = null) => new()
        {
            Language = lang, Word = word, Translation = usageTr ?? tr, Level = level, PartOfSpeech = pos, Reading = reading, Register = register,
            Context = ctx, ContextOffset = ctx.IndexOf(word, StringComparison.Ordinal), ContextTranslation = ctxTr, UsageNote = here,
            WindowTitle = game, CreatedUtc = at, Definition = lang == "en" ? "To think again about a decision and possibly change it." : null,
            DefinitionTranslation = lang == "en" ? "Обдумать решение ещё раз и, возможно, изменить его." : null,
        };
        yield return (W("en", "shit-stained", "испачканный в дерьме", "B1", "прил.", "He looks at his shit-stained Lickra(TM) with a grim expression.",
            "Он с мрачным видом смотрит на свою заляпанную дерьмом Ликру(TM).", "отвращение, брезгливость", "Disco Elysium", now.AddMinutes(-30),
            register: "vulgar") with { ShotFile = shot, WordBox = new PixelRect(941 * 1440 / 1440, 346, 1027, 368) }, true);
        yield return (W("en", "reconsider", "передумать", "B2", "гл.", "You should reconsider your position, mortal.",
            "Тебе стоит пересмотреть свою позицию, смертный.", "Мелиноя угрожает: «одумайся», вежливая форма звучит как предупреждение",
            "Hades II", now.AddDays(-2)), true);
        yield return (W("en", "reconsider", "передумать", "B2", "гл.", "I'd reconsider the offer if I were you.",
            "На твоём месте я бы пересмотрел предложение.", "совет с оттенком угрозы: «подумай ещё раз»", "Disco Elysium", now.AddMinutes(-10),
            usageTr: "пересмотреть") with { ShotFile = shot, WordBox = new PixelRect(941, 346, 1027, 368) }, true);
        yield return (W("ja", "合体する", "сливаться, объединяться", "N4", "гл. suru", "スライムたちが どんどん 合体していく！",
            "Слаймы всё больше и больше сливаются воедино!", "слаймы сливаются в одного большого", "Dragon Quest XI S", now.AddMinutes(-20),
            reading: "がったいする") with { Word = "合体" }, true);
        yield return (W("ja", "俺様", "я (высокомерно)", "N1", "мест.", "俺様に逆らうとは、いい度胸だ。", "Перечить мне — смелости хватает.",
            "надменное «я»: персонаж подчёркивает своё превосходство", "Persona 5 Royal", now.AddHours(-3), reading: "おれさま"), true);
        yield return (W("zh", "房东", "арендодатель", "HSK 3", "сущ.", "房东今天又来催房租了。", "Хозяин квартиры сегодня опять пришёл требовать плату.",
            "хозяин съёмной квартиры", "Love Is All Around", now.AddDays(-1), reading: "fángdōng"), true);
        yield return (W("en", "tsundere", "цундэрэ", null, "сущ.", "She's such a tsundere, it's adorable.", "Она такая цундэрэ, это мило.",
            "характер «колючая снаружи, нежная внутри»", "HuniePop 2", now.AddDays(-1).AddHours(-2), register: "slang"), true);
    }

    private static (BitmapSource Picture, PixelRect Word, Glossa.Core.Text.FrameWords Words)? _walk;

    /// <summary>
    /// Recognizes the scene with the real OCR models and steps over its words as the D-pad would; the words visited go to
    /// still.txt beside the pictures. Null without the models.
    /// </summary>
    private static (BitmapSource Picture, PixelRect Word, Glossa.Core.Text.FrameWords Words)? StillWords(string jpeg, string folder)
    {
        if (_walk is not null) return _walk;
        const string models = @"D:\GlossaData\models\ocr";
        if (!OcrEngine.ModelsPresent(models)) return null;
        var picture = new BitmapImage();
        picture.BeginInit();
        picture.CacheOption = BitmapCacheOption.OnLoad;
        picture.UriSource = new Uri(jpeg);
        picture.EndInit();
        var bgra = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        using var ocr = new OcrEngine(models);
        var page = ocr.RecognizeAsync(pixels, w, h, stride, new PixelRect(0, 0, w, h), OcrModelFamily.CjkLatin, CancellationToken.None)
            .GetAwaiter().GetResult();
        var words = Glossa.Core.Text.FrameWords.Build(page, null);
        if (words.Current is null) return null;
        var visited = new List<string> { words.Current.Text };
        for (var i = 0; i < 2 && words.Move(Glossa.Core.Input.PadButtons.DPadRight); i++) visited.Add(words.Current.Text);
        File.WriteAllText(Path.Combine(folder, "still.txt"),
            $"{page.Lines.Count} lines in {page.Elapsed.TotalMilliseconds:F0} ms; words visited: {string.Join(" → ", visited)}; box {words.Current.Box}");
        return _walk = (picture, words.Current.Box, words);
    }

    /// <summary>Sample profiles for Игры и профили: the games of the sample words, one of them behind an anti-cheat.</summary>
    private static IEnumerable<Glossa.Core.Games.GameProfile> SampleGames()
    {
        var now = DateTime.UtcNow;
        yield return new() { Name = "Persona 5 Royal", FirstTitle = "P5R", Programs = [@"E:\SteamLibrary\steamapps\common\P5R\P5R.exe"],
            Language = "ja", WindowMode = "borderless", CreatedUtc = now.AddDays(-17) };
        yield return new() { Name = "Disco Elysium", FirstTitle = "Disco Elysium", Programs = [@"D:\Games\Disco Elysium\disco.exe"],
            DuringLookup = "frame", WindowMode = "windowed", CreatedUtc = now.AddDays(-9) };
        yield return new() { Name = "Dragon Quest XI S", FirstTitle = "DRAGON QUEST XI S", Programs = [@"D:\Games\DQXIS\DQXIS.exe"],
            Ai = "lowvram", WindowMode = "exclusive", CreatedUtc = now.AddDays(-5) };
        yield return new() { Name = "HuniePop 2", FirstTitle = "HuniePop 2 - Double Date", Programs = [@"D:\Games\HuniePop 2\HuniePop 2.exe"],
            CreatedUtc = now.AddDays(-4) };
        yield return new() { Name = "Hades II", FirstTitle = "Hades II", Programs = [@"D:\Games\Hades II\Ship\Hades2.exe"], CreatedUtc = now.AddDays(-3) };
        yield return new() { Name = "Genshin Impact", FirstTitle = "Genshin Impact", Programs = [@"D:\Games\Genshin Impact\GenshinImpact.exe"],
            AntiCheat = "HoYoProtect", DuringLookup = "pause", CreatedUtc = now.AddDays(-1) };
    }

    internal static void SaveWindow(Window window, string file, int width = 2048, int height = 1152)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        png.Save(stream);
    }

    private static void Save(Window popup, string file)
    {
        var root = (FrameworkElement)popup.Content;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();

        const double scale = 1.25; // the user's 2560×1440 monitor at 125%
        var size = new Size(root.ActualWidth + root.Margin.Left + root.Margin.Right, root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x44, 0x3C)), null, new Rect(size));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(backdrop);
        bitmap.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        png.Save(stream);
    }

    private static readonly (string Name, Action<LookupViewModel> Fill)[] Samples =
    [
        ("en", vm =>
        {
            vm.Language = "en";
            vm.Headword = "shit-stained";
            vm.Level = "B1";
            vm.PartOfSpeech = "прил.";
            vm.Translation = "испачканный в дерьме";
            vm.UsageNote = "отвращение, брезгливость";
            vm.Definition = "Covered or soiled with excrement.";
            vm.DefinitionTranslation = "Покрытый или испачканный экскрементами.";
            vm.Context = "He looks at his shit-stained Lickra(TM) with a grim expression.";
            vm.ContextOffset = 16;
            vm.WordLength = 12;
            vm.ContextTranslation = "Он с мрачным видом смотрит на свою заляпанную дерьмом Ликру(TM).";
            vm.Synonyms = "soiled, filthy, feculent";
            vm.SetDictionaryMark(false);
            vm.IsSaved = true;
            vm.Timing = "ИИ 2,1 с · gemma26b";
        }),
        ("ja", vm =>
        {
            vm.Language = "ja";
            vm.Headword = "合体する";
            vm.Reading = "がったいする";
            vm.Level = "N4";
            vm.PartOfSpeech = "гл. suru";
            vm.Translation = "сливаться, объединяться";
            vm.UsageNote = "слаймы сливаются в одного большого";
            vm.Context = "スライムたちが どんどん 合体していく！";
            vm.ContextOffset = 13;
            vm.WordLength = 2;
            vm.ContextTranslation = "Слаймы всё больше и больше сливаются воедино!";
            vm.Components = [new CardComponent("合", "ごう", "соединять"), new CardComponent("体", "たい", "тело")];
            vm.Dictionaries =
            [
                new DictSectionItem("Warodai", [new DictEntryItem("合体", "がったい", "слияние, объединение; ～する сливаться")]),
            ];
            vm.SetDictionaryMark(true);
            vm.Timing = "ИИ 2,4 с · gemma26b";
        }),
    ];
}
