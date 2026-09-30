using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glossa.App.Games;
using Glossa.Core.Games;
using Microsoft.Win32;

namespace Glossa.App.Views.Settings;

/// <summary>One game in the list: its profile, words and when it was last played.</summary>
public sealed record GameRow(GameProfile Profile, string Name, string Meta, int Words, DateTime? FirstUtc, DateTime? LastUtc);

public partial class GamesSection : UserControl
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly AppServices _services;
    private readonly GameRegistry? _games;
    private List<GameRow> _rows = [];
    private GameProfile? _shown;
    private bool _filling;
    private bool _saving;

    public GamesSection(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _games = services.Games;
        // Filled here, not on Loaded: snapshots draw the section off screen, where Loaded never comes.
        Fill();
        Loaded += (_, _) =>
        {
            if (_games is not null) _games.Changed += OnChanged;
            Fill();
        };
        Unloaded += (_, _) =>
        {
            if (_games is not null) _games.Changed -= OnChanged;
        };
    }

    private void OnChanged()
    {
        if (!_saving) Dispatcher.BeginInvoke(Fill);
    }

    /// <summary>The list, newest game first, with its words counted as the dictionary names games.</summary>
    private void Fill()
    {
        var profiles = _games?.Profiles ?? [];
        Empty.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = profiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (profiles.Count == 0) return;

        var stats = new Dictionary<string, (HashSet<string> Words, DateTime First, DateTime Last)>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in _services.Library.List())
        {
            var sentences = w.Contexts.Count > 0
                ? w.Contexts.Select(c => (c.AppExe, c.WindowTitle, c.CreatedUtc))
                : [(w.AppExe, w.WindowTitle, w.CreatedUtc)];
            foreach (var (exe, title, at) in sentences)
            {
                if (_games!.DisplayName(exe, title) is not { } name) continue;
                var s = stats.TryGetValue(name, out var found) ? found : ([], at, at);
                s.Words.Add(w.Id);
                stats[name] = (s.Words, at < s.First ? at : s.First, at > s.Last ? at : s.Last);
            }
        }

        _rows = profiles.Select(p =>
        {
            var has = stats.TryGetValue(p.Name, out var s);
            var words = has ? s.Words.Count : 0;
            var meta = has ? $"{Words(words)}, {Day(s.Last)}" : $"слов пока нет, профиль с {Date(p.CreatedUtc)}";
            return new GameRow(p, p.Name, meta, words, has ? s.First : null, has ? s.Last : null);
        }).OrderByDescending(r => r.LastUtc ?? r.Profile.CreatedUtc).ToList();

        var query = Search.Text.Trim();
        var shown = query.Length == 0
            ? _rows
            : _rows.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                               || r.Profile.Programs.Any(x => Path.GetFileName(x).Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        NotFound.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var keep = shown.FirstOrDefault(r => r.Profile == _shown) ?? shown.FirstOrDefault();
        _filling = true;
        try
        {
            List.ItemsSource = shown;
            List.SelectedItem = keep;
        }
        finally
        {
            _filling = false;
        }
        if (keep is not null) Show(keep);
        Detail.Visibility = keep is null ? Visibility.Hidden : Visibility.Visible;
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => Fill();

    private void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && List.SelectedItem is GameRow row) Show(row);
    }

    private void Show(GameRow row)
    {
        var p = _shown = row.Profile;
        _filling = true;
        try
        {
            Title.Text = p.Name;
            Stats.Text = row.FirstUtc is { } first && row.LastUtc is { } last
                ? $"{Words(row.Words)}, первый поиск {Date(first)}, последний {When(last)}"
                : $"слов пока нет, профиль с {Date(p.CreatedUtc)}";
            if (!NameBox.IsKeyboardFocused) NameBox.Text = p.Name;
            FirstTitle.Text = p.FirstTitle.Length > 0 && p.FirstTitle != p.Name ? $"заголовок окна был \"{p.FirstTitle}\"" : "";

            Programs.Children.Clear();
            foreach (var program in p.Programs)
            {
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                if (p.Programs.Count > 1)
                {
                    var remove = new Button { Content = "Убрать", Style = (Style)FindResource("GhostButton"), Margin = new Thickness(8, 0, 0, 0), Tag = program };
                    remove.Click += OnRemoveProgram;
                    DockPanel.SetDock(remove, Dock.Right);
                    line.Children.Add(remove);
                }
                line.Children.Add(new ContentControl { Content = program, Style = (Style)FindResource("PathBox"), MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left });
                Programs.Children.Add(line);
            }

            LanguageChoice.SelectedValue = p.Language;
            DuringChoice.SelectedValue = p.DuringLookup;
            AiChoice.SelectedValue = p.Ai;
            PauseItem.IsEnabled = !p.IsProtected;
            var general = _services.Settings.DuringLookup switch { "frame" => "остановить кадр", "pause" => "пауза игры", _ => "не трогать" };
            DuringNote.Text = p.IsProtected
                ? $"В игре античит ({p.AntiCheat}) - пауза недоступна, вместо неё стоп-кадр. Стоп-кадр в игру не вмешивается."
                : $"\"Как в общих\" - как в \"Вызове и клавишах\": сейчас \"{general}\".";

            ShowWindow(p);
            if (p.AntiCheat is { } ac)
            {
                AntiCheatStatus.Style = (Style)FindResource("WarnText");
                AntiCheatStatus.Content = $"{ac} - пауза и растягивание окна выключены";
            }
            else
            {
                var none = new TextBlock { Text = "не найден", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
                none.SetResourceReference(TextBlock.ForegroundProperty, "InkSoft");
                AntiCheatStatus.Style = null;
                AntiCheatStatus.Content = none;
            }
        }
        finally
        {
            _filling = false;
        }
        _games?.Recheck(p);
    }

    /// <summary>How the game's window is shown: live when the game runs, else as at its last lookup.</summary>
    private void ShowWindow(GameProfile p)
    {
        var running = GameWindow.FindRunning(p.Programs);
        var mode = running?.Mode ?? WindowModes.Parse(p.WindowMode);
        (string Style, string Text)? status = mode switch
        {
            WindowMode.Borderless => ("OkText", "без рамки - карточка видна поверх игры"),
            WindowMode.Windowed => ("OkText", "в окне - карточка видна поверх игры"),
            WindowMode.Exclusive when p.CardKnocksOut => ("WarnText", "эксклюзивный полноэкранный режим - карточка выбивает игру из полного экрана"),
            WindowMode.Exclusive => ("OkText", "полноэкранный режим - карточка видна поверх игры"),
            _ => null,
        };
        if (status is { } s)
        {
            WindowStatus.Style = (Style)FindResource(s.Style);
            WindowStatus.Content = s.Text;
        }
        else
        {
            var unknown = new TextBlock { Text = "станет известно при следующем поиске в игре", FontSize = 14 };
            unknown.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            WindowStatus.Style = null;
            WindowStatus.Content = unknown;
        }
        BorderlessSwitch.IsChecked = p.Borderless;
        BorderlessSwitch.IsEnabled = !p.IsProtected;
        var notes = new List<string>();
        if (mode == WindowMode.Exclusive)
            notes.Add(p.CardKnocksOut
                ? "Включи в настройках игры \"Окно без рамки\" (Borderless) - или \"Карточка на другом мониторе\" в \"Вызове и клавишах\", если мониторов два."
                : "Если карточка выбьет игру из полного экрана, Glossa заметит это при поиске и подскажет, что делать.");
        notes.Add(p.IsProtected
            ? "В игре с античитом окно не трогаем."
            : "Для игр, которые умеют только окно с рамкой: Glossa снимет рамку и растянет окно при следующем поиске.");
        notes.Add(running is null ? "Сейчас игра не запущена - показано по последнему поиску." : "Игра запущена сейчас.");
        WindowNote.Text = string.Join(" ", notes);
    }

    private void Save()
    {
        if (_games is null) return;
        _saving = true;
        try
        {
            _games.Save();
        }
        finally
        {
            _saving = false;
        }
    }

    private void OnChoice(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || _shown is not { } p) return;
        if (sender == LanguageChoice && LanguageChoice.SelectedValue is string language) p.Language = language;
        else if (sender == DuringChoice && DuringChoice.SelectedValue is string during) p.DuringLookup = during;
        else if (sender == AiChoice && AiChoice.SelectedValue is string ai) p.Ai = ai;
        else return;
        Save();
    }

    private void OnNameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitName();
        else if (e.Key == Key.Escape && _shown is not null) NameBox.Text = _shown.Name;
    }

    private void OnNameCommit(object sender, KeyboardFocusChangedEventArgs e) => CommitName();

    private void CommitName()
    {
        if (_shown is not { } p) return;
        var name = string.Join(' ', NameBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0 || name == p.Name)
        {
            NameBox.Text = p.Name;
            return;
        }
        p.Name = name;
        Save();
        _services.NotifyLibraryChanged(); // the dictionary names the game anew
        Fill();
    }

    private void OnAddProgram(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } p) return;
        var dialog = new OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Программа игры" };
        if (p.Programs.FirstOrDefault(Path.IsPathRooted) is { } known) dialog.InitialDirectory = Path.GetDirectoryName(known);
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (p.Programs.Any(x => string.Equals(x, dialog.FileName, StringComparison.OrdinalIgnoreCase))) return;
        p.Programs.Add(dialog.FileName);
        Save();
        Fill();
    }

    private void OnRemoveProgram(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } p || sender is not Button { Tag: string program } || p.Programs.Count < 2) return;
        p.Programs.Remove(program);
        Save();
        Fill();
    }

    private void OnBorderless(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } p) return;
        p.Borderless = BorderlessSwitch.IsChecked == true;
        Save();
        if (GameWindow.FindRunning(p.Programs) is { } running)
        {
            if (p.Borderless && running.Mode == WindowMode.Windowed) running.MakeBorderless();
            else if (!p.Borderless) running.RestoreFrame();
        }
        ShowWindow(p);
    }

    private void OnOpenWords(object sender, RoutedEventArgs e)
    {
        if (_shown is { } p && Window.GetWindow(this) is MainWindow main) main.ShowGameWords(p.Name);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } p || _games is null) return;
        _shown = null;
        _saving = true;
        try
        {
            _games.Remove(p);
        }
        finally
        {
            _saving = false;
        }
        Fill();
    }

    private static string Words(int n) =>
        n + " " + (n % 10 == 1 && n % 100 != 11 ? "слово" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "слова" : "слов");

    private static string Date(DateTime utc) => utc.ToLocalTime().ToString("d MMMM", Ru);

    /// <summary>«сегодня», «вчера» or the date: for the list.</summary>
    private static string Day(DateTime utc)
    {
        var day = utc.ToLocalTime().Date;
        return day == DateTime.Today ? "сегодня" : day == DateTime.Today.AddDays(-1) ? "вчера" : Date(utc);
    }

    /// <summary>«сегодня в 20:41», «вчера в 9:05», «26 сентября».</summary>
    private static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date >= DateTime.Today.AddDays(-1) ? $"{Day(utc)} в {local:H:mm}" : Date(utc);
    }
}
