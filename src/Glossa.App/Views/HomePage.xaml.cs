using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glossa.App.Theme;
using Glossa.App.ViewModels;
using Glossa.Core.Games;
using Glossa.Core.Library;

namespace Glossa.App.Views;

/// <summary>«Главная»: the dictionary at a glance, its statistics and the companion's place.</summary>
public partial class HomePage : UserControl
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private const int Weeks = 26;
    private AppServices? _services;

    public HomePage() => InitializeComponent();

    /// <summary>«Открыть словарь».</summary>
    public event Action? OpenWords;

    /// <summary>A recent word clicked: the dictionary opens on it (its id).</summary>
    public event Action<string>? OpenWord;

    /// <summary>Follows «Статистика на главной» through the settings model Настройки uses.</summary>
    public void Attach(AppServices services, SettingsViewModel model)
    {
        _services = services;
        DataContext = model;
        model.PropertyChanged += OnModelChanged;
        foreach (var level in Enumerable.Range(0, 5)) Legend.Children.Add(Cell(level, null));
        Refresh();
    }

    public void Detach()
    {
        if (DataContext is SettingsViewModel model) model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.HomeStats) or "") Refresh();
    }

    /// <summary>Everything from the dictionary as it is now (the page is shown, a word was saved).</summary>
    public void Refresh()
    {
        if (_services is not { } services) return;
        var now = DateTime.UtcNow;
        var today = LibraryStats.Day(now).ToDateTime(TimeOnly.MinValue).ToString("dddd, d MMMM", Russian);
        Today.Text = char.ToUpper(today[0], Russian) + today[1..];

        var words = services.Library.List();
        var stats = LibraryStats.Of(words, now, (exe, title) => GameProfiles.DisplayName(services.Settings.Games, exe, title));
        FillWords(words, stats);
        var shown = Shown(services.Settings.HomeStats && words.Count > 0);
        Stats.Visibility = HeatPanel.Visibility = LanguagesPanel.Visibility = shown;
        Streak.Text = Days(stats.Streak);
        Best.Text = Days(stats.BestStreak);
        Lookups.Text = stats.Lookups.ToString("N0", Russian);
        Pinned.Text = stats.Pinned.ToString(Russian);
        FillHeat(stats, LibraryStats.Day(now));
        Bars(Languages, stats.Languages.Select(l => (LanguageName(l.Language), l.Count)).ToList());
    }

    private void FillWords(IReadOnlyList<SavedWord> words, LibraryStats stats)
    {
        Recent.Children.Clear();
        if (words.Count == 0)
        {
            WordsTotal.Text = "Пока пусто";
            WordsNote.Text = $"Наведи курсор на слово в игре и нажми {_services!.Settings.Hotkey} — слово попадёт сюда.";
            return;
        }
        WordsTotal.Text = $"{words.Count.ToString("N0", Russian)} {Plural(words.Count, "слово", "слова", "слов")}";
        WordsNote.Text = stats.AddedThisWeek > 0 ? $"+{stats.AddedThisWeek} за неделю" : "за неделю новых нет";
        foreach (var w in words.OrderByDescending(w => w.LastSeenUtc).Take(10))
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(11, 5, 11, 6),
                Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, ToolTip = w.Translation,
                Child = new TextBlock { Text = w.Headword, FontSize = 14, FontFamily = UiFonts.For(w.Language) },
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Band");
            chip.SetResourceReference(Border.BorderBrushProperty, "Rule");
            var id = w.Id;
            chip.MouseLeftButtonUp += (_, _) => OpenWord?.Invoke(id);
            Recent.Children.Add(chip);
        }
    }

    /// <summary>Half a year of lookups, a column a week from Monday, darker for more words (ink, not the accent).</summary>
    private void FillHeat(LibraryStats stats, DateOnly today)
    {
        Heat.Children.Clear();
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var start = monday.AddDays(-7 * (Weeks - 1));
        for (var row = 0; row < 7; row++)
            for (var week = 0; week < Weeks; week++)
            {
                var day = start.AddDays(week * 7 + row);
                var count = stats.PerDay.GetValueOrDefault(day);
                Heat.Children.Add(day > today ? new Border { Width = 16, Height = 16, Margin = new Thickness(2) }
                    : Cell(Level(count), $"{day.ToString("d MMMM", Russian)}: {count} {Plural(count, "слово", "слова", "слов")}"));
            }
    }

    private static int Level(int count) => count switch { 0 => 0, <= 2 => 1, <= 5 => 2, <= 9 => 3, _ => 4 };

    private static Border Cell(int level, string? tip)
    {
        var cell = new Border { Width = 16, Height = 16, Margin = new Thickness(2), CornerRadius = new CornerRadius(3), ToolTip = tip };
        if (level == 0) cell.SetResourceReference(Border.BackgroundProperty, "Well");
        else
        {
            cell.SetResourceReference(Border.BackgroundProperty, "Ink");
            cell.Opacity = level switch { 1 => 0.25, 2 => 0.45, 3 => 0.7, _ => 1 };
        }
        return cell;
    }

    /// <summary>A label, a bar against the biggest, the count.</summary>
    private static void Bars(StackPanel target, IReadOnlyList<(string Name, int Count)> rows)
    {
        target.Children.Clear();
        var max = rows.Count == 0 ? 1 : rows.Max(r => r.Count);
        foreach (var (name, count) in rows)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            var label = new TextBlock { Text = name, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var bar = new ProgressBar { Maximum = max, Value = count, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            var number = new TextBlock { Text = count.ToString(Russian), FontSize = 13.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(number, 2);
            row.Children.Add(label);
            row.Children.Add(bar);
            row.Children.Add(number);
            target.Children.Add(row);
        }
    }

    private static string LanguageName(string code) => code switch
    {
        "en" => "Английский", "ja" => "Японский", "zh" => "Китайский", "ko" => "Корейский", "ru" => "Русский", _ => code,
    };

    private static string Days(int n) => $"{n} {Plural(n, "день", "дня", "дней")}";

    private static string Plural(int n, string one, string few, string many) => (n % 10, n % 100) switch
    {
        (1, not 11) => one,
        (2 or 3 or 4, not (12 or 13 or 14)) => few,
        _ => many,
    };

    private static Visibility Shown(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private void OnOpenWords(object sender, RoutedEventArgs e) => OpenWords?.Invoke();
}
