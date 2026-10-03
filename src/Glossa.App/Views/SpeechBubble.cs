using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Glossa.App.Views;

/// <summary>
/// The companion's speech bubble, as in manga (the user's sketch, 2026-10-03): a white oval over the head with an ink
/// outline, its tail down to the speaker, one outline round both. A short line is said in one row, a longer one in
/// two or three even rows, so the oval stays an oval, not a strip or a column. The colours are fixed on purpose, the same in both themes - like the white ring round a word on a
/// frame, the bubble belongs to the picture, not to the window.
/// </summary>
public sealed class SpeechBubble : FrameworkElement
{
    /// <summary>The tail's height under the oval.</summary>
    public const double TailLength = 18;

    /// <summary>
    /// The room a bubble of three lines takes with its tail (the longest line of the eleven, 72 letters, is three):
    /// the sprite keeps it free over the head.
    /// </summary>
    public const double Room = 118;

    private const double Ink = 2;
    private const double FontSize = 15;
    private const int MostLines = 3;

    // A line this wide (in points, laid in one row) is said in one row, up to the second in two, longer in three.
    private const double OneRow = 190;
    private const double TwoRows = 420;

    // The text keeps this far from the oval's edge, past the corners of its box.
    private const double PadX = 10;
    private const double PadY = 13;

    private static readonly Brush Paper = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush InkBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)));
    private static readonly Pen Outline = Frozen(new Pen(InkBrush, Ink) { LineJoin = PenLineJoin.Round });

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private string _text = "";
    private double _tailX = double.NaN;
    private FormattedText? _layout;
    private double _a, _b;

    public string Text
    {
        get => _text;
        set
        {
            _text = Glossa.Core.Text.Russian.NoBreaks(value ?? "");
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Where the tail's tip is, across the bubble from its left edge (the head under it); NaN - the middle.</summary>
    public double TailX
    {
        get => _tailX;
        set
        {
            _tailX = value;
            InvalidateVisual();
        }
    }

    /// <summary>The widest the text may be laid (the room beside the companion).</summary>
    public double MaxTextWidth
    {
        get => _maxTextWidth;
        set
        {
            if (value == _maxTextWidth) return;
            _maxTextWidth = value;
            InvalidateMeasure();
        }
    }

    private double _maxTextWidth = 300;

    /// <summary>The longest word of the line as laid (words bound by no-break spaces count as one): lines go no narrower.</summary>
    public double MinTextWidth => _text.Length == 0 ? 0
        : _text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Max(w => Word(w).WidthIncludingTrailingWhitespace);

    private FormattedText Word(string word) =>
        new(word, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            FontSize, InkBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <param name="width">0 - one row.</param>
    private FormattedText Lay(double width)
    {
        var face = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText(_text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, FontSize, InkBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip) { TextAlignment = TextAlignment.Center, Trimming = TextTrimming.None };
        if (width > 0) text.MaxTextWidth = width;
        return text;
    }

    /// <summary>The oval's half-axes round a text box: its corners inside, a margin past them.</summary>
    private static (double A, double B) Oval(double w, double h)
    {
        var b = h / 2 + PadY;
        var k = (h / 2) / b;
        return (w / 2 / Math.Sqrt(1 - k * k) + PadX, b);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_text.Length == 0)
        {
            _layout = null;
            return new Size(0, 0);
        }
        // Never narrower than the longest word (with what is bound to it): no word is cut or broken.
        var widest = Math.Max(Math.Max(80, MinTextWidth), Math.Min(MaxTextWidth, availableSize.Width - 2 * PadX - Ink));
        // The rows by the line's length, then the narrowest width that keeps to them: the rows come out even.
        var row = Lay(0);
        var total = row.WidthIncludingTrailingWhitespace;
        var rows = total <= OneRow ? 1 : total <= TwoRows ? 2 : MostLines;
        FormattedText? best = null;
        for (var width = Math.Max(60, Math.Floor(total / rows)); width <= widest && best is null; width += 8)
        {
            var lay = Lay(width);
            if (Math.Round(lay.Height / row.Height) <= rows) best = lay;
        }
        best ??= Lay(widest);
        // Laid again at its own width: centred lines, the same breaks.
        _layout = Lay(Math.Ceiling(best.Width) + 1);
        (_a, _b) = Oval(_layout.Width, _layout.Height);
        return new Size(Math.Ceiling(2 * _a + Ink), Math.Ceiling(2 * _b + Ink + TailLength));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_layout is null)
            return;
        var cx = _a + Ink / 2;
        var cy = _b + Ink / 2;
        var oval = new EllipseGeometry(new Point(cx, cy), _a, _b);

        // The tail leaves the lower part of the oval, under the head when it can, and narrows to the tip.
        var tipX = double.IsNaN(_tailX) ? cx : Math.Clamp(_tailX, Ink, 2 * _a);
        var tip = new Point(tipX, 2 * _b + Ink / 2 + TailLength);
        var baseX = Math.Clamp(tipX, cx - _a * 0.55, cx + _a * 0.55);
        const double Half = 10;
        double Edge(double x) => cy + _b * Math.Sqrt(Math.Max(0, 1 - Math.Pow((x - cx) / _a, 2))) - 6; // just inside
        var left = new Point(baseX - Half, Edge(baseX - Half));
        var right = new Point(baseX + Half, Edge(baseX + Half));
        var tail = new StreamGeometry();
        using (var g = tail.Open())
        {
            g.BeginFigure(left, isFilled: true, isClosed: true);
            g.QuadraticBezierTo(new Point((left.X + tip.X) / 2, (left.Y + tip.Y) / 2 + 2), tip, true, true);
            g.QuadraticBezierTo(new Point((right.X + tip.X) / 2 + 3, (right.Y + tip.Y) / 2 - 2), right, true, true);
        }
        var shape = new CombinedGeometry(GeometryCombineMode.Union, oval, tail);
        shape.Freeze();
        dc.DrawGeometry(Paper, Outline, shape);
        // Centred lines are laid across MaxTextWidth from the origin.
        dc.DrawText(_layout, new Point(cx - _layout.MaxTextWidth / 2, cy - _layout.Height / 2));
    }
}
