using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glossa.App.Theme;
using Glossa.App.ViewModels;
using Glossa.Core.Config;
using Glossa.Core.Ocr;

namespace Glossa.App.Views.Settings;

public partial class CardSection : UserControl
{
    private readonly AppServices _services;
    private readonly SettingsViewModel _model;
    private readonly LookupViewModel _card = new();
    private BitmapImage? _shot;
    private PixelRect? _box;

    public CardSection(AppServices services, SettingsViewModel model)
    {
        InitializeComponent();
        _services = services;
        _model = model;
        DataContext = model;
        Presets.ItemsSource = PresetTile.All;
        Accents.ItemsSource = AccentTile.All;
        foreach (var (key, name) in CardPartNames)
        {
            var box = new CheckBox { Content = name, IsChecked = model.IsPartShown(key), Margin = new Thickness(0, 0, 22, 12) };
            box.Checked += (_, _) => model.SetPartShown(key, true);
            box.Unchecked += (_, _) => model.SetPartShown(key, false);
            Parts.Children.Add(box);
        }
        Description.Text = $"Как выглядит окно, которое открывается по {model.Hotkey}.";
        Card.DataContext = _card;
        FillPreview();
        ApplyLook(); // Loaded comes later on screen and never off screen (snapshots)

        // The theme manager outlives this page: follow it only while the page is on screen.
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            _services.Theme.Changed += ApplyLook;
            ApplyLook();
        };
        Unloaded += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _services.Theme.Changed -= ApplyLook;
        };
        PreviewBox.SizeChanged += (_, _) => Arrange();
    }

    /// <summary>What «Свой» can hide, in the order the card shows it.</summary>
    private static readonly (string Key, string Name)[] CardPartNames =
    [
        ("reading", "Чтение"), ("pos", "Часть речи"), ("level", "Уровень"), ("scene", "Контекст сцены"), ("definition", "Значение"),
        ("line", "Реплика"), ("lineTranslation", "Перевод реплики"), ("components", "Разбор иероглифов"), ("forms", "Формы"),
        ("synonyms", "Синонимы"), ("dictionaries", "Статьи словарей"), ("footer", "Нижняя строка"),
    ];

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => ApplyLook();

    private void ApplyLook()
    {
        Card.ApplyLook(_services.Settings.Popup, _services.Theme);
        Ring.Stroke = RingBrush(_services.Theme.CardKind(_services.Settings.Popup.Theme), _services.Settings.Popup);
        Dispatcher.BeginInvoke(Arrange, DispatcherPriority.Loaded);
    }

    /// <summary>The user's newest word that has a game frame; a built-in example when there is none yet.</summary>
    private void FillPreview()
    {
        var word = _services.Library.List()
            .Where(w => w.ShotFile is not null && w.WordBox is not null && File.Exists(Path.Combine(DataPaths.Root, w.ShotFile)))
            .OrderByDescending(w => w.LastSeenUtc)
            .FirstOrDefault();
        if (word is null)
        {
            Example(_card);
            PreviewNote.Text = "Пример: в словаре пока нет слов с кадром из игры. \"Как у окна\" повторяет тему Glossa.";
            return;
        }

        _card.Language = word.Language;
        _card.Headword = word.Headword;
        _card.Reading = word.Reading;
        _card.Level = word.Level;
        _card.PartOfSpeech = word.PartOfSpeech;
        _card.Translation = word.Translation;
        _card.UsageNote = word.UsageNote;
        _card.Definition = word.Definition;
        _card.DefinitionTranslation = word.DefinitionTranslation;
        _card.Context = word.Context ?? "";
        _card.ContextOffset = word.ContextOffset;
        _card.WordLength = (word.Contexts.FirstOrDefault()?.Surface ?? word.Word).Length;
        _card.ContextTranslation = word.ContextTranslation;
        _card.Synonyms = word.Synonyms.Count > 0 ? string.Join(", ", word.Synonyms) : null;
        _card.KeyForms = word.KeyForms.Count > 0 ? string.Join(", ", word.KeyForms) : null;
        _card.Components = word.Components;
        _card.IsSaved = true;
        var sections = _services.Dictionaries.Lookup(word.Language, new[] { word.Headword, word.Word }.Distinct().ToList(), word.Reading,
            Math.Max(1, _services.Settings.Dictionaries.EntriesPerDictionary));
        _card.Dictionaries = sections.Select(s => new DictSectionItem(s.Pack.Title,
            s.Entries.Select(e => new DictEntryItem(e.Headword, e.Reading == e.Headword ? null : e.Reading, e.Body)).ToList())).ToList();
        if (_services.RecentLookups.FirstOrDefault(r => r.AiSeconds is not null) is { } last)
            _card.Timing = string.Format(CultureInfo.GetCultureInfo("ru-RU"), "ИИ {0:0.0} с, {1}", last.AiSeconds, last.Model);

        _shot = new BitmapImage();
        _shot.BeginInit();
        _shot.CacheOption = BitmapCacheOption.OnLoad; // do not lock the file
        _shot.UriSource = new Uri(Path.Combine(DataPaths.Root, word.ShotFile!));
        _shot.EndInit();
        _box = word.WordBox;
        Shot.Source = _shot;
        var game = word.WindowTitle ?? word.AppExe;
        PreviewNote.Text = $"Твоё последнее слово{(game is { Length: > 0 } ? " из \"" + game + "\"" : "")}. \"Как у окна\" повторяет тему Glossa.";
    }

    /// <summary>
    /// Puts the frame so the word sits near the top left, rings it, and hangs the card under it the way it opens in
    /// the game (above the word when there is no room below). A card wider than the preview is scaled down.
    /// </summary>
    private void Arrange()
    {
        var w = PreviewBox.ActualWidth - 2;
        var h = PreviewBox.ActualHeight - 2;
        if (w <= 0 || h <= 0) return;
        PreviewBox.Clip = new RectangleGeometry(new Rect(0, 0, w + 2, h + 2), 12, 12);

        Card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var cardW = Card.DesiredSize.Width;
        var cardH = Card.DesiredSize.Height;
        const double shadowLeft = 16, shadowTop = 12;
        double wordX = 34, wordY = 48, wordH = 0;

        if (_shot is { } shot && _box is { } b)
        {
            // Zoomed in on the word, but never smaller than the stage (no empty band under the frame).
            var k = Math.Max(Math.Min(0.6, w / (shot.PixelWidth * 0.55)), Math.Max(w / shot.PixelWidth, h / shot.PixelHeight));
            var iw = shot.PixelWidth * k;
            var ih = shot.PixelHeight * k;
            var left = Math.Min(0, Math.Max(wordX - b.Left * k, w - iw));
            var top = Math.Min(0, Math.Max(wordY - b.Top * k, h - ih));
            Shot.Width = iw;
            Shot.Height = ih;
            Canvas.SetLeft(Shot, left);
            Canvas.SetTop(Shot, top);
            wordX = left + b.Left * k;
            wordY = top + b.Top * k;
            wordH = b.Height * k;
            Ring.Width = b.Width * k + 10;
            Ring.Height = wordH + 8;
            Canvas.SetLeft(Ring, wordX - 5);
            Canvas.SetTop(Ring, wordY - 4);
            Ring.Visibility = Visibility.Visible;
        }
        else
        {
            Ring.Visibility = Visibility.Collapsed;
        }

        // Scaled only when the card itself is wider than the stage; then slid left so it stays whole, as the real
        // card does at the screen edge.
        var visibleW = cardW - 2 * shadowLeft;
        var fit = Math.Min(1, (w - 24) / visibleW);
        var cardX = Math.Clamp(wordX - 5, 12, Math.Max(12, w - 12 - visibleW * fit));
        CardHost.LayoutTransform = fit < 1 ? new ScaleTransform(fit, fit) : Transform.Identity;
        var below = wordY + wordH + 6;
        var above = wordY - 6 - cardH * fit;
        var y = below + cardH * fit - shadowTop * fit <= h || above < 0 ? below : above;
        Canvas.SetLeft(CardHost, cardX - shadowLeft * fit);
        Canvas.SetTop(CardHost, y - shadowTop * fit);
    }

    private static Brush RingBrush(ThemeKind kind, PopupSettings look)
    {
        var value = kind == ThemeKind.Disco ? "0.74 0.14 55"
            : look.Accent == "custom" ? $"0.82 0.14 {look.AccentHue}"
            : AccentTile.Hues.TryGetValue(look.Accent, out var hue) ? $"0.82 0.14 {hue}" : "0.97 0.005 95";
        var brush = new SolidColorBrush(Oklch.Parse(value));
        brush.Freeze();
        return brush;
    }

    /// <summary>The line the mockups use, for an empty dictionary.</summary>
    private static void Example(LookupViewModel vm)
    {
        vm.Language = "en";
        vm.Headword = "reconsider";
        vm.Level = "B2";
        vm.PartOfSpeech = "гл.";
        vm.Translation = "передумать";
        vm.UsageNote = "угроза под видом вежливости: «одумайся»";
        vm.Definition = "To think again about a decision and possibly change it.";
        vm.DefinitionTranslation = "Обдумать решение ещё раз и, возможно, изменить его.";
        vm.Context = "You should reconsider your position, mortal.";
        vm.ContextOffset = 11;
        vm.WordLength = 10;
        vm.ContextTranslation = "Тебе стоит пересмотреть свою позицию, смертный.";
        vm.Synonyms = "rethink, review, think better of";
        vm.SetDictionaryMark(true);
        vm.Timing = "ИИ 2,1 с, gemma26b";
    }
}

/// <summary>A preset tile: name, what it shows, and the sketch's bar lengths.</summary>
public sealed record PresetTile(string Key, string Name, string Description, double Bar1, double L3, double L4)
{
    public static IReadOnlyList<PresetTile> All { get; } =
    [
        new("less", "Меньше", "перевод и реплика; остальное по Tab", 8, 0.8, 0),
        new("standard", "Стандарт", "всё о слове сразу, словари свёрнуты", 6, 0.7, 0.6),
        new("more", "Больше", "плюс статья словаря, разбор, синонимы", 6, 0.9, 0.9),
        new("custom", "Свой", "ширина, прозрачность и что показывать - как хочешь", 7, 0.5, 0.75),
    ];

    public GridLength Line3 => new(L3, GridUnitType.Star);
    public GridLength Line3Rest => new(1 - L3, GridUnitType.Star);
    public GridLength Line4 => new(L4, GridUnitType.Star);
    public GridLength Line4Rest => new(1 - L4, GridUnitType.Star);
    public bool HasLine4 => L4 > 0;
}

/// <summary>An accent swatch; white is the inverse highlight, the others are OKLCH hues.</summary>
public sealed record AccentTile(string Key, string Name, Brush Swatch, Brush? Outline)
{
    public static readonly IReadOnlyDictionary<string, int> Hues = new Dictionary<string, int>
    {
        ["jade"] = 168, ["amber"] = 62, ["sun"] = 95, ["lilac"] = 300,
    };

    public static IReadOnlyList<AccentTile> All { get; } =
    [
        new("white", "Белый", Frozen("0.97 0.005 95"), Frozen("0.8 0.01 95")),
        new("jade", "Нефрит", Frozen("0.74 0.13 168"), null),
        new("amber", "Янтарь", Frozen("0.74 0.13 62"), null),
        new("sun", "Солнце", Frozen("0.74 0.13 95"), null),
        new("lilac", "Сирень", Frozen("0.74 0.13 300"), null),
        new("custom", "Свой", HueWheel(), null),
    ];

    /// <summary>The «Свой» swatch: every hue, as a hint that it can be any.</summary>
    private static Brush HueWheel()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        foreach (var (hue, offset) in new[] { (25, 0.0), (95, 0.25), (168, 0.5), (250, 0.75), (330, 1.0) })
            brush.GradientStops.Add(new GradientStop(Oklch.Parse($"0.74 0.13 {hue}"), offset));
        brush.Freeze();
        return brush;
    }

    private static Brush Frozen(string oklch)
    {
        var brush = new SolidColorBrush(Oklch.Parse(oklch));
        brush.Freeze();
        return brush;
    }
}
