using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glossa.App.Capture;
using Glossa.App.Interop;
using Glossa.Core.Ocr;
using CoreBlock = Glossa.Core.Text.TextBlock;

namespace Glossa.App.Views;

/// <summary>
/// «Остановить кадр»: the captured monitor shown over the game, pixel for pixel, while the game runs on underneath. It
/// takes the keyboard and mouse from the game (a game that holds the cursor lets it go), so words can be clicked or
/// picked with the gamepad, or (Только перевод, «Зона») a rectangle drawn around the text to translate; closing it gives
/// the game its focus back.
/// </summary>
public partial class FrozenFrame : Window
{
    /// <summary>A press and release closer than this (physical pixels, both ways) is a click, not a rectangle.</summary>
    private const double MinDrag = 8;

    private PixelRect _bounds;
    private readonly List<PixelRect> _plates = []; // where the vertical blocks' plates stand (they widen, so they may meet)
    private IntPtr _hwnd;
    private Point? _dragFrom;

    public FrozenFrame()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnPress;
        MouseMove += OnDrag;
        // The window takes the monitor's size only once shown: the dimming follows it until a drag begins.
        SizeChanged += (_, _) =>
        {
            if (Selecting && _dragFrom is null) Dim.Data = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        };
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

    /// <summary>«Зона»: a rectangle drawn on the still, in physical screen pixels.</summary>
    public event Action<PixelRect>? ZoneSelected;

    /// <summary>The still waits for a rectangle (a click without dragging still reports a point).</summary>
    public bool Selecting { get; private set; }

    /// <summary>«Зона»: the still dims and takes a rectangle drawn with the mouse; off, clicks pick words as before.</summary>
    public void SelectZone(bool on)
    {
        Selecting = on;
        _dragFrom = null;
        Cursor = on ? Cursors.Cross : null;
        SelectionBox.Visibility = Visibility.Collapsed;
        Selection.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) Dim.Data = new RectangleGeometry(new Rect(0, 0, Math.Max(ActualWidth, 1), Math.Max(ActualHeight, 1)));
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (!Selecting) return;
        _dragFrom = e.GetPosition(this);
        CaptureMouse();
        ShowSelection(_dragFrom.Value, _dragFrom.Value);
    }

    private void OnDrag(object sender, MouseEventArgs e)
    {
        if (_dragFrom is { } from && e.LeftButton == MouseButtonState.Pressed) ShowSelection(from, e.GetPosition(this));
    }

    /// <summary>The rectangle between two points (window units) left clear, the rest of the still dimmed.</summary>
    private void ShowSelection(Point a, Point b)
    {
        var rect = new Rect(a, b);
        Canvas.SetLeft(SelectionBox, rect.X);
        Canvas.SetTop(SelectionBox, rect.Y);
        SelectionBox.Width = rect.Width;
        SelectionBox.Height = rect.Height;
        SelectionBox.Visibility = Visibility.Visible;
        Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, Math.Max(ActualWidth, 1), Math.Max(ActualHeight, 1))), new RectangleGeometry(rect));
    }

    /// <summary>The end of a drag: a rectangle, or a point when the mouse hardly moved.</summary>
    private void OnReleased(MouseButtonEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        _dragFrom = null;
        ReleaseMouseCapture();
        var a = PointToScreen(from); // physical screen pixels
        var b = PointToScreen(e.GetPosition(this));
        if (Math.Abs(b.X - a.X) < MinDrag && Math.Abs(b.Y - a.Y) < MinDrag)
        {
            SelectionBox.Visibility = Visibility.Collapsed;
            Clicked?.Invoke((int)b.X, (int)b.Y);
            return;
        }
        ZoneSelected?.Invoke(new PixelRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
    }

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
        Plates.Children.Clear();
        _plates.Clear();
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
        SelectZone(false);
        Hide();
        Shot.Source = null;
        Highlight.Visibility = Visibility.Collapsed;
        ShowKnown([]);
        Plates.Children.Clear();
        _plates.Clear();
    }

    /// <summary>
    /// Перевод экрана: a plate exactly over a paragraph (screen pixels) in the window's colours. The translation is set in
    /// the original's type size and shrinks to fit the original's place when it is longer (Russian often is), so plates
    /// never run over each other. A column of vertical text is too thin to hold a line of Russian: its plate is wider
    /// (<see cref="Glossa.Core.Text.TextBlocks.PlateBox"/>), the translation reads horizontally in a size taken from the column's width, and
    /// the Viewbox shrinks what does not fit its height; the extra width goes to the side where the other blocks
    /// (<paramref name="others"/>, the frame's other paragraphs) and the plates already placed are not. A click shows the
    /// original under it and back. Returns where the text goes as it streams.
    /// </summary>
    public Action<string> AddTranslation(CoreBlock block, IEnumerable<PixelRect>? others = null)
    {
        var scale = Scale();
        const double pad = 4, inset = 6;
        var box = Glossa.Core.Text.TextBlocks.PlateBox(block, _bounds, [.. others ?? [], .. _plates]);
        var width = Math.Max(box.Width / scale + pad * 2, 120);
        // A one-line caption has room beside it: widen before shrinking its (usually longer) translation.
        if (!block.Vertical && block.Lines == 1) width = Math.Max(width, Math.Min(width * 1.8, 420));
        // Where the plate really stands (screen pixels), widened or not: the next plates keep clear of it.
        _plates.Add(new PixelRect(box.Left, box.Top, box.Left + (width - pad * 2) * scale, box.Bottom));
        var size = block.Vertical
            ? block.Box.Width / Math.Max(1, block.Lines) / scale * 0.5 // a column's width is the size of its letters
            : block.Box.Height / Math.Max(1, block.Lines) / scale * 0.58;
        var text = new TextBlock
        {
            Text = "...", TextWrapping = TextWrapping.Wrap, Width = width - inset * 2,
            FontSize = Math.Clamp(size, 11, 22),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var plate = new Border
        {
            CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Padding = new Thickness(inset, 3, inset, 3),
            Cursor = Cursors.Hand, ToolTip = "Щелчок - оригинал",
            Child = new Viewbox { Child = text, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top },
        };
        plate.SetResourceReference(Border.BackgroundProperty, "Surface");
        plate.SetResourceReference(Border.BorderBrushProperty, "Rule");
        Place(plate, box, scale, pad);
        plate.Width = width;
        if (block.Vertical)
        {
            // A column's plate is as tall as its text needs (up to the column's height), from the column's top: the rest of
            // the original stays in view, and the Viewbox shrinks the text only when the column is too short for it.
            plate.ClearValue(HeightProperty);
            plate.MaxHeight = box.Height / scale + pad * 2;
        }
        plate.MouseLeftButtonUp += (_, e) =>
        {
            plate.Opacity = plate.Opacity > 0.5 ? 0.06 : 1;
            e.Handled = true; // not a click on a word
        };
        Plates.Children.Add(plate);
        return t => text.Text = t;
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
        if (Selecting)
        {
            OnReleased(e);
            return;
        }
        var p = PointToScreen(e.GetPosition(this)); // physical screen pixels
        Clicked?.Invoke((int)p.X, (int)p.Y);
    }
}
