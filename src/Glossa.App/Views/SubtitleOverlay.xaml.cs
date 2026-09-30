using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Glossa.App.Interop;
using Glossa.Core.Ocr;

namespace Glossa.App.Views;

/// <summary>Живой перевод: a subtitle over the game, above its dialogue line (below it when there is no room).</summary>
public partial class SubtitleOverlay : Window
{
    private IntPtr _hwnd;
    private PixelRect _line;
    private PixelRect _game;

    public SubtitleOverlay() => InitializeComponent();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW
            | Native.WS_EX_TOPMOST | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED));
        Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>A subtitle for a line (screen pixels) inside the game's window: the caption, then the text.</summary>
    public void ShowFor(PixelRect line, PixelRect game, string caption, string text)
    {
        _line = line;
        _game = game;
        Caption.Text = caption;
        Line.Text = text;
        // The window's surface, slightly see-through so the scene is not cut out by a solid box.
        var surface = (Color)FindResource("SurfaceColor");
        Box.Background = new SolidColorBrush(Color.FromArgb(242, surface.R, surface.G, surface.B));
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var scale = Scale();
        Line.MaxWidth = Math.Clamp(Math.Max(line.Width / scale, 620), 360, Math.Max(360, game.Width / scale - 80));
        if (!IsVisible) Show();
        UpdateLayout();
        Place();
    }

    /// <summary>The line above the subtitle ("ПЕРЕВОД", or for a moment "СОХРАНЕНО В ЦИТАТЫ").</summary>
    public void SetCaption(string caption) => Caption.Text = caption;

    /// <summary>The translation as it streams in.</summary>
    public void SetText(string text)
    {
        if (Line.Text == text) return;
        Line.Text = text;
        if (!IsVisible) return;
        UpdateLayout();
        Place();
    }

    /// <summary>The game went to the background: the subtitle waits for it.</summary>
    public void Conceal()
    {
        if (IsVisible) Hide();
    }

    /// <summary>The game is in front again: the last subtitle comes back where it was.</summary>
    public void Reveal()
    {
        if (IsVisible || Line.Text.Length == 0) return;
        Show();
        UpdateLayout();
        Place();
    }

    /// <summary>Glossa.exe --render-card: the subtitle laid out off screen, never shown.</summary>
    internal void Preview(string caption, string text, double maxWidth)
    {
        Caption.Text = caption;
        Line.Text = text;
        Line.MaxWidth = maxWidth;
        var surface = (Color)FindResource("SurfaceColor");
        Box.Background = new SolidColorBrush(Color.FromArgb(242, surface.R, surface.G, surface.B));
    }

    private double Scale() => _hwnd == IntPtr.Zero || Native.GetDpiForWindow(_hwnd) == 0 ? 1 : Native.GetDpiForWindow(_hwnd) / 96.0;

    private void Place()
    {
        var scale = Scale();
        var w = (int)Math.Ceiling(ActualWidth * scale);
        var h = (int)Math.Ceiling(ActualHeight * scale);
        var x = (int)Math.Clamp(_line.Left - 8 * scale, _game.Left, Math.Max(_game.Left, _game.Right - w));
        var y = (int)(_line.Top - h);                            // above the line: the original stays readable
        if (y < _game.Top) y = (int)_line.Bottom;                // no room above: under it
        if (y + h > _game.Bottom) y = (int)_game.Top + 8;         // no room at all: the top of the game
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }
}
