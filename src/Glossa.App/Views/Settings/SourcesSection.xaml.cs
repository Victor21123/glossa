using System.Windows;
using System.Windows.Controls;
using Glossa.App.ViewModels;
using Glossa.Core.Dictionaries;
using Microsoft.Win32;

namespace Glossa.App.Views.Settings;

public partial class SourcesSection : UserControl
{
    private static readonly (string Name, string Description)[] FormatList =
    [
        ("DSL", "ABBYY Lingvo: .dsl, .dsl.dz"), ("StarDict", ".ifo, .idx и .dict (.dz), обычно в архиве"), ("MDX", "MDict, файл .mdx"),
        ("Yomitan", ".zip словаря Yomitan или Yomichan"),
    ];

    private readonly DictionariesViewModel _vm;

    public SourcesSection(AppServices services)
    {
        InitializeComponent();
        _vm = new DictionariesViewModel(services);
        DataContext = _vm;
        EntryCounts.ItemsSource = new[] { 1, 2, 3, 4, 5 };
        foreach (var (name, description) in FormatList)
        {
            var row = new Grid { Margin = new Thickness(0, 10, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold });
            var text = new TextBlock { Text = description, FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty, "InkSoft");
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
            line.SetResourceReference(Border.BorderBrushProperty, "RuleSoft");
            Formats.Children.Add(line);
        }
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DictionariesViewModel.SelectedPack)) CheckBox.Text = "";
        };
    }

    /// <summary>0 — Установленные, 1 — Каталог, 2 — Импорт файла.</summary>
    public void ShowTab(int index) => Tabs.SelectedIndex = index;

    private void OnTab(object sender, SelectionChangedEventArgs e)
    {
        if (InstalledTab is null) return;
        InstalledTab.Visibility = Tabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        CatalogTab.Visibility = Tabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        ImportTab.Visibility = Tabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUp(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void OnDown(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.DataContext is PackItem item) _vm.Move(item, delta);
    }

    private void OnCheck(object sender, TextChangedEventArgs e)
    {
        var entries = _vm.Check(CheckBox.Text);
        CheckResults.ItemsSource = entries;
        CheckEmpty.Visibility = entries.Count == 0 && CheckBox.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnUpdatePack(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPack is not { } pack) return;
        var entry = _vm.Catalog.FirstOrDefault(c => c.Item.Id == pack.Info.Id);
        if (entry is null) return;
        _vm.SelectedEntry = entry;
        Tabs.SelectedIndex = 1;
        await _vm.InstallAsync(entry);
    }

    private void OnRemovePack(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPack is not { } pack) return;
        var answer = MessageBox.Show(Window.GetWindow(this), $"Удалить словарь \"{pack.Title}\"? Его можно будет поставить снова из каталога.",
            "Glossa", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK) _vm.Remove(pack);
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CatalogEntry entry) return;
        _vm.SelectedEntry = entry;
        await _vm.InstallAsync(entry);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _vm.Cancel();

    private void OnChooseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = DictionaryImport.FileFilter, Title = "Файл словаря" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _vm.Prepare(dialog.FileName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _vm.Prepare(files[0]);
    }

    private void OnDiscard(object sender, RoutedEventArgs e) => _vm.Discard();

    private async void OnBuild(object sender, RoutedEventArgs e) => await _vm.ImportAsync();
}
