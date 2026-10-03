using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using Glossa.App.Ai;
using Glossa.App.Theme;
using Glossa.App.ViewModels;
using Glossa.Core.Companions;
using Glossa.Core.Games;
using Glossa.Core.Library;
using Glossa.Core.Llm;
using Glossa.Core.Study;

namespace Glossa.App.Views;

/// <summary>«Главная»: the dictionary at a glance, its statistics and the companion's place.</summary>
public partial class HomePage : UserControl
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    // The day map's weeks: half a year at least, a year at most - as many as leave the companion its own width.
    private const int FewestWeeks = 26, MostWeeks = 53, WeekWidth = 20;

    // Round the weeks in their row: the card's border and padding (1 + 24 a side), the weekday labels, the gap after.
    private const double AroundWeeks = 2 * 25 + 26 + 20;
    private int _weeks = FewestWeeks;
    private (IReadOnlyList<DayActivity> Activity, ActivityStreak Series, DateOnly Today, int Goal)? _heat;
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
        services.Ai.StateChanged += OnAiState;
        IsVisibleChanged += OnShown;
        Sprite.MouseLeftButtonUp += (_, e) =>
        {
            if (_shownMood is not { } mood || _shown is not { } c)
                return;
            // Clicked again and again: the companion's own "enough", whatever was clicked (the user, 2026-10-03).
            if (_pokes.Pester(DateTime.UtcNow) && c.Phrases?.ContainsKey(SpeechEvents.Enough) == true)
            {
                Speak(SpeechEvents.Enough);
                return;
            }
            // A place of its own (a flask, a bow, a sword...); not asleep - the sleep is another pose.
            if (mood != Mood.Asleep && Sprite.PixelAt(e.GetPosition(Sprite)) is { } pixel && c.ZoneAt(pixel.X, pixel.Y) is { } zone
                && c.Phrases?.ContainsKey(SpeechEvents.Poke(zone)) == true)
            {
                Speak(SpeechEvents.Poke(zone));
                return;
            }
            Speak(SpeechEvents.Click(mood));
        };
        // The secret word for the admin menu is typed on this page: the window hears the keys wherever the focus is.
        Loaded += (_, _) =>
        {
            if (_wordHooked || Window.GetWindow(this) is not { } window)
                return;
            _wordHooked = true;
            window.PreviewTextInput += OnTyped;
            window.PreviewKeyDown += OnTypedKey;
        };
        // A wider window: more weeks on the day map, the companion's card keeps its width (no empty middle).
        Lower.SizeChanged += (_, _) => FitHeat();
        // The bubble follows the head when the window, the card, the line or the name in the corner changes size.
        BubbleLayer.SizeChanged += (_, _) => PlaceBubble();
        CompanionInfo.SizeChanged += (_, _) => PlaceBubble();
        // The name in Press Start 2P, sharp only at whole screen pixels of its 8-pixel grid (SizeName).
        CompanionName.FontFamily = UiFonts.Pixel;
        ((FrameworkElement)CompanionInfo.Parent).SizeChanged += (_, e) => SizeName(e.NewSize.Width);
        Bubble.SizeChanged += (_, _) => PlaceBubble();
        Sprite.SizeChanged += (_, _) => PlaceBubble();
        foreach (var level in Enumerable.Range(0, 5)) Legend.Children.Add(Cell(level, null));
        Refresh();
    }

    public void Detach()
    {
        if (DataContext is SettingsViewModel model) model.PropertyChanged -= OnModelChanged;
        if (_services is { } services)
        {
            services.ActivityChanged -= OnActivity;
            services.Ai.StateChanged -= OnAiState;
        }
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
        _streakDays = series.Days;
        _todayPoints = series.TodayPoints;
        RefreshCompanion(now, goal, activity);
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

    private Mood? _devMood;
    private bool _devFilling;
    private Companion? _shown;
    private Mood? _shownMood;
    private Mood? _realMood;
    private string? _lastLine;
    private int _streakDays;
    private int _todayPoints;
    private DateTime _greetedUtc = DateTime.MinValue;
    private readonly Random _rng = new();
    private System.Windows.Threading.DispatcherTimer? _bubbleTimer;

    /// <summary>
    /// The companion speaks: a line of its own for the moment (<see cref="SpeechEvents"/>) in the bubble, 4-9 s by its
    /// length, then it fades. Nothing when the character has no line for it.
    /// </summary>
    /// <param name="always">Snapshots: the page is drawn without being shown.</param>
    private void Speak(string moment, bool always = false)
    {
        if (!IsVisible && !always) return; // a line nobody sees is not said
        if (_shown is not { } c || CompanionSpeech.Say(c, moment, _rng, _lastLine, CardsToday() ?? 0, _streakDays) is not { } line)
            return;
        _lastLine = line;
        ++_bubbleLine;
        Bubble.Text = line;
        Bubble.Visibility = Visibility.Visible;
        PlaceBubble();
        Bubble.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        if (_bubbleTimer is null)
        {
            _bubbleTimer = new System.Windows.Threading.DispatcherTimer();
            _bubbleTimer.Tick += (_, _) =>
            {
                _bubbleTimer.Stop();
                var fading = _bubbleLine;
                var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
                // A new line during the fade keeps the bubble.
                fade.Completed += (_, _) =>
                {
                    if (fading == _bubbleLine) Bubble.Visibility = Visibility.Collapsed;
                };
                Bubble.BeginAnimation(OpacityProperty, fade);
            };
        }
        _bubbleTimer.Stop();
        _bubbleTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(2500 + line.Length * 60, 4000, 9000));
        _bubbleTimer.Start();
    }

    /// <summary>
    /// The bubble over the companion's head, as in the user's sketch: the tail's tip a hair over the head, the oval
    /// reaching right from it over the free top of the words' column. The sprite keeps the room above the head
    /// (<see cref="SpeechBubble.Room"/>).
    /// </summary>
    private void PlaceBubble()
    {
        if (Bubble.Visibility != Visibility.Visible || Sprite.HeadTop is not { } head || BubbleLayer.ActualWidth <= 0)
            return;
        var tip = Sprite.TranslatePoint(head, BubbleLayer);
        tip.Y -= 4;
        // The bubble stays left of the name in the corner when they would meet (a narrow window): narrower, then moved.
        var room = BubbleLayer.ActualWidth;
        if (CompanionInfo.ActualWidth > 0 && CompanionInfo.TranslatePoint(new Point(0, CompanionInfo.ActualHeight), BubbleLayer) is var corner
            && corner.Y > tip.Y - SpeechBubble.Room)
            room = Math.Min(room, corner.X - 12);
        Bubble.MaxTextWidth = Math.Clamp(room * 0.5, 160, 300);
        Bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Too wide for the room left of the corner: narrower lines, more of them.
        while (Bubble.DesiredSize.Width > room && Bubble.MaxTextWidth - 20 >= Bubble.MinTextWidth)
        {
            Bubble.MaxTextWidth -= 20;
            Bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        var size = Bubble.DesiredSize;
        var left = Math.Round(Math.Clamp(tip.X - size.Width * 0.35, 0, Math.Max(0, room - size.Width)));
        Canvas.SetLeft(Bubble, left);
        Canvas.SetTop(Bubble, Math.Round(Math.Max(0, tip.Y - size.Height)));
        Bubble.TailX = tip.X - left;
    }

    /// <summary>Snapshots (--render-main): the companion says a line at once, no fade.</summary>
    internal void Say(string moment)
    {
        Speak(moment, always: true);
        Bubble.BeginAnimation(OpacityProperty, null);
        Bubble.Opacity = 1;
    }

    /// <summary>The page shown: a greeting or a reminder (the series in danger, the cards due), once in 10 minutes.</summary>
    private void OnShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || _shown is null || DateTime.UtcNow - _greetedUtc < TimeSpan.FromMinutes(10)) return;
        _greetedUtc = DateTime.UtcNow;
        Speak(CompanionSpeech.OnOpen(DateTime.Now, _todayPoints, _streakDays, CardsToday() ?? 0,
            _services?.Settings.Purpose == "translate"));
    }
    private string? _missingLogged;
    private int? _cardsLeft;
    private DateOnly _cardsDay;
    private int _bubbleLine;

    /// <summary>Today's cards left, as «Учёба» counted them; null before it did, or when its count is yesterday's.</summary>
    private int? CardsToday() => _cardsDay == LibraryStats.Day(DateTime.UtcNow) ? _cardsLeft : null;

    /// <summary>
    /// The companion: met on the first visit, with today's mood (and why) and the days together. In every mode and
    /// with the statistics off - it lives on the series of days.
    /// </summary>
    private void RefreshCompanion(DateTime now, int goal, IReadOnlyList<DayActivity> activity)
    {
        // The day's study done (the dictionary mode): no cards left and answers given today - happy before a full day.
        var day = LibraryStats.Day(now);
        var studyDone = _cardsDay == day && _cardsLeft == 0 && _services?.Settings.Purpose != "translate"
                        && activity.Where(d => d.Day == day).Sum(d => d.Answers) > 0;
        if (_services?.Companions is { } away && away.Away(now) is { } gone)
        {
            ShowAway(away, gone);
            return;
        }
        if (_services?.Companions is not { } keeper || keeper.View(now, goal, activity, studyDone) is not { } view)
        {
            if (_services?.Companions?.MissingArt() is { } missing && missing != _missingLogged)
                _services.Log.Warn($"Companion {missing}: its art is not beside the program, the card is hidden (the companion is kept)");
            _missingLogged = _services?.Companions?.MissingArt();
            CompanionPanel.Visibility = Visibility.Collapsed;
            return;
        }
        if (!AdminOn) _devMood = null; // the menu closed, the preview goes with it
        var c = view.Companion;
        var feel = _devMood is { } preview ? new MoodNow(preview, MoodReason.None) : new MoodNow(view.Mood, view.Reason);
        try
        {
            Sprite.Silhouette = false;
            Sprite.Show(c, feel.Mood, keeper.Catalog.TallestHeight);
        }
        catch (InvalidOperationException ex)
        {
            _services.Log.Warn(ex.Message);
            CompanionPanel.Visibility = Visibility.Collapsed;
            return;
        }
        CompanionPanel.Visibility = Visibility.Visible;
        // The mood changed while the page was open: the companion says so (waking up has its own line). The real mood
        // only: the owner's preview coming and going is no change.
        var changed = _shown?.Id == c.Id && _devMood is null ? CompanionSpeech.OnMoodChange(_realMood, view.Mood) : null;
        _shown = c;
        _shownMood = feel.Mood;
        _realMood = view.Mood;
        if (changed is not null) Speak(changed);
        CompanionName.Text = c.Name;
        CompanionWho.Text = c.Game; // the rarity is not shown (user, 2026-10-03): only the owner's test strip names it
        CompanionMood.Text = CompanionLabels.Mood(feel, c.Gender);
        CompanionDays.Text = CompanionLabels.Together(view.DaysTogether);
        RefreshDev(keeper, c);
    }

    /// <summary>
    /// The companion away for its week, caught forged (the user, 2026-10-03): its grey silhouette stays, its last words
    /// the minute it left, and the day it comes back - "это ещё не конец, а только начало".
    /// </summary>
    private void ShowAway(CompanionKeeper keeper, CompanionAway away)
    {
        var c = away.Companion;
        try
        {
            Sprite.Silhouette = true;
            Sprite.Show(c, Mood.Sad, keeper.Catalog.TallestHeight);
        }
        catch (InvalidOperationException ex)
        {
            _services!.Log.Warn(ex.Message);
            CompanionPanel.Visibility = Visibility.Collapsed;
            return;
        }
        CompanionPanel.Visibility = Visibility.Visible;
        _shown = c;
        _shownMood = null; // no clicks, no moods while it is away
        _realMood = null;
        if (away.JustLeft && !_saidGoodbye)
        {
            _saidGoodbye = true;
            Speak(SpeechEvents.Leave);
        }
        CompanionName.Text = c.Name;
        CompanionWho.Text = c.Game;
        CompanionMood.Text = CompanionLabels.Left(c.Gender);
        CompanionDays.Text = CompanionLabels.ComesBack(away.DaysLeft);
        RefreshDev(keeper, c);
    }

    private bool _saidGoodbye;
    private readonly CompanionPokes _pokes = new();
    private bool _wordHooked;

    /// <summary>
    /// The admin menu (the owner's strip: any companion, a test roll, a mood preview, the roll log): opened for this
    /// session by the secret word typed on this page (<see cref="OnTypedKey"/>); in a debug build also by
    /// <c>companions.dev</c> in the data folder.
    /// </summary>
    private bool AdminOn =>
        _adminOpen
#if DEBUG
        || File.Exists(Glossa.Core.Config.DataPaths.CompanionsDev)
#endif
        ;

    private bool _adminOpen;
    private readonly System.Text.StringBuilder _typed = new();
    private DateTime _typedUtc;
    private int _wrongWords;
    private DateTime _wordsLockedUtc;

    /// <summary>
    /// The letters typed on this page: kept only a few seconds, never written anywhere. A text field's typing is
    /// left alone.
    /// </summary>
    private void OnTyped(object sender, TextCompositionEventArgs e)
    {
        if (!IsVisible || Glossa.Core.Companions.CompanionSecrets.Word is null || e.OriginalSource is TextBox or PasswordBox)
            return;
        if (DateTime.UtcNow - _typedUtc > TimeSpan.FromSeconds(5))
            _typed.Clear();
        _typedUtc = DateTime.UtcNow;
        if (_typed.Length < 128)
            _typed.Append(e.Text);
    }

    /// <summary>
    /// Enter after typing on this page: the letters are checked against the secret word (a slow hash, off the UI
    /// thread). The right word opens the admin menu; a wrong one shows nothing, and five wrong in a row rest the check
    /// for a minute. The word itself is in no file, no log and no build - only its hash (glossa-cli secrets word).
    /// </summary>
    private async void OnTypedKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !IsVisible || _typed.Length == 0 || Glossa.Core.Companions.CompanionSecrets.Word is not { } word)
            return;
        if (DateTime.UtcNow - _typedUtc > TimeSpan.FromSeconds(5))
        {
            _typed.Clear(); // letters from long ago are no word, and this Enter is the button's
            return;
        }
        e.Handled = true; // the letters were for the word: Enter does not press a focused button
        var typed = _typed.ToString();
        _typed.Clear();
        if (DateTime.UtcNow < _wordsLockedUtc || _checkingWord)
            return; // one check at a time: Enter held down does not run hashes side by side
        _checkingWord = true;
        try
        {
            if (!await Task.Run(() => word.Matches(typed)))
            {
                if (++_wrongWords >= 5)
                {
                    _wrongWords = 0;
                    _wordsLockedUtc = DateTime.UtcNow.AddMinutes(1);
                }
                return;
            }
            _wrongWords = 0;
            _adminOpen = true;
            _services?.Log.Info("Companions: the admin menu is open for this session");
            Refresh();
        }
        catch (Exception ex)
        {
            _services?.Log.Error("Companions: the secret word check", ex);
        }
        finally
        {
            _checkingWord = false;
        }
    }

    private bool _checkingWord;

    /// <summary>The admin menu's strip, only while it is open (<see cref="AdminOn"/>).</summary>
    private void RefreshDev(CompanionKeeper keeper, Companion current)
    {
        var on = AdminOn;
        CompanionDev.Visibility = Shown(on);
        if (!on) return;
        _devFilling = true;
        if (DevCompanion.Items.Count == 0)
        {
            foreach (var c in keeper.Catalog.All)
                DevCompanion.Items.Add(new ComboBoxItem { Content = $"{c.Name} - {CompanionLabels.Rarity(c.Rarity, c.Gender)}", Tag = c.Id });
            DevMood.Items.Add(new ComboBoxItem { Content = "Настроение: как есть", Tag = null });
            foreach (var (m, name) in new[] { (Mood.Calm, "Спокойствие"), (Mood.Happy, "Радость"), (Mood.Sad, "Грусть"),
                         (Mood.Tired, "Усталость"), (Mood.Asleep, "Сон") })
                DevMood.Items.Add(new ComboBoxItem { Content = name, Tag = m });
            DevMood.SelectedIndex = 0;
        }
        DevCompanion.SelectedItem = DevCompanion.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current.Id);
        DevLog.Text = string.Join("\n", _services!.Library.RollLog(5).Select(r =>
            $"{r.RolledUtc.ToLocalTime():dd.MM HH:mm} {r.Reason}: {r.CompanionId}, {r.Rarity} ({r.RarityRoll.ToString("0.000", Russian)})"));
        _devFilling = false;
    }

    private void OnDevMood(object sender, SelectionChangedEventArgs e)
    {
        if (_devFilling || !AdminOn) return;
        var before = _shownMood;
        _devMood = (DevMood.SelectedItem as ComboBoxItem)?.Tag as Mood?;
        Refresh();
        // The preview speaks as a real change would, to hear the line.
        if (_devMood is { } preview && CompanionSpeech.OnMoodChange(before, preview) is { } moment) Speak(moment);
    }

    private void OnDevPick(object sender, RoutedEventArgs e)
    {
        // The strip only hides without the word; its buttons check it too (an inspector can show a hidden panel).
        if (!AdminOn || _services?.Companions is not { } keeper || (DevCompanion.SelectedItem as ComboBoxItem)?.Tag is not string id) return;
        keeper.Pick(id, DateTime.UtcNow);
        Refresh();
    }

#if DEBUG
    /// <summary>Snapshots (--render-main): the admin menu open or closed without the word.</summary>
    internal void ShowAdmin(bool open)
    {
        _adminOpen = open;
        Refresh();
    }
#endif

    /// <summary>
    /// The pixel name's size in whole screen pixels of its grid: 24 (32 from 150%), 16 in a narrow card (the smallest
    /// window) so a long name takes two lines, not a column of words. Lines a half apart: the font has no leading.
    /// </summary>
    protected override void OnDpiChanged(System.Windows.DpiScale oldDpi, System.Windows.DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        FitHeat(); // the companion's 2x width in points changes with the monitor's scale
        SizeName(((FrameworkElement)CompanionInfo.Parent).ActualWidth); // another monitor: whole pixels again
    }

    private void SizeName(double width)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        var pixels = width < 200 ? 16 : dpi >= 1.5 ? 32 : 24;
        CompanionName.FontSize = pixels / dpi;
        CompanionName.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        CompanionName.LineHeight = Math.Round(pixels * 1.5) / dpi;
    }

    /// <summary>"Закрыть": the admin menu hides until the word is typed again (the user, 2026-10-03).</summary>
    private void OnDevClose(object sender, RoutedEventArgs e)
    {
        _adminOpen = false;
        _devMood = null;
        _devFilling = true;
        DevMood.SelectedIndex = 0;
        _devFilling = false;
        Refresh();
    }

    private void OnDevRoll(object sender, RoutedEventArgs e)
    {
        if (!AdminOn || _services?.Companions is not { } keeper) return;
        keeper.Roll(CompanionKeeper.Dev, DateTime.UtcNow);
        Refresh();
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
        AiMissing.Content = missing ?? _aiError;
        AiMissing.Visibility = Shown(missing is not null || _aiError is not null);
        var router = services.Ai;
        if (loaded && router.UnloadPending) AiState.Text = "Выгрузится, как только закончит поиск";
        else if (loaded && router.Pinned) AiState.Text = "Загружена и держится в фоне";
        else if (router.Loading) AiState.Text = "Загружается...";
        AiLoad.Visibility = Shown(missing is null && s.DictionaryEngine == "local" && ai.Mode != "off");
        AiLoad.IsEnabled = !router.Loading && !router.UnloadPending;
        AiLoad.Content = router.Loading ? "Загружается..." : loaded ? "Выгрузить" : "Загрузить в фон";
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

    private string? _aiError;

    /// <summary>"Загрузить в фон" or, once loaded, "Выгрузить".</summary>
    private async void OnAiLoad(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || services.Ai.Loading) return;
        _aiError = null;
        var unload = services.Ai.Current?.Dictionary is not null;
        try
        {
            if (unload) services.Ai.UnloadNow(); // busy with a lookup: it leaves after it (UnloadPending)
            else await services.Ai.PreloadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A lookup would say the same: the model or the engine is missing, the server did not start.
            services.Log.Warn($"AI: {(unload ? "unload" : "load")} by hand failed: {ex.Message}");
            _aiError = ex is LlmException ? ex.Message
                : unload ? "Модель не выгрузилась - подробности в журнале." : "Модель не загрузилась - подробности в журнале.";
        }
        try
        {
            RefreshAi();
        }
        catch (Exception ex)
        {
            services.Log.Error("AI state", ex);
        }
    }

    private void OnAiState() => Dispatcher.BeginInvoke(RefreshAi);

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
    /// The weeks the row has room for: what is left after the companion's card (<see cref="CompanionNeed"/>), in whole
    /// weeks; the map is drawn again when that changes.
    /// </summary>
    private void FitHeat()
    {
        if (Lower.ActualWidth <= 0)
            return;
        var weeks = Math.Clamp((int)((Lower.ActualWidth - CompanionNeed() - AroundWeeks) / WeekWidth), FewestWeeks, MostWeeks);
        if (weeks == _weeks)
            return;
        _weeks = weeks;
        if (_heat is { } h)
            FillHeat(h.Activity, h.Series, h.Today, h.Goal);
    }

    /// <summary>
    /// The companion card's width with every companion at its full 2x scale: the widest sprite of the catalog in screen
    /// pixels, its margins, the room beside it and the card's own edges. A fixed width fitted 2B (238 px) and halved
    /// the wider ones (the user, 2026-10-04: "Остальных скукожило").
    /// </summary>
    private double CompanionNeed()
    {
        var widest = _services?.Companions?.Catalog.All.Select(c => c.Width).DefaultIfEmpty(0).Max() ?? 0;
        if (widest == 0)
            widest = 262; // no catalog: the widest of the eleven
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        return Math.Ceiling(widest * Sprite.MaxScale / dpi) + Sprite.Margin.Left + Sprite.Margin.Right + Sprite.Beside
               + 2 * 25 + 4;
    }

    /// <summary>
    /// Days with Glossa - half a year to a year as the window allows - a column a week from Monday (ink, not the
    /// accent): darker for more actions, the darkest a full day; a missed day a freeze covered is outlined.
    /// </summary>
    private void FillHeat(IReadOnlyList<DayActivity> activity, ActivityStreak series, DateOnly today, int goal)
    {
        _heat = (activity, series, today, goal);
        var weeks = _weeks;
        Heat.Columns = weeks;
        // The card's width from its weeks, not from its content: a language bar (ProgressBar) keeps the width it was
        // last laid out at, and an Auto column measured by it would never narrow again after a wide window.
        HeatPanel.Width = weeks * WeekWidth + AroundWeeks - HeatPanel.Margin.Right;
        HeatCaption.Text = weeks >= 52 ? "ДНИ С GLOSSA, ГОД" : weeks <= FewestWeeks ? "ДНИ С GLOSSA, ПОЛГОДА"
            : $"ДНИ С GLOSSA, {(int)Math.Round(weeks * 7 / 30.44)} МЕСЯЦЕВ";
        Heat.Children.Clear();
        var byDay = activity.ToDictionary(d => d.Day);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var start = monday.AddDays(-7 * (weeks - 1));
        for (var row = 0; row < 7; row++)
            for (var week = 0; week < weeks; week++)
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
        var today = LibraryStats.Day(DateTime.UtcNow);
        if (_cardsLeft != n || _cardsDay != today)
        {
            _cardsLeft = n;
            _cardsDay = today;
            if (IsVisible) Refresh(); // the companion is glad when the day's cards are done; a hidden page refreshes on show
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string section }) OpenSettings?.Invoke(section);
    }
}
