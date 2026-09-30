using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using Glossa.App.Theme;
using Glossa.App.ViewModels;
using Microsoft.Win32;

namespace Glossa.App.Views;

/// <summary>«Словарь» → «Цитаты»: the quotes, the one chosen with its frame, and what can be done to one or several.</summary>
public partial class QuotesPage : UserControl
{
    private QuotesViewModel? _vm;

    public QuotesPage() => InitializeComponent();

    /// <summary>«Слова» on the switch: the words page instead of this one.</summary>
    public event Action? WordsRequested;

    public QuotesViewModel? Model => _vm;

    public void Attach(AppServices services)
    {
        _vm = new QuotesViewModel(services);
        _vm.PropertyChanged += OnModelChanged;
        DataContext = _vm;
        ShowFrame(_vm.Selected);
    }

    /// <summary>The library changed (a quote kept, frames cleared in the settings).</summary>
    public void Reload() => _vm?.Reload();

    /// <summary>In «Только перевод» «Словарь» holds only quotes: no switch to words.</summary>
    public void ShowSwitch(bool visible) => ModeSwitch.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QuotesViewModel.Selected) or "" or null) ShowFrame(_vm?.Selected);
    }

    private void OnSized(object sender, SizeChangedEventArgs e) => FrameBox.Height = Math.Clamp(ActualHeight * 0.45, 240, 560);

    /// <summary>The quote's frame with its line ringed, white with a dark edge over the game picture as everywhere.</summary>
    private void ShowFrame(QuoteEntry? entry)
    {
        FrameOverlay.Children.Clear();
        var image = ImageFiles.Load(entry?.ShotPath);
        FrameImage.Source = image;
        NoFrame.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        if (image is null) return;
        FrameImage.Width = FrameOverlay.Width = image.PixelWidth;
        FrameImage.Height = FrameOverlay.Height = image.PixelHeight;
        if (entry!.Box is not { } b) return;
        foreach (var (brush, thickness) in new[] { (GameRing.Edge, 5.0), (GameRing.Ink, 2.5) })
        {
            var ring = new Rectangle
            {
                Width = b.Width + 14, Height = b.Height + 10, RadiusX = 5, RadiusY = 5, StrokeThickness = thickness, Stroke = brush,
            };
            Canvas.SetLeft(ring, b.Left - 7);
            Canvas.SetTop(ring, b.Top - 5);
            FrameOverlay.Children.Add(ring);
        }
    }

    private void OnModeSwitch(object sender, SelectionChangedEventArgs e)
    {
        if (ModeSwitch.SelectedIndex != 0) return;
        ModeSwitch.SelectedIndex = 1; // back to «Цитаты» for the next time this page shows
        WordsRequested?.Invoke();
    }

    private void OnScope(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuoteScope scope) _vm?.SelectScope(scope);
    }

    /// <summary>The quotes chosen in the list, or the one on view.</summary>
    private List<QuoteEntry> Chosen()
    {
        var items = QuotesList.SelectedItems.Cast<QuoteEntry>().ToList();
        if (items.Count == 0 && _vm?.Selected is { } one) items.Add(one);
        return items;
    }

    private void OnListSelection(object sender, SelectionChangedEventArgs e)
    {
        var n = QuotesList.SelectedItems.Count;
        BulkBar.Visibility = n > 1 ? Visibility.Visible : Visibility.Collapsed;
        BulkText.Text = $"Выбрано: {n}";
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true;
        Delete();
    }

    private void OnDelete(object sender, RoutedEventArgs e) => Delete();

    private void Delete()
    {
        var items = Chosen();
        if (_vm is null || items.Count == 0) return;
        var text = items.Count == 1 ? "Удалить цитату?" : $"Удалить цитат: {items.Count}?";
        if (MessageBox.Show(Window.GetWindow(this)!, text, "Glossa", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            _vm.Delete(items);
    }

    /// <summary>«Убрать кадр(ы)»: frees the disk, keeps the quotes; several at once only after asking.</summary>
    private void OnClearFrames(object sender, RoutedEventArgs e)
    {
        var items = Chosen().Where(q => q.HasShot).ToList();
        if (_vm is null || items.Count == 0) return;
        if (items.Count > 1 && MessageBox.Show(Window.GetWindow(this)!, $"Убрать кадры у цитат: {items.Count}? Сами цитаты останутся.", "Glossa",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _vm.ClearFrames(items);
    }

    private async void OnSpeak(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.SpeakAsync();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_vm?.Selected is not { } q) return;
        try
        {
            Clipboard.SetText(string.IsNullOrWhiteSpace(q.Translation) ? q.Text : q.Text + Environment.NewLine + q.Translation);
            _vm.Status = "Скопировано";
        }
        catch (ExternalException)
        {
            _vm.Status = "Буфер обмена занят другой программой - попробуйте ещё раз.";
        }
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var dialog = new SaveFileDialog { Filter = "Таблица CSV|*.csv", FileName = "Glossa-цитаты.csv" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            _vm.ExportCsv(dialog.FileName, _vm.Visible());
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _vm.Status = $"Не удалось сохранить: {ex.Message}";
        }
    }
}
