using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using Shape = System.Windows.Shapes.Shape;
using Glossa.App.ViewModels;
using Glossa.Core.Config;
using Glossa.Core.Export;
using Glossa.Core.Library;
using Microsoft.Win32;

namespace Glossa.App.Views.Settings;

/// <summary>The settings model and the «Словарь» model (its filter decides what the exports take).</summary>
public sealed record LibrarySectionContext(SettingsViewModel Settings, LibraryViewModel Library);

public partial class LibrarySection : UserControl
{
    private readonly AppServices _services;
    private readonly LibraryViewModel _library;
    private SavedWord? _word;
    private BitmapImage? _shot;

    public LibrarySection(AppServices services, SettingsViewModel model, LibraryViewModel library)
    {
        InitializeComponent();
        _services = services;
        _library = library;
        DataContext = new LibrarySectionContext(model, library);
        ShotsFolder.Content = DataPaths.Shots;
        FillPreview();
        FaceShot.SizeChanged += (_, _) => ArrangeFace();
        Loaded += async (_, _) =>
        {
            ShotsSize.Text = Sizes.Format(await Task.Run(() => Sizes.Folder(DataPaths.Shots)));
            var up = await new AnkiConnectSync(_services.LocalHttp).IsAvailableAsync(CancellationToken.None);
            AnkiDot.SetResourceReference(Shape.FillProperty, up ? "Good" : "Page");
            AnkiDot.SetResourceReference(Shape.StrokeProperty, up ? "Good" : "Muted");
            AnkiState.Text = up ? "Anki запущена, AnkiConnect отвечает" : "Anki не запущена";
        };
    }

    /// <summary>The newest word, laid out as the note's front and back.</summary>
    private void FillPreview()
    {
        _word = _services.Library.List().OrderByDescending(w => w.LastSeenUtc).FirstOrDefault();
        if (_word is not { } w)
        {
            FaceWord.Text = "reconsider";
            FaceContext.Text = "You should reconsider your position, mortal.";
            BackTranslation.Text = "передумать";
            BackContext.Text = "Тебе стоит пересмотреть свою позицию, смертный.";
            BackMeta.Text = "B2, гл.";
            PreviewNote.Text = "Пример: в словаре пока нет слов.";
            FaceShot.Visibility = Visibility.Collapsed;
            return;
        }
        FaceWord.Text = w.Headword;
        FaceWord.FontFamily = Theme.UiFonts.For(w.Language);
        FaceContext.FontFamily = Theme.UiFonts.For(w.Language);
        Mark(FaceContext, w.Context ?? "", w.ContextOffset, (w.Contexts.FirstOrDefault()?.Surface ?? w.Word).Length);
        BackTranslation.Text = w.Translation ?? "";
        BackDefinition.Text = w.Definition ?? "";
        BackDefinition.Visibility = string.IsNullOrEmpty(w.Definition) ? Visibility.Collapsed : Visibility.Visible;
        BackContext.Text = w.ContextTranslation ?? "";
        BackMeta.Text = string.Join(", ", new[] { w.Level, w.PartOfSpeech }.Where(x => !string.IsNullOrEmpty(x)));
        PreviewNote.Text = "Так заметка с последним словом выглядит в Anki с нынешними галочками.";

        if (w.ShotFile is { } file && File.Exists(Path.Combine(DataPaths.Root, file)))
        {
            _shot = new BitmapImage();
            _shot.BeginInit();
            _shot.CacheOption = BitmapCacheOption.OnLoad;
            _shot.UriSource = new Uri(Path.Combine(DataPaths.Root, file));
            _shot.EndInit();
            FaceImage.Source = _shot;
        }
    }

    /// <summary>The frame zoomed in on the word, the word ringed, as on the note's front.</summary>
    private void ArrangeFace()
    {
        var w = FaceShot.ActualWidth;
        var h = FaceShot.ActualHeight;
        if (_shot is not { } shot || _word?.WordBox is not { } b || w <= 0)
        {
            FaceRing.Visibility = Visibility.Collapsed;
            return;
        }
        FaceShot.Clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, w, h), 8, 8);
        var k = Math.Max(h / shot.PixelHeight, Math.Min(1.0, w / (b.Width * 6)));
        var iw = shot.PixelWidth * k;
        var ih = shot.PixelHeight * k;
        var left = Math.Min(0, Math.Max(w / 2 - (b.Left + b.Width / 2) * k, w - iw));
        var top = Math.Min(0, Math.Max(h / 2 - (b.Top + b.Height / 2) * k, h - ih));
        FaceImage.Width = iw;
        FaceImage.Height = ih;
        Canvas.SetLeft(FaceImage, left);
        Canvas.SetTop(FaceImage, top);
        FaceRing.Width = b.Width * k + 8;
        FaceRing.Height = b.Height * k + 6;
        Canvas.SetLeft(FaceRing, left + b.Left * k - 4);
        Canvas.SetTop(FaceRing, top + b.Top * k - 3);
        FaceRing.Visibility = Visibility.Visible;
    }

    private static void Mark(TextBlock target, string text, int offset, int length)
    {
        target.Inlines.Clear();
        if (offset < 0 || offset + length > text.Length)
        {
            target.Inlines.Add(new Run(text));
            return;
        }
        target.Inlines.Add(new Run(text[..offset]));
        var word = new Run(text.Substring(offset, length));
        word.SetResourceReference(TextElement.BackgroundProperty, "MarkBg");
        word.SetResourceReference(TextElement.ForegroundProperty, "MarkInk");
        target.Inlines.Add(word);
        target.Inlines.Add(new Run(text[(offset + length)..]));
    }

    private void OnOpenShots(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DataPaths.Shots);
        Process.Start("explorer.exe", DataPaths.Shots);
    }

    private async void OnSync(object sender, RoutedEventArgs e) => await _library.SyncAnki();

    private async void OnExportApkg(object sender, RoutedEventArgs e)
    {
        if (Ask("Колода Anki (*.apkg)|*.apkg", "glossa.apkg") is { } path) await _library.ExportApkg(path);
    }

    private async void OnExportQuizlet(object sender, RoutedEventArgs e)
    {
        if (Ask("Текст для Quizlet (*.txt)|*.txt", "glossa-quizlet.txt") is { } path) await _library.ExportQuizlet(path);
    }

    private async void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (Ask("CSV (*.csv)|*.csv", "glossa.csv") is { } path) await _library.ExportCsv(path);
    }

    private string? Ask(string filter, string name)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = name };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }
}
