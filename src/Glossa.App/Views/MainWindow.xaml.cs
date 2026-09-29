using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Glossa.App.Ai;
using Glossa.App.ViewModels;
using Microsoft.Win32;

namespace Glossa.App.Views;

public partial class MainWindow : Window
{
    private readonly LibraryViewModel _library;
    private readonly AppServices _services;
    private readonly DispatcherTimer _aiTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private WordEntry? _shown;

    public MainWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _library = new LibraryViewModel(services);
        DataContext = _library;
        WordsPage.DataContext = _library;
        _library.CollectionDialogRequested += c => OpenCollection(c);
        SettingsPage.Attach(services, _library);
        Closing += (_, _) => SettingsPage.Flush();
        HomePage.Attach(services, SettingsPage.Model!);
        HomePage.OpenWords += () => ShowTab(MainTab.Words);
        HomePage.OpenWord += id =>
        {
            if (_library.Items.FirstOrDefault(w => w.Word.Id == id) is { } entry) _library.Selected = entry;
            ShowTab(MainTab.Words);
        };

        _library.PropertyChanged += OnLibraryChanged;
        _library.Items.CollectionChanged += (_, _) => UpdateEmpty();
        // The app outlives this window: listen only while it is open.
        Action reload = () => Dispatcher.Invoke(() =>
        {
            _library.Reload();
            if (HomePage.IsVisible) HomePage.Refresh();
        });
        services.LibraryChanged += reload;
        // A game profile created or renamed: the catalogue names the game by its profile.
        if (services.Games is { } games) games.Changed += reload;
        Closed += (_, _) =>
        {
            services.LibraryChanged -= reload;
            if (services.Games is { } g) g.Changed -= reload;
            SettingsPage.Detach();
            HomePage.Detach();
        };
        StateChanged += (_, _) => Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        SizeChanged += (_, _) => FrameBorder.Height = Math.Clamp(ActualHeight * 0.45, 240, 560);
        _aiTimer.Tick += (_, _) => UpdateAiStatus();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { UpdateAiStatus(); _aiTimer.Start(); }
            else _aiTimer.Stop();
        };

        Show(_library.Selected);
        UpdateEmpty();
    }

    /// <summary>«Открыть слова в словаре» from a game's profile: the dictionary filtered to that game.</summary>
    public void ShowGameWords(string game)
    {
        _library.ShowGame(game);
        ShowTab(MainTab.Words);
    }

    /// <summary>Главная, Словарь, Настройки → Справочники, or Настройки at the section last open.</summary>
    public void ShowTab(MainTab tab)
    {
        switch (tab)
        {
            case MainTab.Home:
                NavHome.IsChecked = true;
                break;
            case MainTab.Words:
                NavWords.IsChecked = true;
                break;
            default:
                NavSettings.IsChecked = true;
                SettingsPage.Show(tab == MainTab.Sources ? "sources" : SettingsPage.Current);
                break;
        }
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (WordsPage is null || HomePage is null) return;
        var home = NavHome.IsChecked == true;
        var words = NavWords.IsChecked == true;
        HomePage.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        WordsPage.Visibility = words ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = home || words ? Visibility.Collapsed : Visibility.Visible;
        if (home) HomePage.Refresh();
    }

    // ---- the selected word ----

    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.Selected)) Show(_library.Selected);
    }

    /// <summary>Follows the selected word, and the sentence being viewed inside it.</summary>
    private void Show(WordEntry? entry)
    {
        if (_shown is not null) _shown.PropertyChanged -= OnEntryChanged;
        _shown = entry;
        if (entry is not null) entry.PropertyChanged += OnEntryChanged;
        ShowShot(entry);
        RenderContext(entry);
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or null or nameof(WordEntry.Current))
        {
            ShowShot(_shown);
            RenderContext(_shown);
        }
    }

    /// <summary>The saved scene with the word outlined, both in screenshot pixels inside one Viewbox.</summary>
    private void ShowShot(WordEntry? entry)
    {
        ShotOverlay.Children.Clear();
        ShotImage.Source = null;
        var path = entry?.ShotPath;
        NoShot.Visibility = path is not null && File.Exists(path) ? Visibility.Collapsed : Visibility.Visible;
        if (path is null || !File.Exists(path)) return;

        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad; // do not lock the file
        img.UriSource = new Uri(path);
        img.EndInit();
        ShotImage.Source = img;
        ShotImage.Width = img.PixelWidth;
        ShotImage.Height = img.PixelHeight;
        ShotOverlay.Width = img.PixelWidth;
        ShotOverlay.Height = img.PixelHeight;

        if (entry!.WordBox is { } b)
        {
            var scale = Math.Max(1, img.PixelWidth / 1200.0);
            var ring = new Rectangle
            {
                Width = b.Width + 12 * scale, Height = b.Height + 8 * scale, RadiusX = 5 * scale, RadiusY = 5 * scale,
                StrokeThickness = 3 * scale,
            };
            ring.SetResourceReference(Shape.StrokeProperty, "Accent");
            Canvas.SetLeft(ring, b.Left - 6 * scale);
            Canvas.SetTop(ring, b.Top - 4 * scale);
            ShotOverlay.Children.Add(ring);
        }
    }

    /// <summary>The sentence with the word highlighted (inverse, like the card).</summary>
    private void RenderContext(WordEntry? entry)
    {
        DetailContext.Inlines.Clear();
        if (entry?.ContextText is not { } text) return;
        var off = entry.ContextOffset;
        var len = entry.SurfaceLength;
        if (off < 0 || off + len > text.Length)
        {
            DetailContext.Inlines.Add(new Run(text));
            return;
        }
        DetailContext.Inlines.Add(new Run(text[..off]));
        var word = new Run(text.Substring(off, len));
        word.SetResourceReference(TextElement.BackgroundProperty, "MarkBg");
        word.SetResourceReference(TextElement.ForegroundProperty, "MarkInk");
        DetailContext.Inlines.Add(word);
        DetailContext.Inlines.Add(new Run(text[(off + len)..]));
    }

    private void UpdateEmpty() =>
        EmptyList.Visibility = _library.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"ИИ: gemma26b выгружена · свободно 13,1 ГБ видеопамяти".</summary>
    private void UpdateAiStatus()
    {
        var s = _services.Settings.LocalAi;
        var loaded = _services.Ai.Current?.Dictionary is not null;
        var state = s.Mode == "off" ? "выключена" : loaded ? "готова"
            : !s.HasModel(s.Profile) ? Settings.ProfileTile.Absent(s.Profile)
            : !s.HasRuntime() ? "без движка llama.cpp" : "выгружена";
        var free = AiRouter.FreeVramMb();
        var vram = free >= 0 ? string.Format(CultureInfo.GetCultureInfo("ru-RU"), " · свободно {0:0.0} ГБ видеопамяти", free / 1024.0) : "";
        AiStatus.Text = $"ИИ: {s.Profile} {state}{vram}";
        AiDot.SetResourceReference(Shape.FillProperty, loaded ? "Good" : "Page");
        AiDot.SetResourceReference(Shape.StrokeProperty, loaded ? "Good" : "Muted");
    }

    // ---- window ----

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Hides Glossa to the tray, or quits it, as chosen in Настройки → Приложение.</summary>
    private void OnClose(object sender, RoutedEventArgs e)
    {
        SettingsPage.Flush();
        if (_services.Settings.CloseToTray) Close();
        else Application.Current.Shutdown();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SettingsPage.CapturingKeys) return;
        if (ModalHost.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                CloseModal();
                e.Handled = true;
            }
            return;
        }
        var editing = _library.Selected?.IsEditing == true;
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && editing)
        {
            CommitFocusedText();
            _library.SaveEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && editing)
        {
            _library.CancelEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            NavWords.IsChecked = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>Text boxes write back on focus loss; Ctrl+S must not lose the field being typed in.</summary>
    private static void CommitFocusedText()
    {
        if (Keyboard.FocusedElement is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    // ---- «Словарь» actions ----

    private void OnScope(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SidebarItem item) _library.SelectScope(item);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) DeleteSelected();
    }

    private void OnDelete(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        var items = WordsList.SelectedItems.Cast<WordEntry>().ToList();
        if (items.Count == 0 && _library.Selected is { } one) items.Add(one);
        if (items.Count == 0) return;
        var text = items.Count == 1 ? $"Удалить «{items[0].Headword}» из словаря?" : $"Удалить слов: {items.Count}?";
        if (MessageBox.Show(this, text, "Glossa", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            _library.Delete(items);
    }

    private void OnEdit(object sender, RoutedEventArgs e) => _library.BeginEdit();

    private void OnCancelEdit(object sender, RoutedEventArgs e) => _library.CancelEdit();

    private void OnSaveEdit(object sender, RoutedEventArgs e)
    {
        CommitFocusedText();
        _library.SaveEdit();
    }

    private void OnTogglePin(object sender, RoutedEventArgs e) => _library.TogglePin();

    private async void OnSpeak(object sender, RoutedEventArgs e) => await _library.SpeakAsync();

    private void OnPrevContext(object sender, RoutedEventArgs e) => _library.Step(-1);

    private void OnNextContext(object sender, RoutedEventArgs e) => _library.Step(1);

    /// <summary>The scene in the default viewer, with the word outlined as it is here.</summary>
    private void OnOpenShot(object sender, RoutedEventArgs e)
    {
        if (_library.Selected is not { ShotPath: { } path } entry || !File.Exists(path)) return;
        var open = path;
        if (entry.WordBox is { } box)
        {
            try
            {
                open = FrameExport.Outlined(path, box, System.IO.Path.Combine(Glossa.Core.Config.DataPaths.Work, "frames"));
            }
            catch (Exception ex)
            {
                _services.Log.Error("open frame", ex); // the plain frame is better than none
            }
        }
        Process.Start(new ProcessStartInfo(open) { UseShellExecute = true });
    }

    private async void OnSyncAnki(object sender, RoutedEventArgs e) => await _library.SyncAnki();

    private void OnExport(object sender, RoutedEventArgs e)
    {
        ExportButton.ContextMenu.PlacementTarget = ExportButton;
        ExportButton.ContextMenu.IsOpen = true;
    }

    private async void OnExportApkg(object sender, RoutedEventArgs e)
    {
        if (AskPath("Колода Anki|*.apkg", "Glossa.apkg") is { } p) await _library.ExportApkg(p);
    }

    private async void OnExportQuizlet(object sender, RoutedEventArgs e)
    {
        if (AskPath("Текст для Quizlet|*.txt", "Glossa-quizlet.txt") is { } p) await _library.ExportQuizlet(p);
    }

    private async void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (AskPath("Таблица CSV|*.csv", "Glossa.csv") is { } p) await _library.ExportCsv(p);
    }

    // ---- dialogs over the window ----

    private void ShowModal(FrameworkElement dialog)
    {
        AddWordDialog.Visibility = dialog == AddWordDialog ? Visibility.Visible : Visibility.Collapsed;
        CollectionDialog.Visibility = dialog == CollectionDialog ? Visibility.Visible : Visibility.Collapsed;
        ModalHost.Visibility = Visibility.Visible;
    }

    internal void CloseModal()
    {
        ModalHost.Visibility = Visibility.Collapsed;
        _pendingForCollection = [];
    }

    private void OnCloseModal(object sender, RoutedEventArgs e) => CloseModal();

    // «Новое слово»

    private void OnAddWord(object sender, RoutedEventArgs e) => OpenAddWord();

    internal void OpenAddWord(string word = "")
    {
        AddWordBox.Text = word;
        AddSentence.Text = "";
        AddGame.Text = "";
        AddLanguage.SelectedIndex = 0;
        AddCollection.ItemsSource = new[] { new Glossa.Core.Library.WordCollection("", "без коллекции", null) }.Concat(_library.ManualCollections).ToList();
        AddCollection.SelectedIndex = 0;
        UpdateAddPreview();
        ShowModal(AddWordDialog);
        Dispatcher.BeginInvoke(() => AddWordBox.Focus(), DispatcherPriority.Input);
    }

    private string? AddLanguageCode => (AddLanguage.SelectedItem as ListBoxItem)?.Tag is string { Length: > 0 } code ? code : null;

    private void OnAddWordChanged(object sender, RoutedEventArgs e) => UpdateAddPreview();

    private void UpdateAddPreview()
    {
        if (AddPreview is null) return;
        var preview = _library.Preview(AddWordBox.Text, AddLanguageCode);
        AddPreview.ItemsSource = preview;
        AddPreviewEmpty.Visibility = preview.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddSubmit.IsEnabled = AddWordBox.Text.Trim().Length > 0;
    }

    private async void OnAddWordSubmit(object sender, RoutedEventArgs e)
    {
        var input = new NewWord(AddWordBox.Text.Trim(), AddLanguageCode, Blank(AddSentence.Text), Blank(AddGame.Text));
        var collection = AddCollection.SelectedItem is Glossa.Core.Library.WordCollection { Id.Length: > 0 } c ? c.Id : null;
        CloseModal();
        NavWords.IsChecked = true;
        await _library.AddWordAsync(input, collection);
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // «Коллекция»

    private Glossa.Core.Library.WordCollection? _editingCollection;
    private IReadOnlyList<WordEntry> _pendingForCollection = [];

    private static readonly (string Name, int? Value)[] LookupChoices = [("любое число раз", null), ("2 раза и больше", 2), ("3 раза и больше", 3), ("5 раз и больше", 5)];
    private static readonly (string Name, int? Value)[] DayChoices = [("когда угодно", null), ("за последние 7 дней", 7), ("за 30 дней", 30), ("за 90 дней", 90)];

    internal void OpenCollection(Glossa.Core.Library.WordCollection? collection, IReadOnlyList<WordEntry>? words = null)
    {
        _editingCollection = collection;
        _pendingForCollection = words ?? [];
        var f = collection?.Filter;
        CollectionTitle.Text = collection is null ? "Новая коллекция" : "Коллекция";
        CollectionName.Text = collection?.Name ?? "";
        CollectionKind.SelectedIndex = collection?.IsSmart == true ? 1 : 0;
        CollectionKind.IsEnabled = collection is null && words is null;
        CollectionSave.Content = collection is null ? "Создать" : "Сохранить";
        CollectionDelete.Visibility = collection is null ? Visibility.Collapsed : Visibility.Visible;

        FilterGame.ItemsSource = new[] { "любая" }.Concat(_library.KnownGames).ToList();
        FilterGame.SelectedItem = f?.Game is { } g && _library.KnownGames.Contains(g) ? g : "любая";
        var languages = new[] { "любой" }.Concat(_library.KnownLanguages.Select(l => l.ToUpperInvariant())).ToList();
        FilterLanguage.ItemsSource = languages;
        FilterLanguage.SelectedItem = f?.Language?.ToUpperInvariant() is { } lang && languages.Contains(lang) ? lang : "любой";
        var registers = new[] { new RegisterChoice("", "любая") }.Concat(LibraryViewModel.Registers.Skip(1)).ToList();
        FilterRegister.ItemsSource = registers;
        FilterRegister.SelectedItem = registers.FirstOrDefault(r => r.Code == (f?.Register ?? "")) ?? registers[0];
        FilterLevel.Text = f?.Level ?? "";
        FilterLookups.ItemsSource = LookupChoices.Select(c => c.Name).ToList();
        FilterLookups.SelectedIndex = Math.Max(0, Array.FindIndex(LookupChoices, c => c.Value == f?.MinLookups));
        FilterDays.ItemsSource = DayChoices.Select(c => c.Name).ToList();
        FilterDays.SelectedIndex = Math.Max(0, Array.FindIndex(DayChoices, c => c.Value == f?.Days));
        FilterPinned.IsChecked = f?.PinnedOnly == true;

        UpdateCollectionDialog();
        ShowModal(CollectionDialog);
        Dispatcher.BeginInvoke(() => CollectionName.Focus(), DispatcherPriority.Input);
    }

    private bool CollectionIsSmart => CollectionKind.SelectedIndex == 1;

    private Glossa.Core.Library.SmartFilter ReadFilter() => new()
    {
        Game = FilterGame.SelectedIndex > 0 ? FilterGame.SelectedItem as string : null,
        Language = FilterLanguage.SelectedIndex > 0 ? (FilterLanguage.SelectedItem as string)?.ToLowerInvariant() : null,
        Register = FilterRegister.SelectedItem is RegisterChoice { Code.Length: > 0 } r ? r.Code : null,
        Level = Blank(FilterLevel.Text),
        MinLookups = LookupChoices[Math.Max(0, FilterLookups.SelectedIndex)].Value,
        Days = DayChoices[Math.Max(0, FilterDays.SelectedIndex)].Value,
        PinnedOnly = FilterPinned.IsChecked == true,
    };

    private void OnCollectionChanged(object sender, RoutedEventArgs e) => UpdateCollectionDialog();

    private void UpdateCollectionDialog()
    {
        if (SmartFields is null || FilterLookups.ItemsSource is null) return;
        SmartFields.Visibility = CollectionIsSmart ? Visibility.Visible : Visibility.Collapsed;
        CollectionKindNote.Text = CollectionIsSmart
            ? "Слова попадают сами — по тому, что Glossa записала при поиске: игра, помета от ИИ, уровень, сколько раз искал. Интернет не нужен."
            : "Слова кладёшь сам: «+ добавить» у слова или правый клик по выделенным в списке.";
        if (CollectionIsSmart)
        {
            var n = _library.CountMatching(ReadFilter());
            FilterCount.Text = $"Подходит слов сейчас: {n}";
        }
        CollectionSave.IsEnabled = CollectionName.Text.Trim().Length > 0;
    }

    private void OnCollectionSave(object sender, RoutedEventArgs e)
    {
        var filter = CollectionIsSmart ? ReadFilter() : null;
        var id = _library.SaveCollection(_editingCollection?.Id, CollectionName.Text.Trim(), filter);
        if (filter is null && _pendingForCollection.Count > 0) _library.AddToCollection(id, _pendingForCollection);
        CloseModal();
    }

    private void OnCollectionDelete(object sender, RoutedEventArgs e)
    {
        if (_editingCollection is not { } c) return;
        if (MessageBox.Show(this, $"Удалить коллекцию «{c.Name}»? Слова останутся в словаре.", "Glossa",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _library.DeleteCollection(c.Id);
        CloseModal();
    }

    private void OnScopeRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SidebarItem item && _library.CollectionOf(item) is { } c)
        {
            OpenCollection(c);
            e.Handled = true;
        }
    }

    /// <summary>Right click on the list: put the selected words into a collection, or delete them.</summary>
    private void OnListMenu(object sender, ContextMenuEventArgs e)
    {
        var words = WordsList.SelectedItems.Cast<WordEntry>().ToList();
        var menu = WordsList.ContextMenu!;
        menu.Items.Clear();
        if (words.Count == 0)
        {
            e.Handled = true;
            return;
        }
        var into = new MenuItem { Header = words.Count == 1 ? "В коллекцию" : $"В коллекцию ({words.Count})" };
        foreach (var c in _library.ManualCollections)
        {
            var item = new MenuItem { Header = c.Name };
            item.Click += (_, _) => _library.AddToCollection(c.Id, words);
            into.Items.Add(item);
        }
        var fresh = new MenuItem { Header = "Новая коллекция…" };
        fresh.Click += (_, _) => OpenCollection(null, words);
        into.Items.Add(fresh);
        menu.Items.Add(into);
        var delete = new MenuItem { Header = words.Count == 1 ? "Удалить" : $"Удалить ({words.Count})" };
        delete.Click += (_, _) => DeleteSelected();
        menu.Items.Add(delete);
    }

    /// <summary>«+ добавить» under the word: the manual collections it is not in yet, or a new one.</summary>
    private void OnAddToCollectionMenu(object sender, RoutedEventArgs e)
    {
        if (_library.Selected is not { } word) return;
        var menu = AddToCollectionButton.ContextMenu!;
        menu.Items.Clear();
        foreach (var c in _library.ManualCollections.Where(c => !word.Word.CollectionIds.Contains(c.Id)))
        {
            var item = new MenuItem { Header = c.Name };
            item.Click += (_, _) => _library.AddToCollection(c.Id, [word]);
            menu.Items.Add(item);
        }
        var fresh = new MenuItem { Header = "Новая коллекция…" };
        fresh.Click += (_, _) => OpenCollection(null, [word]);
        menu.Items.Add(fresh);
        menu.PlacementTarget = AddToCollectionButton;
        menu.IsOpen = true;
    }

    private void OnRemoveFromCollection(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Glossa.Core.Library.WordCollection c && _library.Selected is { } word)
            _library.RemoveFromCollection(c.Id, word);
    }

    private string? AskPath(string filter, string name)
    {
        var dlg = new SaveFileDialog
        {
            Filter = filter,
            FileName = name,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }
}

/// <summary>The main window's pages.</summary>
public enum MainTab { Home, Words, Sources, Settings }
