using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Glossa.App.ViewModels;
using Glossa.Core.Study;

namespace Glossa.App.Views.Settings;

public partial class StudySection : UserControl
{
    private static readonly StudyClock Clock = StudyClock.Local;
    private static readonly string[] ShortDays = ["Вс", "Пн", "Вт", "Ср", "Чт", "Пт", "Сб"];
    private readonly AppServices _services;
    private readonly SettingsViewModel _model;

    public StudySection(AppServices services, SettingsViewModel model)
    {
        InitializeComponent();
        _services = services;
        _model = model;
        DataContext = model;
        ShowForecast(); // before the first Loaded: a snapshot draws the section without waiting for it
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnChanged;
            ShowForecast();
        };
        Unloaded += (_, _) => _model.PropertyChanged -= OnChanged;
    }

    /// <summary>Choices that change what a session holds redraw the week: size, limits, direction, pinned share.</summary>
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.StudySessionSize) or nameof(SettingsViewModel.StudyNewPerDay)
            or nameof(SettingsViewModel.StudyReviewsPerDay) or nameof(SettingsViewModel.StudyDirection)
            or nameof(SettingsViewModel.StudyPinnedPercent) or nameof(SettingsViewModel.StudyBurySiblings))
            ShowForecast();
    }

    /// <summary>
    /// «Прогноз на неделю» from the mockup: today's column is the whole session available now (pinned, due, new), the
    /// others the cards the schedule brings; under it, the last week in three numbers.
    /// </summary>
    private void ShowForecast()
    {
        var now = DateTime.UtcNow;
        var today = Clock.Day(now);
        var study = _services.Settings.Study;
        var limits = study.Limits();
        var words = _services.Library.List();
        var states = _services.Library.ReviewStates();
        var answers = _services.Library.Answers(now.AddDays(-400));
        var answeredToday = answers.Where(a => Clock.Day(a.AnsweredUtc) == today).ToList();

        var session = SessionBuilder.Build(words, states, answeredToday, limits with { Size = 100_000 }, study.Config(), Clock, now);
        var alive = words.Select(w => w.Id).ToHashSet();
        var days = StudyForecast.Due(states.Values.Where(s => alive.Contains(s.WordId)), limits.Direction, Clock, today);
        days[0] = session.Cards.Count;

        Forecast.Children.Clear();
        var top = Math.Max(1, days.Max());
        for (var i = 0; i < days.Length; i++) Forecast.Children.Add(Bar(days[i], top, i == 0 ? "Сегодня" : ShortDays[(int)today.AddDays(i).DayOfWeek], i == 0));

        int Count(StudyBucket bucket) => session.Cards.Count(c => c.Bucket == bucket);
        TodayNote.Text = session.Cards.Count == 0
            ? "Сегодня повторять нечего: новые слова берутся из словаря, когда ты их ищешь в игре."
            : $"Сегодня: {Count(StudyBucket.Pinned)} с пометкой \"Не могу запомнить\", {Count(StudyBucket.Due)} по расписанию, {Count(StudyBucket.New)} новых.";

        var stats = StudyStats.Of(answers, Clock, now);
        WeekText.Text = stats.WeekAnswers.ToString(CultureInfo.InvariantCulture);
        RetentionText.Text = stats.WeekRetention is { } r ? $"{r}%" : "-";
        StreakText.Text = stats.Streak switch { 0 => "-", var d => $"{d} {DaysWord(d)}" };
    }

    private static FrameworkElement Bar(int count, int top, string day, bool today)
    {
        var column = new DockPanel { Margin = new Thickness(4, 0, 4, 0) };
        var label = new TextBlock { Text = day, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(label, Dock.Bottom);
        column.Children.Add(label);
        var bar = new Border
        {
            Height = Math.Max(3, 140.0 * count / top), VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(5, 5, 2, 2),
        };
        bar.SetResourceReference(Border.BackgroundProperty, today ? "Ink" : "Well");
        DockPanel.SetDock(bar, Dock.Bottom);
        column.Children.Add(bar);
        var number = new TextBlock
        {
            Text = count.ToString(CultureInfo.InvariantCulture), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6),
        };
        number.SetResourceReference(TextBlock.ForegroundProperty, "InkSoft");
        column.Children.Add(number);
        return column;
    }

    private static string DaysWord(int days) => (days % 10, days % 100) switch
    {
        (1, not 11) => "день",
        (>= 2 and <= 4, < 12 or > 14) => "дня",
        _ => "дней",
    };

    /// <summary>«Дополнительно»: Anki's finer knobs, folded by default.</summary>
    private void OnMore(object sender, RoutedEventArgs e) => SetMore(More.Visibility != Visibility.Visible);

    /// <summary>--render-main: the snapshot with «Дополнительно» open, brought into view.</summary>
    internal void OpenMore()
    {
        SetMore(true);
        UpdateLayout();
        MoreButton.BringIntoView();
    }

    private void SetMore(bool open)
    {
        More.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Content = open ? "Скрыть" : "Показать";
    }
}
