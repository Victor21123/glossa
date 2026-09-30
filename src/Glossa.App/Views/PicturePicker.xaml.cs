using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Glossa.Core.Pictures;
using Microsoft.Win32;

namespace Glossa.App.Views;

/// <summary>
/// «Картинка значения»: searches the query (the AI card's, or what the user types), shows six pictures as they arrive
/// and gives back the one clicked; or takes the user's own from the clipboard, a dropped file or one chosen.
/// </summary>
public partial class PicturePicker : UserControl
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff"];
    private const long MaxFileBytes = 40L << 20;

    private MeaningPictures? _pictures;
    private CancellationTokenSource? _search;

    public PicturePicker() => InitializeComponent();

    /// <summary>A picture chosen: its bytes, the found picture it is (null for the user's own) and the query that found it.</summary>
    public event Action<byte[], PictureCandidate?, string?>? Chosen;

    /// <summary>«Убрать картинку».</summary>
    public event Action? Removed;

    public event Action? CloseRequested;

    public void Open(MeaningPictures pictures, string word, string query, bool hasPicture)
    {
        _pictures = pictures;
        WordText.Text = word;
        QueryBox.Text = query;
        RemoveButton.Visibility = hasPicture ? Visibility.Visible : Visibility.Collapsed;
        Tiles.ItemsSource = null;
        _ = SearchAsync();
        Dispatcher.BeginInvoke(() =>
        {
            QueryBox.Focus();
            QueryBox.CaretIndex = QueryBox.Text.Length;
        }, DispatcherPriority.Input);
    }

    /// <summary>The dialog closed: a search still running stops.</summary>
    public void Stop()
    {
        _search?.Cancel();
        _search = null;
    }

    /// <summary>What went wrong with the picture just chosen (it would not open).</summary>
    public void Fail(string message) => Status.Text = message;

    /// <summary>Glossa.exe --render-main: the dialog with pictures already found, no network.</summary>
    internal void Preview(string word, string query, IReadOnlyList<(PictureCandidate Candidate, byte[] Bytes)> found)
    {
        WordText.Text = word;
        QueryBox.Text = query;
        RemoveButton.Visibility = Visibility.Visible;
        Status.Text = Clicking;
        var tiles = found.Select(f => new Tile(f.Candidate, query)).ToList();
        for (var i = 0; i < tiles.Count; i++) tiles[i].Take(found[i].Bytes);
        Tiles.ItemsSource = tiles;
    }

    private const string Clicking = "Щёлкните картинку, чтобы взять её.";

    private async Task SearchAsync()
    {
        if (_pictures is not { } pictures) return;
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        Tiles.ItemsSource = null;
        var query = QueryBox.Text.Trim();
        if (query.Length == 0)
        {
            Status.Text = "Напишите, что на картинке: 1-3 слова, лучше по-английски.";
            return;
        }
        Status.Text = "Ищу в Википедии, Wikimedia Commons и Openverse...";
        try
        {
            var search = await pictures.SearchAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (search.Offline)
            {
                Status.Text = "Нет связи с Википедией, Wikimedia Commons и Openverse - ни напрямую, ни через прокси.";
                return;
            }
            if (search.Found.Count == 0)
            {
                Status.Text = $"По \"{query}\" ничего не нашлось. Попробуйте другие слова, лучше по-английски.";
                return;
            }
            Status.Text = Clicking;
            var tiles = new ObservableCollection<Tile>(search.Found.Take(MeaningPictures.Count).Select(c => new Tile(c, query)));
            var spares = new Queue<PictureCandidate>(search.Found.Skip(MeaningPictures.Count));
            Tiles.ItemsSource = tiles;

            // A picture that will not load gives its place to a spare, or goes: no dead tiles to click.
            async Task LoadAsync(Tile tile)
            {
                var bytes = await pictures.FetchAsync(tile.Candidate, cts.Token);
                if (cts.IsCancellationRequested) return;
                tile.Take(bytes);
                if (tile.Ready) return;
                if (spares.TryDequeue(out var next))
                {
                    var spare = new Tile(next, query);
                    tiles[tiles.IndexOf(tile)] = spare;
                    await LoadAsync(spare);
                }
                else tiles.Remove(tile);
            }
            await Task.WhenAll(tiles.ToList().Select(LoadAsync));
            if (!cts.IsCancellationRequested && tiles.Count == 0)
                Status.Text = "Картинки нашлись, но ни одна не загрузилась. Попробуйте ещё раз или другие слова.";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // a new search or the dialog closed
        }
        catch (Exception e) when (!cts.IsCancellationRequested)
        {
            // A handler awaits this: whatever the sites answered must not take the window down.
            Status.Text = $"Поиск не удался: {e.Message}";
        }
    }

    private async void OnSearch(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync();
    }

    private void OnTile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Tile { Bytes: { } bytes } tile) Chosen?.Invoke(bytes, tile.Candidate, tile.Query);
    }

    private void OnRemove(object sender, RoutedEventArgs e) => Removed?.Invoke();

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    // ---- the user's own picture ----

    private void TakeOwn(byte[]? bytes)
    {
        if (bytes is null) Status.Text = $"Это не картинка или файл больше {MaxFileBytes >> 20} МБ.";
        else Chosen?.Invoke(bytes, null, null);
    }

    /// <summary>Ctrl+V with a picture on the clipboard takes it; text still goes into the search box.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || FromClipboard() is not { } bytes) return;
        e.Handled = true;
        TakeOwn(bytes);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!HasPicture(e.Data)) return; // text may still be dropped into the search box
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!HasPicture(e.Data)) return;
        e.Handled = true;
        TakeOwn(FromData(e.Data));
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Своя картинка значения",
            Filter = "Картинки|" + string.Join(";", Extensions.Select(x => "*" + x)),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) TakeOwn(ImageFile([dialog.FileName]));
    }

    private static bool HasPicture(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsImage)
        || data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap);

    private static byte[]? FromData(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] files && ImageFile(files) is { } file) return file;
        if (data.GetData("PNG") is MemoryStream png) return png.ToArray();
        if (data.GetData(DataFormats.Bitmap) is BitmapSource image) return Png(image);
        return null;
    }

    /// <summary>
    /// A picture copied in a browser or an image editor, or an image file copied in Explorer. PNG data goes first:
    /// the plain bitmap of some programs comes with its alpha empty and would read as fully transparent.
    /// </summary>
    private static byte[]? FromClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList() && ImageFile(Clipboard.GetFileDropList().Cast<string>()) is { } file) return file;
            if (Clipboard.GetData("PNG") is MemoryStream png) return png.ToArray();
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image) return Png(image);
        }
        catch (ExternalException)
        {
            // another program holds the clipboard
        }
        return null;
    }

    /// <summary>The first picture file's bytes; null for none, one too big, or one that cannot be read (locked, gone, no access).</summary>
    private static byte[]? ImageFile(IEnumerable<string> files)
    {
        try
        {
            return files.FirstOrDefault(IsImage) is { } path && new FileInfo(path).Length <= MaxFileBytes ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()) && File.Exists(path);

    /// <summary>The bitmap as PNG, opaque (see <see cref="FromClipboard"/>).</summary>
    private static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>One found picture: the source's name on it, the credit as its tooltip.</summary>
    public sealed class Tile(PictureCandidate candidate, string query) : ObservableObject
    {
        public PictureCandidate Candidate => candidate;
        public string Query => query;
        public string Source => candidate.Source;
        public string Credit => candidate.Keep("").Credit;
        public byte[]? Bytes { get; private set; }
        public ImageSource? Image { get; private set; }
        public string? Note { get; private set; } = "загружаю";
        public bool Ready => Image is not null;

        public void Take(byte[]? bytes)
        {
            Image = ImageFiles.Decode(bytes, 480);
            Bytes = Image is null ? null : bytes;
            Note = Image is null ? "не открылась" : null;
            OnPropertyChanged(string.Empty);
        }
    }
}
