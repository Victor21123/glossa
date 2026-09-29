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
        ShowKnown([]);
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
        ShowKnown([]);
    }

    /// <summary>
    /// Frames the words already in the dictionary (screen pixels): thin and dashed, or bold and amber for «Не могу запомнить»,
    /// so the two differ by line as well as colour; the hint says how many there are.
    /// </summary>
    public void ShowKnown(IReadOnlyList<(PixelRect Box, bool Pinned)> words)
    {
        Known.Children.Clear();
        var scale = Scale();
        foreach (var (box, pinned) in words)
        {
            var frame = new System.Windows.Shapes.Rectangle
            {
                RadiusX = 4, RadiusY = 4,
                Stroke = pinned ? PinnedBrush : Brushes.White,
                StrokeThickness = pinned ? 2.5 : 1.5,
                StrokeDashArray = pinned ? null : new DoubleCollection { 3, 2 },
                Effect = Halo,
            };
            Place(frame, box, scale, pad: 3);
            Known.Children.Add(frame);
        }
        KnownText.Text = words.Count == 0 ? "" : $"из словаря: {words.Count}";
        KnownText.Visibility = words.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static readonly Brush PinnedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xB8, 0x4B)));
    private static readonly System.Windows.Media.Effects.DropShadowEffect Halo = Frozen(
        new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.85 });

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void Place(FrameworkElement element, PixelRect b, double scale, double pad)
    {
        Canvas.SetLeft(element, (b.Left - _bounds.Left) / scale - pad);
        Canvas.SetTop(element, (b.Top - _bounds.Top) / scale - pad);
        element.Width = b.Width / scale + pad * 2;
        element.Height = b.Height / scale + pad * 2;
    }

    /// <summary>Outlines a word (screen pixels) for the gamepad; null hides the outline.</summary>
    public void Mark(PixelRect? box)
    {
        if (box is not { } b)
        {
            Highlight.Visibility = Visibility.Collapsed;
            return;
        }
        Place(Highlight, b, Scale(), pad: 4);
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
