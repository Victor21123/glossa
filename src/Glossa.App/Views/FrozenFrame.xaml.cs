using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glossa.App.Capture;
using Glossa.App.Interop;
using Glossa.Core.Ocr;

namespace Glossa.App.Views;

/// <summary>
/// «Остановить кадр»: the captured monitor shown over the game, pixel for pixel, while the game runs on underneath. It
/// takes the keyboard and mouse from the game (a game that holds the cursor lets it go), so words can be clicked or
/// picked with the gamepad; closing it gives the game its focus back.
/// </summary>
public partial class FrozenFrame : Window
{
    private PixelRect _bounds;
    private IntPtr _hwnd;

    public FrozenFrame()
    {
        InitializeComponent();
        MouseLeftButtonUp += OnClick;
        MouseRightButtonUp += (_, _) => CloseRequested?.Invoke();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CloseRequested?.Invoke();
        };
    }

    /// <summary>A word was clicked: the point in physical screen pixels.</summary>
    public event Action<int, int>? Clicked;

    /// <summary>Esc, a right click: back to the game.</summary>
    public event Action? CloseRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
    }

    /// <summary>Covers the frame's monitor with the frame and takes the focus from the game.</summary>
    public void ShowFrame(CapturedFrame frame, bool hideFromCapture)
    {
        _bounds = frame.Bounds;
        Shot.Source = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Bgra, frame.Stride);
        Highlight.Visibility = Visibility.Collapsed;
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowDisplayAffinity(_hwnd, hideFromCapture ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
        Show();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, (int)_bounds.Left, (int)_bounds.Top, (int)_bounds.Width, (int)_bounds.Height,
            Native.SWP_SHOWWINDOW);
        Native.ForceForeground(_hwnd);
        Activate();
        Keyboard.Focus(this);
        // A game that confines the cursor to its window lets go of it once it is not in front; do not wait for it.
        Native.ClipCursor(IntPtr.Zero);
    }

    public void SetHint(string text) => HintText.Text = text;

    /// <summary>Back to the game: hides the still and lets its picture go (a 1440p frame is ~15 MB).</summary>
    public void Leave()
    {
        Hide();
        Shot.Source = null;
        Highlight.Visibility = Visibility.Collapsed;
    }

    /// <summary>Outlines a word (screen pixels) for the gamepad; null hides the outline.</summary>
    public void Mark(PixelRect? box)
    {
        if (box is not { } b)
        {
            Highlight.Visibility = Visibility.Collapsed;
            return;
        }
        var scale = Scale();
        const double pad = 4;
        Canvas.SetLeft(Highlight, (b.Left - _bounds.Left) / scale - pad);
        Canvas.SetTop(Highlight, (b.Top - _bounds.Top) / scale - pad);
        Highlight.Width = b.Width / scale + pad * 2;
        Highlight.Height = b.Height / scale + pad * 2;
        Highlight.Visibility = Visibility.Visible;
    }

    /// <summary>Glossa.exe --render-main: a still with a word outlined, laid out off screen at the picture's own size.</summary>
    internal void Preview(BitmapSource shot, PixelRect word, string hint)
    {
        _bounds = new PixelRect(0, 0, shot.PixelWidth, shot.PixelHeight);
        Shot.Source = shot;
        SetHint(hint);
        Mark(word);
    }

    private double Scale() => _hwnd == IntPtr.Zero ? 1 : Native.GetDpiForWindow(_hwnd) / 96.0;

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        var p = PointToScreen(e.GetPosition(this)); // physical screen pixels
        Clicked?.Invoke((int)p.X, (int)p.Y);
    }
}
