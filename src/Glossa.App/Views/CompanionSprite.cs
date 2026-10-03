using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glossa.Core.Companions;

namespace Glossa.App.Views;

/// <summary>
/// The companion on «Главная», drawn pixel for pixel: every sprite pixel is a whole number of device pixels (at 125%
/// too) and never smoothed - the largest whole scale that fits the height it is given, one scale for every companion
/// (from the tallest sprite of the catalog) so a child stays shorter than an adult. Stands on the bottom edge, centred.
/// Plays the idle <see cref="IdleTimeline"/> plans - one timer for the next change, stopped while hidden.
/// </summary>
public sealed class CompanionSprite : FrameworkElement
{
    private readonly Dictionary<string, BitmapSource> _frames = [];
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    private readonly Random _rng = new();
    private Companion? _companion;
    private Mood _mood;
    private IReadOnlyList<IdleStep> _steps = [];
    private int _step;
    private bool _lastBlinked;
    private string _frame = CompanionFrames.Base;
    private int _tallest = 1;
    private (int X, int Y)? _head;

    public CompanionSprite()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        _timer.Tick += (_, _) => Advance();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Advance();
            else
            {
                _timer.Stop();
                _steps = []; // shown again, a breath starts from its rest
            }
        };
    }

    /// <summary>False for snapshots: the mood's rest frame, no timer.</summary>
    public bool Animate { get; set; } = true;

    /// <summary>
    /// The companion away (caught forged): a flat grey shape of its rest frame, standing still - it is gone, its place
    /// is kept.
    /// </summary>
    public bool Silhouette
    {
        get => _silhouette;
        set
        {
            if (value == _silhouette) return;
            _silhouette = value;
            _steps = [];
            Advance();
        }
    }

    private bool _silhouette;
    private static readonly Brush Shade = Frozen(new SolidColorBrush(Color.FromRgb(0x6B, 0x6F, 0x78)));

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    /// <summary>
    /// The scale asked for, sprite pixels to device pixels: 2 - at 125% the tallest companion stands 419 points high
    /// (at 1 it was 210, lost in its card).
    /// </summary>
    public int MaxScale { get; set; } = 2;

    /// <summary>
    /// Points kept free over the head for the speech bubble (<see cref="SpeechBubble.Room"/>): a short companion has
    /// it already under the tallest one's height, a tall one asks for more.
    /// </summary>
    public double HeadRoom { get; set; }

    /// <summary>
    /// Points the sprite leaves beside it for the words (name, mood): a narrow companion (Ренне, 184 pixels) fitted
    /// the 2x scale in the smallest window and left the words a few points, one letter a row.
    /// </summary>
    public double Beside { get; set; }

    /// <summary>The top of the head on the base frame as drawn now, in this element's points; null before a companion.</summary>
    public Point? HeadTop
    {
        get
        {
            if (_head is not { } head || !_frames.TryGetValue(CompanionFrames.Base, out var image) || RenderSize.Height <= 0)
                return null;
            var (_, at) = Place(image);
            return new Point(at.X + (head.X + 0.5) * at.Width / image.PixelWidth, at.Y + head.Y * at.Height / image.PixelHeight);
        }
    }

    /// <summary>The scale drawn last (for the snapshots' notes).</summary>
    public int Scale { get; private set; }

    /// <summary>
    /// The display scale to draw for (1.25 at 125%); null - the monitor's. Snapshots set it: off screen WPF reports 96
    /// DPI, and a sheet rendered at 125% would show pixels that are not whole.
    /// </summary>
    public double? Dpi { get; set; }

    private (double X, double Y) DpiScale()
    {
        if (Dpi is { } forced)
            return (forced, forced);
        var dpi = VisualTreeHelper.GetDpi(this);
        return (dpi.DpiScaleX, dpi.DpiScaleY);
    }

    /// <summary>
    /// Who and how. A new companion's frames are read once (ten frames, about 2.5 MB decoded); a frame that cannot be
    /// decoded shows the base instead. <see cref="InvalidOperationException"/> if even the base cannot be read.
    /// </summary>
    public void Show(Companion companion, Mood mood, int tallest)
    {
        var changed = _companion?.Id != companion.Id;
        if (changed)
        {
            var baseImage = Read(companion.Frame(CompanionFrames.Base))
                ?? throw new InvalidOperationException($"Companion {companion.Id}: the base frame cannot be read");
            _frames.Clear();
            // A frame the companion has not got is the base's bytes: decoded once.
            var decoded = new Dictionary<byte[], BitmapSource>(ReferenceEqualityComparer.Instance)
            {
                [companion.Frame(CompanionFrames.Base)] = baseImage,
            };
            foreach (var frame in CompanionFrames.All)
            {
                var png = companion.Frame(frame);
                if (!decoded.TryGetValue(png, out var image))
                    decoded[png] = image = Read(png) ?? baseImage;
                _frames[frame] = image;
            }
            _companion = companion;
            _head = HeadOf(baseImage);
        }
        if (changed || _tallest != Math.Max(1, tallest))
            InvalidateMeasure(); // the size asked for follows the companion's width and the tallest height
        _tallest = Math.Max(1, tallest);
        if (changed || mood != _mood)
        {
            _mood = mood;
            _steps = [];
            _step = 0;
            Advance();
        }
        InvalidateVisual();
    }

    /// <summary>Where the head is (<see cref="CompanionHead"/>), from the frame's alpha (kept for the clicks).</summary>
    private (int X, int Y)? HeadOf(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        var alpha = new byte[w * h];
        for (var i = 0; i < alpha.Length; i++)
            alpha[i] = pixels[i * 4 + 3];
        (_alpha, _alphaWidth, _alphaHeight) = (alpha, w, h);
        return CompanionHead.Find(alpha, w, h);
    }

    private byte[]? _alpha;
    private int _alphaWidth, _alphaHeight;

    /// <summary>
    /// The base frame's pixel under a point of this element, or null off the figure (a transparent pixel): the click
    /// zones are in those pixels (<see cref="Companion.ZoneAt"/>).
    /// </summary>
    public (int X, int Y)? PixelAt(Point point)
    {
        if (_alpha is null || !_frames.TryGetValue(CompanionFrames.Base, out var image))
            return null;
        var (_, at) = Place(image);
        if (!at.Contains(point))
            return null;
        var x = Math.Clamp((int)((point.X - at.X) * _alphaWidth / at.Width), 0, _alphaWidth - 1);
        var y = Math.Clamp((int)((point.Y - at.Y) * _alphaHeight / at.Height), 0, _alphaHeight - 1);
        return _alpha[y * _alphaWidth + x] != 0 ? (x, y) : null;
    }

    /// <summary>A frame decoded from the pack's bytes, or null: the catalog checked only its header.</summary>
    private static BitmapSource? Read(byte[] png)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png, writable: false);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException
                                       or InvalidOperationException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>The next frame of the idle; a new breath when one ends (a blink in some of the rests).</summary>
    private void Advance()
    {
        _timer.Stop();
        if (_companion is null)
            return;
        if (!Animate || _silhouette)
        {
            _frame = IdleTimeline.Breath(_mood, blink: false)[0].Frame;
            InvalidateVisual();
            return;
        }
        if (++_step >= _steps.Count)
        {
            _lastBlinked = IdleTimeline.Blinks(_rng, _lastBlinked);
            _steps = IdleTimeline.Breath(_mood, _lastBlinked);
            _step = 0;
        }
        var step = _steps[_step];
        _frame = step.Frame;
        InvalidateVisual();
        if (step.Ms > 0 && IsVisible)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(step.Ms);
            _timer.Start();
        }
    }

    /// <summary>
    /// Asks for the room of <see cref="MaxScale"/>: the tallest companion's height (one height for all, so the card
    /// keeps its size from character to character) and this one's width. Given less, it draws at the largest whole
    /// scale that fits.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_companion is null)
            return new Size(0, 0);
        var (dx, dy) = DpiScale();
        // A narrow window (1100 points wide at the least) leaves less than the 2x width: the scale steps down, the
        // sprite is never cut.
        var scale = double.IsInfinity(availableSize.Width) ? MaxScale
            : Math.Clamp((int)Math.Floor(Math.Max(0, availableSize.Width - Beside) * dx / _companion.Width + 1e-6), 1, MaxScale);
        var high = _tallest * scale / dy;
        // The bubble's room over the head: the figure stands on the bottom edge, so the head is this far up.
        if (HeadRoom > 0 && _head is { } head && _frames.TryGetValue(CompanionFrames.Base, out var image))
            high = Math.Max(high, (image.PixelHeight - head.Y) * scale / dy + HeadRoom);
        return new Size(_companion.Width * scale / dx, high);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_companion is null || !_frames.TryGetValue(_frame, out var image))
            return;
        (Scale, var at) = Place(image);
        if (!_silhouette)
        {
            dc.DrawImage(image, at);
            return;
        }
        // The frame's own alpha as the mask, pixel for pixel like the picture itself.
        var mask = new ImageBrush(image);
        RenderOptions.SetBitmapScalingMode(mask, BitmapScalingMode.NearestNeighbor);
        mask.Freeze();
        dc.PushOpacityMask(mask);
        dc.DrawRectangle(Shade, null, at);
        dc.Pop();
    }

    /// <summary>The scale and the rectangle a frame is drawn in: on the bottom edge, centred, the corner on a device pixel.</summary>
    private (int Scale, Rect At) Place(BitmapSource image)
    {
        var (dx, dy) = DpiScale();
        var width = _companion?.Width ?? image.PixelWidth;
        // A hair over: the height asked for at 125% (tallest * 2 / 1.25) comes back as 418.99999 after layout.
        var scale = Math.Clamp(Math.Min((int)Math.Floor(RenderSize.Height * dy / _tallest + 1e-6),
            (int)Math.Floor(RenderSize.Width * dx / width + 1e-6)), 1, MaxScale);
        var w = image.PixelWidth * scale / dx;
        var h = image.PixelHeight * scale / dy;
        var x = Math.Round((RenderSize.Width - w) / 2 * dx) / dx;
        var y = Math.Round((RenderSize.Height - h) * dy) / dy;
        return (scale, new Rect(x, y, w, h));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
        InvalidateVisual();
    }
}
