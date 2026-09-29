using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Glossa.Core.Dictionaries;

namespace Glossa.App.Views;

/// <summary>Renders a dictionary article body (<see cref="DictMarkup"/>): one wrapped TextBlock per line.</summary>
public sealed class MarkupView : StackPanel
{
    public static readonly DependencyProperty MarkupProperty = DependencyProperty.Register(
        nameof(Markup), typeof(string), typeof(MarkupView), new PropertyMetadata(null, (d, _) => ((MarkupView)d).Render()));

    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.Register(
        nameof(MaxLines), typeof(int), typeof(MarkupView), new PropertyMetadata(40, (d, _) => ((MarkupView)d).Render()));

    public string? Markup
    {
        get => (string?)GetValue(MarkupProperty);
        set => SetValue(MarkupProperty, value);
    }

    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    private void Render()
    {
        Children.Clear();
        if (string.IsNullOrEmpty(Markup)) return;
        var lines = DictMarkup.Parse(Markup);
        foreach (var line in lines.Take(MaxLines))
        {
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(line.Indent * 14, 1, 0, 1),
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "InkSoft");
            foreach (var span in line.Spans) tb.Inlines.Add(RunFor(span));
            Children.Add(tb);
        }
        if (lines.Count > MaxLines)
        {
            var more = new TextBlock { Text = $"... ещё строк: {lines.Count - MaxLines}", FontSize = 11.5 };
            more.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Children.Add(more);
        }
    }

    /// <summary>Colours come from the current theme (the card's own palette inside the card).</summary>
    private static Run RunFor(MarkupSpan span)
    {
        var run = new Run(span.Text);
        var s = span.Style;
        if (s.HasFlag(SpanStyle.Bold)) run.FontWeight = FontWeights.SemiBold;
        if (s.HasFlag(SpanStyle.Italic) || s.HasFlag(SpanStyle.Label) || s.HasFlag(SpanStyle.Example)) run.FontStyle = FontStyles.Italic;
        if (s.HasFlag(SpanStyle.Underline) || s.HasFlag(SpanStyle.Ref)) run.TextDecorations = TextDecorations.Underline;
        var key = s.HasFlag(SpanStyle.Accent) ? "AccentText"
            : s.HasFlag(SpanStyle.Ref) ? "Ink"
            : s.HasFlag(SpanStyle.Example) || s.HasFlag(SpanStyle.Muted) || s.HasFlag(SpanStyle.Label) ? "Muted"
            : null;
        if (key is not null) run.SetResourceReference(TextElement.ForegroundProperty, key);
        if (s.HasFlag(SpanStyle.Accent)) run.FontWeight = FontWeights.SemiBold;
        return run;
    }
}
