using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Glossa.App.Theme;
using Glossa.Core.Config;
using Glossa.Core.Games;
using Glossa.Core.Library;
using Glossa.Core.Ocr;
using Glossa.Core.Pictures;
using Glossa.Core.Study;
using Russian = Glossa.Core.Text.Russian;

namespace Glossa.App.Views;

/// <summary>«Учёба»: today's session and then the review, one word at a time, on Anki's schedule.</summary>
public partial class StudyPage : UserControl
{
    private static readonly StudyClock Clock = StudyClock.Local;
    private static readonly int[] Sizes = [10, 20, 30, 50];
    private const int RowsShown = 5;

    private AppServices? _services;
    private IReadOnlyList<SavedWord> _words = [];
    private IReadOnlyDictionary<CardKey, ReviewState> _states = new Dictionary<CardKey, ReviewState>();
    private IReadOnlyList<ReviewAnswer> _answers = [];
    private StudyStats _stats = StudyStats.Empty;
    private StudyPlan? _plan;
    private StudySession? _session;
    private bool _filling, _flipped, _pinned, _reverse;
    private DateTime _shownUtc, _startedUtc;
    private int _answered;
    private BitmapImage? _shot;
    private PixelRect? _box;
    private SavedWord? _word;

    /// <summary>Pictures picked from the back during this review: a word answered «Снова» comes back with its new one.</summary>
    private readonly Dictionary<string, (MeaningPicture? Picture, string? Query)> _chosenPictures = [];

    /// <summary>The word as this review last changed it (the session holds the copy it started with).</summary>
    private SavedWord Latest(SavedWord w) => _chosenPictures.TryGetValue(w.Id, out var chosen)
        ? w with { Picture = chosen.Picture, PictureQuery = chosen.Query ?? w.PictureQuery }
        : w;

    public StudyPage()
    {
        InitializeComponent();
        FrameRing.Stroke = GameRing.Ink;
        FrameHalo.Stroke = GameRing.Edge;
    }

    /// <summary>
    /// Today at a glance: the session as «Начать» would start it with no filters (for «Главная»), how many cards the
    /// whole day holds (the tab's badge) and the study statistics.
    /// </summary>
    public event Action<StudyPlan, int, StudyStats>? TodayChanged;

    /// <summary>
    /// «подобрать» on the back: the picture dialog for this word, and what to call with the picture chosen (null:
    /// removed) and the query that found it.
    /// </summary>
    public event Action<SavedWord, Action<MeaningPicture?, string?>>? PickPicture;

    /// <summary>A review is on screen: the keys belong to it.</summary>
    public bool Reviewing => _session is not null;

    public void Attach(AppServices services)
    {
        _services = services;
        _filling = true;
        foreach (var n in Sizes)
            SizeFilter.Items.Add(new ComboBoxItem { Content = $"{n} {Russian.Plural(n, "карточка", "карточки", "карточек")}", Tag = n });
        var size = services.Settings.Study.SessionSize;
        SizeFilter.SelectedIndex = Array.IndexOf(Sizes, Sizes.MinBy(n => Math.Abs(n - size)));
        _filling = false;
        Refresh();
    }

    /// <summary>Everything from the library as it is now; a running review is left alone.</summary>
    public void Refresh()
    {
        if (_services is not { } services || Reviewing) return;
        var now = DateTime.UtcNow;
        _words = services.Library.List();
        _states = services.Library.ReviewStates();
        _answers = services.Library.Answers(now.AddDays(-400));
        _stats = StudyStats.Of(_answers, Clock, now);
        FillFilters();
        ShowPlan(now);
        var study = services.Settings.Study;
        var session = SessionBuilder.Build(_words, _states, Today(now), study.Limits(), study.Config(), Clock, now);
        var all = SessionBuilder.Build(_words, _states, Today(now), study.Limits() with { Size = 100_000 }, study.Config(), Clock, now);
        TodayChanged?.Invoke(session, all.Cards.Count, _stats);
    }

    /// <summary>Space and Enter start; in a review Space shows the answer and then means "Нормально", 1-4 answer, P speaks, Esc ends.</summary>
    public bool HandleKey(Key key)
    {
        if (_session is null)
        {
            if (key is not (Key.Space or Key.Enter) || !StartButton.IsEnabled || GameFilter.IsDropDownOpen || SizeFilter.IsDropDownOpen) return false;
            Start();
            return true;
        }
        switch (key)
        {
            case Key.Space or Key.Enter:
                if (_flipped) Answer(Rating.Good);
                else Flip();
                return true;
            case Key.D1 or Key.NumPad1: Answer(Rating.Again); return true;
            case Key.D2 or Key.NumPad2: Answer(Rating.Hard); return true;
            case Key.D3 or Key.NumPad3: Answer(Rating.Good); return true;
            case Key.D4 or Key.NumPad4: Answer(Rating.Easy); return true;
            case Key.P: Speak(); return true;
            case Key.Escape: Finish(); return true;
            default: return false;
        }
    }

    // ---- the start screen ----

    private IReadOnlyList<ReviewAnswer> Today(DateTime now) =>
        _answers.Where(a => a.AnsweredUtc >= Clock.Start(Clock.Day(now))).ToList();

    private string? ChosenLanguage => (LanguageFilter.SelectedItem as ListBoxItem)?.Tag as string;

    private string? ChosenGame => (GameFilter.SelectedItem as ComboBoxItem)?.Tag as string;

    private int ChosenSize => (SizeFilter.SelectedItem as ComboBoxItem)?.Tag as int? ?? 20;

    private IEnumerable<string> GamesOf(SavedWord w)
    {
        var games = _services!.Settings.Games;
        var met = w.Contexts.Count > 0 ? w.Contexts.Select(c => (c.AppExe, c.WindowTitle)) : [(w.AppExe, w.WindowTitle)];
        return met.Select(m => GameProfiles.DisplayName(games, m.AppExe, m.WindowTitle)).OfType<string>().Distinct();
    }

    /// <summary>Languages and games of the dictionary; the choice made stays while it still exists.</summary>
    private void FillFilters()
    {
        _filling = true;
        var language = ChosenLanguage;
        LanguageFilter.Items.Clear();
        LanguageFilter.Items.Add(new ListBoxItem { Content = "Все языки" });
        foreach (var code in _words.GroupBy(w => w.Language).OrderByDescending(g => g.Count()).Select(g => g.Key))
            LanguageFilter.Items.Add(new ListBoxItem { Content = code.ToUpperInvariant(), Tag = code });
        LanguageFilter.SelectedItem = LanguageFilter.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == language) ?? LanguageFilter.Items[0];
        LanguageFilter.Visibility = LanguageFilter.Items.Count > 2 ? Visibility.Visible : Visibility.Collapsed;

        var game = ChosenGame;
        GameFilter.Items.Clear();
        GameFilter.Items.Add(new ComboBoxItem { Content = "Все игры" });
        foreach (var name in _words.SelectMany(GamesOf).GroupBy(g => g).OrderByDescending(g => g.Count()).Select(g => g.Key))
            GameFilter.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        GameFilter.SelectedItem = GameFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == game) ?? GameFilter.Items[0];
        GameFilter.Visibility = GameFilter.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _filling = false;
    }

    private void OnFilter(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && _services is not null && !Reviewing) ShowPlan(DateTime.UtcNow);
    }

    private void ShowPlan(DateTime now)
    {
        var study = _services!.Settings.Study;
        var (language, game) = (ChosenLanguage, ChosenGame);
        _plan = SessionBuilder.Build(_words, _states, Today(now), study.Limits() with { Size = ChosenSize }, study.Config(), Clock, now,
            w => (language is null || w.Language == language) && (game is null || GamesOf(w).Contains(game)));

        var n = _plan.Cards.Count;
        var minutes = Math.Max(1, (int)Math.Ceiling(n * _stats.SecondsPerCard / 60.0));
        SessionTitle.Text = n > 0 ? $"{n} {Russian.Plural(n, "карточка", "карточки", "карточек")}, около {minutes} мин" : "На сегодня всё";
        SessionCounts.Text = n > 0
            ? string.Join(", ", new[]
              {
                  _plan.Pinned.Count > 0 ? $"{_plan.Pinned.Count} не могу запомнить" : null,
                  _plan.Due.Count > 0 ? $"{_plan.Due.Count} по расписанию" : null,
                  _plan.New.Count > 0 ? $"{_plan.New.Count} {Russian.Plural(_plan.New.Count, "новое", "новых", "новых")}" : null,
              }.OfType<string>())
            : NothingDue(now);
        StartButton.IsEnabled = n > 0;
        StartHint.Visibility = n > 0 ? Visibility.Visible : Visibility.Hidden;
        Queues.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;

        PinnedTitle.Text = $"НЕ МОГУ ЗАПОМНИТЬ ({_plan.Pinned.Count})";
        DueTitle.Text = $"ПО РАСПИСАНИЮ ({_plan.Due.Count})";
        NewTitle.Text = $"НОВЫЕ ({_plan.New.Count} ИЗ {_plan.NewPerDay} В ДЕНЬ)";
        Rows(PinnedRows, _plan.Pinned, now);
        Rows(DueRows, _plan.Due, now);
        Rows(NewRows, _plan.New, now);
        NewNote.Text = study.Limits().NewOrder switch
        {
            NewWordOrder.Recent => "Сначала те, что встретились недавно.",
            NewWordOrder.Random => "Вперемешку; порядок держится весь день.",
            _ => "Сначала те, что ты искал несколько раз, потом недавние.",
        };

        var week = $"За неделю: {_stats.WeekAnswers} {Russian.Plural(_stats.WeekAnswers, "ответ", "ответа", "ответов")}";
        if (_stats.WeekRetention is { } kept) week += $", помню {kept}%";
        if (_stats.Streak > 0) week += $", {StudyLabels.Days(_stats.Streak)} подряд";
        WeekLine.Text = week;
        var steps = study.Config().LearnSteps;
        ScheduleNote.Text = steps.Count > 0
            ? $"Расписание как в Anki: шаги {string.Join(" и ", steps.Select(s => s.ToString("0.#", Russian.Culture)))} мин, дальше по дням"
            : "Расписание как в Anki";
    }

    /// <summary>Why there is nothing to study and when there will be.</summary>
    private string NothingDue(DateTime now)
    {
        if (_words.Count == 0) return $"Словарь пуст: найди слово в игре ({_services!.Settings.Hotkey}), и оно попадёт сюда.";
        var today = Clock.Day(now);
        if (_states.Values.Where(s => s.DueAt > now).Min(s => s.DueAt) is { } soon)
            return $"Слова на учебных шагах вернутся через {Math.Max(1, (int)Math.Ceiling((soon - now).TotalMinutes))} мин.";
        var next = _states.Values.Where(s => s.DueDay > today).GroupBy(s => s.DueDay!.Value).MinBy(g => g.Key);
        if (next is null) return "Новые слова на сегодня кончились.";
        var days = next.Key.DayNumber - today.DayNumber;
        return $"Следующее повторение {(days == 1 ? "завтра" : $"через {StudyLabels.Days(days)}")}: " +
               $"{next.Count()} {Russian.Plural(next.Count(), "слово", "слова", "слов")}.";
    }

    private void Rows(Panel target, IReadOnlyList<SessionCard> cards, DateTime now)
    {
        target.Children.Clear();
        foreach (var card in cards.Take(RowsShown))
        {
            var (meta, warn) = Meta(card, now);
            var right = new TextBlock { Text = meta, FontSize = 12.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            right.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Warn" : "Faint");
            DockPanel.SetDock(right, Dock.Right);
            var word = new TextBlock
            {
                Text = card.Word.Headword, FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = UiFonts.For(card.Word.Language),
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(word, Dock.Left);
            var translation = new TextBlock
            {
                Text = card.Word.Translation, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            translation.SetResourceReference(TextBlock.ForegroundProperty, "InkSoft");
            var row = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12, 9, 12, 10),
                Margin = new Thickness(0, 0, 0, 6), Child = new DockPanel { Children = { right, word, translation } },
            };
            row.SetResourceReference(Border.BackgroundProperty, "Surface");
            row.SetResourceReference(Border.BorderBrushProperty, "RuleSoft");
            target.Children.Add(row);
        }
        if (cards.Count > RowsShown)
        {
            var more = new TextBlock { Text = $"и ещё {cards.Count - RowsShown}", FontSize = 12.5, Margin = new Thickness(4, 2, 4, 0) };
            more.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            target.Children.Add(more);
        }
    }

    /// <summary>The right-hand note of a queue row; true when it is a warning (overdue).</summary>
    private (string Text, bool Warn) Meta(SessionCard card, DateTime now)
    {
        var today = Clock.Day(now);
        switch (card.Bucket)
        {
            case StudyBucket.Pinned:
                var right = StudyPins.RightDays(card.Word, _answers, Clock);
                return (right > 0 ? $"верно {StudyLabels.Days(right)}" : card.State.Queue == CardQueue.New ? "ещё не отвечал" : "пока не верно", false);
            case StudyBucket.Due:
                if (card.State.DueAt is not null) return ("учебный шаг", false);
                var late = today.DayNumber - (card.State.DueDay ?? today).DayNumber;
                return late > 0 ? ($"просрочено {StudyLabels.Days(late)}", true) : ("сегодня", false);
            default:
                if (card.Word.Lookups > 1) return ($"искал {card.Word.Lookups} {Russian.Plural(card.Word.Lookups, "раз", "раза", "раз")}", false);
                return (Ago(card.Word.LastSeenUtc, now), false);
        }
    }

    private static string Ago(DateTime utc, DateTime now) => (Clock.Day(now).DayNumber - Clock.Day(utc).DayNumber) switch
    {
        <= 0 => "сегодня",
        1 => "вчера",
        var d => $"{StudyLabels.Days(d)} назад",
    };

    private void OnStart(object sender, RoutedEventArgs e) => Start();

    // ---- the review ----

    private void Start()
    {
        if (_services is not { } services || _plan is not { Cards.Count: > 0 } plan) return;
        _session = new StudySession(plan, _states, services.Settings.Study.Config(), Clock, services.Library.SaveAnswer);
        _answered = 0;
        _chosenPictures.Clear();
        _startedUtc = DateTime.UtcNow;
        DoneBanner.Visibility = Visibility.Collapsed;
        StartView.Visibility = Visibility.Collapsed;
        ReviewView.Visibility = Visibility.Visible;
        ShowNext();
    }

    private void ShowNext()
    {
        if (_session?.Next(DateTime.UtcNow) is { } card) Show(card);
        else Finish();
    }

    private void Show(SessionCard card)
    {
        var study = _services!.Settings.Study;
        var w = card.Word;
        var now = DateTime.UtcNow;
        _flipped = false;
        _pinned = w.Pinned;
        _shownUtc = now;
        ProgressText.Text = $"{Math.Min(_session!.Done + 1, _session.Total)} из {_session.Total}";
        Progress.Maximum = Math.Max(_session.Total, 1);
        Progress.Value = _session.Done;

        // Front: the scene, the word, the line with the word marked.
        var font = UiFonts.For(w.Language);
        Headword.Text = w.Headword;
        Headword.FontFamily = font;
        Reading.Text = w.Reading;
        Reading.FontFamily = font;
        Reading.Visibility = study.FrontReading && !string.IsNullOrWhiteSpace(w.Reading) && w.Reading != w.Headword ? Visibility.Visible : Visibility.Collapsed;
        Line.FontFamily = font;
        FillLine(w);
        Line.Visibility = study.FrontLine && !string.IsNullOrWhiteSpace(w.Context) ? Visibility.Visible : Visibility.Collapsed;
        ShowShot(study.FrontShot ? w : null, now);

        // Back, shown on Space.
        Translation.Text = w.Translation ?? w.DefinitionTranslation ?? w.Definition;
        SceneNote.Inlines.Clear();
        if (w.UsageNote is { Length: > 0 } usage)
        {
            var label = new Run("контекст сцены: ");
            label.SetResourceReference(TextElement.ForegroundProperty, "Muted");
            SceneNote.Inlines.Add(label);
            SceneNote.Inlines.Add(new Run(usage));
        }
        SceneNote.Visibility = SceneNote.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LineTranslation.Text = w.ContextTranslation;
        LineTranslation.Visibility = study.BackLineTranslation && !string.IsNullOrWhiteSpace(w.ContextTranslation) ? Visibility.Visible : Visibility.Collapsed;
        FillMeta(w);
        ShowPin(w);
        _word = w;
        ShowMeaning(w);

        // A reverse card turns the faces round: the meaning asks, the frame, the word and its line answer.
        _reverse = card.Direction == CardDirection.Reverse;
        if (_reverse)
        {
            PromptTranslation.Text = Translation.Text;
            PromptScene.Text = w.UsageNote;
            PromptScene.Visibility = string.IsNullOrWhiteSpace(w.UsageNote) ? Visibility.Collapsed : Visibility.Visible;
            ShowShot(w, now); // the frame is part of the answer, whatever the front settings say
            Frame.Visibility = Visibility.Collapsed;
            Reading.Visibility = !string.IsNullOrWhiteSpace(w.Reading) && w.Reading != w.Headword ? Visibility.Visible : Visibility.Collapsed;
            Line.Visibility = string.IsNullOrWhiteSpace(w.Context) ? Visibility.Collapsed : Visibility.Visible;
        }
        Prompt.Visibility = _reverse ? Visibility.Visible : Visibility.Collapsed;
        WordPanel.Visibility = _reverse ? Visibility.Collapsed : Visibility.Visible;
        Translation.Visibility = _reverse ? Visibility.Collapsed : Visibility.Visible;
        if (_reverse) SceneNote.Visibility = Visibility.Collapsed;
        FlipText.Text = _reverse ? "показать слово" : "показать перевод";

        Back.Visibility = Visibility.Collapsed;
        FlipHint.Visibility = Visibility.Visible;
        Answers.Visibility = Visibility.Hidden;
    }

    private void FillLine(SavedWord w)
    {
        Line.Inlines.Clear();
        if (w.Context is not { } text) return;
        var length = (w.Contexts.FirstOrDefault()?.Surface ?? w.Word).Length;
        var at = w.ContextOffset;
        if (at < 0 || at + length > text.Length)
        {
            Line.Inlines.Add(new Run(text));
            return;
        }
        Line.Inlines.Add(new Run(text[..at]));
        var word = new Run(text.Substring(at, length));
        word.SetResourceReference(TextElement.BackgroundProperty, "MarkBg");
        word.SetResourceReference(TextElement.ForegroundProperty, "MarkInk");
        Line.Inlines.Add(word);
        Line.Inlines.Add(new Run(text[(at + length)..]));
    }

    private void FillMeta(SavedWord w)
    {
        WordMeta.Inlines.Clear();
        var head = string.Join(", ", new[] { w.Level, w.PartOfSpeech }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (head.Length > 0) WordMeta.Inlines.Add(new Run(head));
        foreach (var part in w.Components.Where(c => !string.IsNullOrWhiteSpace(c.Meaning)))
        {
            if (WordMeta.Inlines.Count > 0) WordMeta.Inlines.Add(new Run(WordMeta.Inlines.Count == 1 && head.Length > 0 ? "\n" : ", "));
            WordMeta.Inlines.Add(new Run(part.Part) { FontFamily = UiFonts.For(w.Language) });
            WordMeta.Inlines.Add(new Run(" " + part.Meaning));
        }
        WordMeta.Visibility = WordMeta.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPin(SavedWord w)
    {
        PinText.Text = _pinned ? "Снять пометку" : "Не могу запомнить";
        var suggest = _pinned && _services!.Settings.Study.SuggestUnpin && StudyPins.SuggestUnpin(w with { Pinned = true }, _answers, Clock);
        UnpinHint.Text = $"Верно в {StudyPins.DaysToUnpin} разных дня: пометку можно снять.";
        UnpinHint.Visibility = suggest ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The picture of the meaning, one chosen during this review first; or «нет, подобрать».</summary>
    private void ShowMeaning(SavedWord w)
    {
        var picture = Latest(w).Picture;
        var image = ImageFiles.Load(picture is null ? null : Path.Combine(DataPaths.Root, picture.File));
        MeaningImage.Source = image;
        MeaningImage.Visibility = MeaningCredit.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        MeaningCredit.Text = picture?.Credit;
        NoMeaning.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        MeaningPanel.Visibility = _services!.Settings.Study.BackPicture ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPickPicture(object sender, RoutedEventArgs e)
    {
        if (_word is not { } w) return;
        PickPicture?.Invoke(Latest(w), (picture, query) =>
        {
            _chosenPictures[w.Id] = (picture, query ?? Latest(w).PictureQuery);
            if (_word?.Id == w.Id) ShowMeaning(w);
        });
    }

    /// <summary>The frame around the word, a little closer than the whole screenshot, the word in the middle when possible.</summary>
    private void ShowShot(SavedWord? w, DateTime now)
    {
        _shot = null;
        _box = null;
        var path = w?.ShotFile is { } file ? Path.Combine(DataPaths.Root, file) : null;
        if (path is null || !File.Exists(path))
        {
            Frame.Visibility = Visibility.Collapsed;
            return;
        }
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad; // do not lock the file
        img.UriSource = new Uri(path);
        img.EndInit();
        _shot = img;
        _box = w!.WordBox;
        FrameImage.Source = img;
        var context = w.Contexts.FirstOrDefault();
        var game = GameProfiles.DisplayName(_services!.Settings.Games, context?.AppExe ?? w.AppExe, context?.WindowTitle ?? w.WindowTitle);
        GameText.Text = string.Join(", ", new[] { game, Ago(w.LastSeenUtc, now) }.OfType<string>());
        Frame.Visibility = Visibility.Visible;
        PlaceShot();
    }

    private void OnFrameSized(object sender, SizeChangedEventArgs e) => PlaceShot();

    private void PlaceShot()
    {
        if (_shot is null || Frame.ActualWidth <= 0) return;
        double w = Frame.ActualWidth, h = Frame.ActualHeight, iw = _shot.PixelWidth, ih = _shot.PixelHeight;
        var scale = Math.Max(w / iw, h / ih) * 1.25;
        var (cx, cy) = _box is { } b ? (b.CenterX, b.CenterY) : (iw / 2, ih / 2);
        var left = Math.Clamp(w / 2 - cx * scale, w - iw * scale, 0);
        var top = Math.Clamp(h / 2 - cy * scale, h - ih * scale, 0);
        FrameImage.Width = iw * scale;
        FrameImage.Height = ih * scale;
        Canvas.SetLeft(FrameImage, left);
        Canvas.SetTop(FrameImage, top);
        FrameRing.Visibility = FrameHalo.Visibility = _box is null ? Visibility.Collapsed : Visibility.Visible;
        if (_box is not { } box) return;
        foreach (var ring in new[] { FrameHalo, FrameRing })
        {
            ring.Width = box.Width * scale + 12;
            ring.Height = box.Height * scale + 8;
            Canvas.SetLeft(ring, left + box.Left * scale - 6);
            Canvas.SetTop(ring, top + box.Top * scale - 4);
        }
    }

    private void Flip()
    {
        if (_session is null || _flipped) return;
        _flipped = true;
        Back.Visibility = Visibility.Visible;
        FlipHint.Visibility = Visibility.Collapsed;
        if (_reverse)
        {
            WordPanel.Visibility = Visibility.Visible;
            if (_shot is not null)
            {
                Frame.Visibility = Visibility.Visible;
                PlaceShot();
            }
        }
        var choices = _session.Choices(DateTime.UtcNow);
        Answers.Children.Clear();
        Answers.Children.Add(AnswerButton(Rating.Again, "Снова", "Danger", choices.Again));
        Answers.Children.Add(AnswerButton(Rating.Hard, "Сложно", "Warn", choices.Hard));
        Answers.Children.Add(AnswerButton(Rating.Good, "Нормально", "Good", choices.Good));
        Answers.Children.Add(AnswerButton(Rating.Easy, "Легко", "Info", choices.Easy));
        Answers.Visibility = Visibility.Visible;
        if (_services!.Settings.Study.SpeakOnFlip) Speak();
    }

    /// <summary>A button of the mockup: a colour strip, the answer, the real next interval, its key.</summary>
    private Button AnswerButton(Rating rating, string name, string tone, Outcome outcome)
    {
        var strip = new Border { Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(-4, -2, -4, 0) };
        strip.SetResourceReference(Border.BackgroundProperty, tone);
        var when = new TextBlock
        {
            Text = StudyLabels.Button(outcome.Interval, _services!.Settings.Study.Config().LearnAhead), FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
        };
        when.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var key = new Border
        {
            Style = (Style)FindResource("KbdBox"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 7, 0, 2),
            Child = new TextBlock { Text = ((int)rating).ToString(Russian.Culture), FontSize = 11 },
        };
        var button = new Button
        {
            Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(4, 2, 4, 10), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Children =
                {
                    strip,
                    new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 9, 0, 0) },
                    when,
                    key,
                },
            },
        };
        button.Click += (_, _) => Answer(rating);
        return button;
    }

    private void Answer(Rating rating)
    {
        if (_session is null || !_flipped) return;
        var now = DateTime.UtcNow;
        try
        {
            _session.Answer(rating, now, (int)Math.Min((now - _shownUtc).TotalMilliseconds, 60_000)); // Anki counts at most a minute
        }
        catch (Exception ex)
        {
            _services!.Log.Error("study: saving an answer failed", ex);
            ProgressText.Text = "Ответ не сохранился, попробуй ещё раз";
            return;
        }
        _answered++;
        ShowNext();
    }

    private void Speak()
    {
        if (_reverse && !_flipped) return; // the word is the answer of a reverse card: not before Space
        if (_session?.Current is { } card) _ = _services!.Speech.SpeakAsync(card.Word.Headword, card.Word.Language);
    }

    private void OnSpeak(object sender, RoutedEventArgs e) => Speak();

    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (_session?.Current is not { } card) return;
        _pinned = !_pinned;
        _services!.Library.SetPinned(card.Word.Id, _pinned);
        _services.NotifyLibraryChanged();
        ShowPin(card.Word with { Pinned = _pinned });
    }

    /// <summary>Back to the start screen with what the session came to (Esc ends it early).</summary>
    private void Finish()
    {
        if (_session is null) return;
        var waiting = _session.Waiting;
        _session = null;
        _shot = null;
        FrameImage.Source = null;
        ReviewView.Visibility = Visibility.Collapsed;
        StartView.Visibility = Visibility.Visible;
        if (_answered > 0)
        {
            var minutes = Math.Max(1, (int)Math.Round((DateTime.UtcNow - _startedUtc).TotalMinutes));
            var text = $"Сессия закончена: {_answered} {Russian.Plural(_answered, "ответ", "ответа", "ответов")} за {minutes} мин.";
            if (waiting is { Count: > 0, Next: { } next })
                text += $" {waiting.Count} {Russian.Plural(waiting.Count, "слово вернётся", "слова вернутся", "слов вернутся")} через " +
                        $"{Math.Max(1, (int)Math.Ceiling((next - DateTime.UtcNow).TotalMinutes))} мин: тогда начни сессию снова.";
            DoneText.Text = text;
            DoneBanner.Visibility = Visibility.Visible;
        }
        Refresh();
    }
}
