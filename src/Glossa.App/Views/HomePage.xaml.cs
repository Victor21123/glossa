using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using Glossa.App.Ai;
using Glossa.App.Theme;
using Glossa.App.ViewModels;
using Glossa.Core.Games;
using Glossa.Core.Library;
using Glossa.Core.Llm;
using Glossa.Core.Study;

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

    /// <summary>«Настройки ИИ», «Сменить режим»: a section of Настройки (its key).</summary>
    public event Action<string>? OpenSettings;

    /// <summary>«Начать учёбу».</summary>
    public event Action? OpenStudy;

    /// <summary>Follows the mode and «Статистика на главной» through the settings model Настройки uses.</summary>
    public void Attach(AppServices services, SettingsViewModel model)
    {
        _services = services;
        DataContext = model;
        model.PropertyChanged += OnModelChanged;
        services.ActivityChanged += OnActivity;
        foreach (var level in Enumerable.Range(0, 5)) Legend.Children.Add(Cell(level, null));
        Refresh();
    }

    public void Detach()
    {
        if (DataContext is SettingsViewModel model) model.PropertyChanged -= OnModelChanged;
        if (_services is { } services) services.ActivityChanged -= OnActivity;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.HomeStats) or nameof(SettingsViewModel.Purpose)
            or nameof(SettingsViewModel.TranslateMode) or nameof(SettingsViewModel.DayGoal) or "") Refresh();
    }

    private int _activityPending;

    /// <summary>
    /// An action counted (any thread, often several at once - a «Весь экран»): the series follows once, and only while
    /// the page is on screen (it refreshes whenever it is shown anyway).
    /// </summary>
    private void OnActivity()
    {
        if (Interlocked.Exchange(ref _activityPending, 1) == 1) return;
        Dispatcher.BeginInvoke(() =>
        {
            Volatile.Write(ref _activityPending, 0);
            if (IsVisible) Refresh();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Everything from the dictionary and the AI as it is now (the page is shown, a word was saved).</summary>
    public void Refresh()
    {
        if (_services is not { } services) return;
        var now = DateTime.UtcNow;
        var today = LibraryStats.Day(now).ToDateTime(TimeOnly.MinValue).ToString("dddd, d MMMM", Russian);
        Today.Text = char.ToUpper(today[0], Russian) + today[1..];
        RefreshAi();

        // «Только перевод» hides the dictionary and study, and with them everything counted from the dictionary.
        // «Только перевод» has no words or study: its figures are the lines translated and the quotes.
        var translate = services.Settings.Purpose == "translate";
        WordsPanel.Visibility = Shown(!translate);
        TranslatePanel.Visibility = Shown(translate);
        StudyPanel.Visibility = Shown(!translate);
        if (translate) TranslateCall.Text = $"{Settings.KeyCaps.Display(services.Settings.Hotkey)} - {TranslateModeName(services.Settings.TranslateMode)}";

        // One series for every mode (decided 2026-09-30): words looked up, study answers and lines translated all count.
        var day = LibraryStats.Day(now);
        var activity = services.Library.ActivityDays(DateOnly.MinValue);
        var goal = Math.Max(1, services.Settings.DayGoal);
        var series = ActivityStreak.Of(activity, day, goal);
        Stats.Visibility = HeatPanel.Visibility = Shown(services.Settings.HomeStats && activity.Count > 0);
        Streak.Text = Days(series.Days);
        Best.Text = Days(Math.Max(series.Best, series.Days));
        StreakNote.Text = TodayNote(series, goal);
        FillHeat(activity, series, day, goal);

        if (translate)
        {
            var quotes = services.Library.ListQuotes();
            ThirdLabel.Text = "ПЕРЕВОДОВ ВСЕГО";
            Lookups.Text = activity.Sum(d => d.Translations).ToString("N0", Russian);
            FourthLabel.Text = "ЦИТАТ";
            Pinned.Text = quotes.Count.ToString("N0", Russian);
            Bars(Languages, quotes.GroupBy(q => q.Language).Select(g => (LanguageName(g.Key), g.Count())).OrderByDescending(x => x.Item2).ToList());
            return;
        }

        var words = services.Library.List();
        var stats = LibraryStats.Of(words, now, (exe, title) => GameProfiles.DisplayName(services.Settings.Games, exe, title));
        FillWords(words, stats);
        ThirdLabel.Text = "ПОИСКОВ ВСЕГО";
        Lookups.Text = stats.Lookups.ToString("N0", Russian);
        FourthLabel.Text = "НЕ МОГУ ЗАПОМНИТЬ";
        Pinned.Text = stats.Pinned.ToString(Russian);
        Bars(Languages, stats.Languages.Select(l => (LanguageName(l.Language), l.Count)).ToList());
    }

    /// <summary>Under the series: how today goes, and the freezes held.</summary>
    private static string TodayNote(ActivityStreak series, int goal)
    {
        var today = series.TodayFull ? "сегодня полный день"
            : series.TodayPoints > 0 ? $"сегодня {series.TodayPoints} из {goal}"
            : series.Days > 0 ? "сегодня пока ничего - серия ждёт"
            : "поиск слова, учёба или перевод начнут серию";
        return series.Freezes > 0 ? $"{today}, заморозок: {series.Freezes}" : today;
    }

    /// <summary>The AI's state and what it runs on (also every few seconds while the window is open).</summary>
    public void RefreshAi()
    {
        if (_services is not { } services || DataContext is not SettingsViewModel model) return;
        var s = services.Settings;
        var ai = s.LocalAi;
        var loaded = services.Ai.Current?.Dictionary is not null;
        AiState.Text = s.DictionaryEngine != "local" ? $"Карточку делает \"{s.DictionaryEngine}\""
            : ai.Mode == "off" ? "Выключен - только справочники"
            : loaded ? "Загружена и готова"
            : !ai.HasModel(ai.Profile) ? $"Модель {Settings.ProfileTile.Absent(ai.Profile)}"
            : !ai.HasRuntime() ? "Движок не скачан"
            : "Выгружена - загрузится при поиске";
        string? missing = s.DictionaryEngine != "local" || ai.Mode == "off" ? null
            : !ai.HasModel(ai.Profile) ? (ai.Profile == "custom" ? "Выбери файл модели в \"Настройках ИИ\"." : "Скачай модель в \"Настройках ИИ\".")
            : !ai.HasRuntime() ? "Скачай движок в \"Настройках ИИ\"."
            : null;
        AiMissing.Content = missing;
        AiMissing.Visibility = Shown(missing is not null);
        AiDot.SetResourceReference(Shape.FillProperty, loaded ? "Good" : "Surface");
        AiDot.SetResourceReference(Shape.StrokeProperty, loaded ? "Good" : "Muted");

        var title = Settings.ProfileTile.Title(ai.Profile);
        AiModel.Text = ai.Profile == "custom" && ai.CustomModel.Length > 0 ? System.IO.Path.GetFileName(ai.CustomModel)
            : char.ToUpper(title[0], Russian) + title[1..];
        AiModel.ToolTip = ai.SingleModel(ai.Profile);
        AiMode.Text = ai.Mode switch { "lowvram" => "Минимум видеопамяти", "off" => "Выключен", _ => "Авто" };
        var runtime = RuntimeCatalog.For(model.Runtime);
        AiEngine.Text = ai.LlamaServerPath.Length > 0 ? "свой llama-server"
            : $"llama.cpp {runtime?.Release}, {(runtime?.Id == "vulkan" ? "Vulkan" : "CUDA")}{(ai.HasRuntime() ? "" : ", не скачан")}";
        var free = AiRouter.FreeVramMb();
        AiVram.Text = free >= 0 ? string.Format(Russian, "свободно {0:0.0} ГБ", free / 1024.0) : "нет данных";
    }

    private static string TranslateModeName(string mode) => mode switch
    {
        "screen" => "Весь экран", "live" => "Живой перевод", _ => "Зона",
    };

    private void FillWords(IReadOnlyList<SavedWord> words, LibraryStats stats)
    {
        Recent.Children.Clear();
        if (words.Count == 0)
        {
            WordsTotal.Text = "Пока пусто";
            WordsNote.Text = $"Наведи курсор на слово в игре и нажми {_services!.Settings.Hotkey} - слово попадёт сюда.";
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

    /// <summary>
    /// Half a year of days with Glossa, a column a week from Monday (ink, not the accent): darker for more actions, the
    /// darkest a full day; a missed day a freeze covered is outlined.
    /// </summary>
    private void FillHeat(IReadOnlyList<DayActivity> activity, ActivityStreak series, DateOnly today, int goal)
    {
        Heat.Children.Clear();
        var byDay = activity.ToDictionary(d => d.Day);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var start = monday.AddDays(-7 * (Weeks - 1));
        for (var row = 0; row < 7; row++)
            for (var week = 0; week < Weeks; week++)
            {
                var day = start.AddDays(week * 7 + row);
                var date = day.ToString("d MMMM", Russian);
                if (day > today) Heat.Children.Add(new Border { Width = 16, Height = 16, Margin = new Thickness(2) });
                else if (series.Frozen.Contains(day)) Heat.Children.Add(Frozen($"{date}: заморозка - серия не прервалась"));
                else
                {
                    var a = byDay.GetValueOrDefault(day);
                    var points = a?.Points ?? 0;
                    Heat.Children.Add(Cell(Level(points, goal), points == 0 ? $"{date}: ничего"
                        : $"{date}: {(points >= goal ? "полный день" : $"{points} из {goal}")} ({What(a!)})"));
                }
            }
        HeatNote.Text = $"самый тёмный - полный день, {goal} {Plural(goal, "действие", "действия", "действий")}; обведён - пропуск, который закрыла заморозка";
    }

    /// <summary>Paler below a full day: a third, two thirds, nearly there; full days are the darkest.</summary>
    private static int Level(int points, int goal) =>
        points <= 0 ? 0 : points >= goal ? 4 : points * 3 < goal ? 1 : points * 3 < goal * 2 ? 2 : 3;

    /// <summary>"8 переводов, 3 поиска, 1 ответ в учёбе".</summary>
    private static string What(DayActivity a)
    {
        var parts = new List<string>();
        if (a.Translations > 0) parts.Add($"{a.Translations} {Plural(a.Translations, "перевод", "перевода", "переводов")}");
        if (a.Lookups > 0) parts.Add($"{a.Lookups} {Plural(a.Lookups, "поиск", "поиска", "поисков")}");
        if (a.Answers > 0) parts.Add($"{a.Answers} {Plural(a.Answers, "ответ", "ответа", "ответов")} в учёбе");
        return string.Join(", ", parts);
    }

    private static Border Frozen(string tip)
    {
        var cell = new Border { Width = 16, Height = 16, Margin = new Thickness(2), CornerRadius = new CornerRadius(3), ToolTip = tip, BorderThickness = new Thickness(1.5) };
        cell.SetResourceReference(Border.BorderBrushProperty, "Ink");
        return cell;
    }

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

    private void OnOpenStudy(object sender, RoutedEventArgs e) => OpenStudy?.Invoke();

    /// <summary>Today's session as «Учёба» would start it, and the days in a row with answers.</summary>
    public void ShowStudy(StudyPlan session, StudyStats stats)
    {
        var n = session.Cards.Count;
        var minutes = Math.Max(1, (int)Math.Ceiling(n * stats.SecondsPerCard / 60.0));
        StudyTotal.Text = n > 0 ? $"{n} {Plural(n, "карточка", "карточки", "карточек")} на сегодня, около {minutes} мин" : "На сегодня всё";
        var parts = new List<string>();
        if (session.Pinned.Count > 0) parts.Add($"{session.Pinned.Count} не могу запомнить");
        if (session.Due.Count > 0) parts.Add($"{session.Due.Count} по расписанию");
        if (session.New.Count > 0) parts.Add($"{session.New.Count} {Plural(session.New.Count, "новое", "новых", "новых")}");
        if (stats.Streak > 0) parts.Add($"{StudyLabels.Days(stats.Streak)} подряд с учёбой");
        StudyNote.Text = parts.Count > 0 ? string.Join(", ", parts) : "Слова для учёбы появятся после поиска в игре.";
        StudyButton.Content = n > 0 ? "Начать учёбу" : "Открыть учёбу";
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string section }) OpenSettings?.Invoke(section);
    }
}
