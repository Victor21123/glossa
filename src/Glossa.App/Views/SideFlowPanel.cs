using System.Windows;
using System.Windows.Controls;

namespace Glossa.App.Views;

/// <summary>
/// Two children: content and a side block (buttons). Side by side, the side block at the right edge, while the content
/// fits in what is left at its natural width; otherwise the side block drops under it, right-aligned, so the content
/// keeps the whole line (a word is never squeezed by the buttons). <see cref="FlexFirst"/> keeps them side by side always
/// (the content then wraps itself, as the fields of the edit row do).
/// </summary>
public sealed class SideFlowPanel : Panel
{
    private const double Gap = 12;
    private bool _stacked;

    public static readonly DependencyProperty FlexFirstProperty = DependencyProperty.Register(
        nameof(FlexFirst), typeof(bool), typeof(SideFlowPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public bool FlexFirst
    {
        get => (bool)GetValue(FlexFirstProperty);
        set => SetValue(FlexFirstProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        if (Children.Count < 2) return Children.Count == 1 ? Measure1(Children[0], available) : default;
        UIElement first = Children[0], side = Children[1];
        var open = new Size(double.PositiveInfinity, double.PositiveInfinity);
        side.Measure(open);
        first.Measure(open);
        var sideWidth = side.DesiredSize.Width;
        var natural = first.DesiredSize.Width;
        _stacked = !FlexFirst && !double.IsInfinity(available.Width) && natural + Gap + sideWidth > available.Width;
        if (_stacked)
        {
            first.Measure(new Size(available.Width, double.PositiveInfinity));
            return new Size(available.Width, first.DesiredSize.Height + Gap / 2 + side.DesiredSize.Height);
        }
        var room = double.IsInfinity(available.Width) ? double.PositiveInfinity : Math.Max(0, available.Width - sideWidth - Gap);
        first.Measure(new Size(room, double.PositiveInfinity));
        var width = double.IsInfinity(available.Width) ? first.DesiredSize.Width + Gap + sideWidth : available.Width;
        return new Size(width, Math.Max(first.DesiredSize.Height, side.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2)
        {
            if (Children.Count == 1) Children[0].Arrange(new Rect(finalSize));
            return finalSize;
        }
        UIElement first = Children[0], side = Children[1];
        var sideWidth = side.DesiredSize.Width;
        if (_stacked)
        {
            var h = first.DesiredSize.Height;
            first.Arrange(new Rect(0, 0, finalSize.Width, h));
            side.Arrange(new Rect(Math.Max(0, finalSize.Width - sideWidth), h + Gap / 2, sideWidth, side.DesiredSize.Height));
        }
        else
        {
            first.Arrange(new Rect(0, 0, Math.Max(0, finalSize.Width - sideWidth - Gap), finalSize.Height));
            side.Arrange(new Rect(Math.Max(0, finalSize.Width - sideWidth), 0, sideWidth, side.DesiredSize.Height));
        }
        return finalSize;
    }

    private static Size Measure1(UIElement only, Size available)
    {
        only.Measure(available);
        return only.DesiredSize;
    }
}
