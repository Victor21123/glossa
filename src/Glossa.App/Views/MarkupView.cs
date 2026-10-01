using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Glossa.Core.Dictionaries;

namespace Glossa.App.Views;

/// <summary>
/// Renders a dictionary article body (<see cref="DictMarkup"/>): one wrapped TextBlock per line, or, with
/// <see cref="Selectable"/>, one read-only RichTextBox the mouse can select in (one paragraph per line).
/// </summary>
public sealed class MarkupView : StackPanel
{
    public static readonly DependencyProperty MarkupProperty = DependencyProperty.Register(
        nameof(Markup), typeof(string), typeof(MarkupView), new PropertyMetadata(null, (d, _) => ((MarkupView)d).Render()));

    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.Register(
        nameof(MaxLines), typeof(int), typeof(MarkupView), new PropertyMetadata(40, (d, _) => ((MarkupView)d).Render()));

    public static readonly DependencyProperty SelectableProperty = DependencyProperty.Register(
        nameof(Selectable), typeof(bool), typeof(MarkupView), new PropertyMetadata(false, (d, _) => ((MarkupView)d).Render()));

    public bool Selectable
    {
        get => (bool)GetValue(SelectableProperty);
        set => SetValue(SelectableProperty, value);
    }

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
        if (Selectable)
        {
            RenderSelectable(lines);
            return;
        }
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

    private void RenderSelectable(IReadOnlyList<MarkupLine> lines)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        foreach (var line in lines.Take(MaxLines))
        {
            var p = new Paragraph { Margin = new Thickness(line.Indent * 14, 2, 0, 0) };
            p.SetResourceReference(TextElement.ForegroundProperty, "InkSoft");
            foreach (var span in line.Spans) p.Inlines.Add(RunFor(span));
            doc.Blocks.Add(p);
        }
        if (lines.Count > MaxLines)
        {
            var more = new Paragraph(new Run($"... ещё строк: {lines.Count - MaxLines}")) { Margin = new Thickness(0), FontSize = 11.5 };
            more.SetResourceReference(TextElement.ForegroundProperty, "Muted");
            doc.Blocks.Add(more);
        }
        // Paragraph margins collapse (TextBlocks in a stack do not): 2 px above each line, the RichTextBox shifted to match.
        var box = new RichTextBox { Document = doc, Margin = new Thickness(0, -1, 0, 1) };
        box.SetResourceReference(StyleProperty, "SelectableDocument");
        System.Windows.Automation.AutomationProperties.SetName(box, "Статья словаря");
        Children.Add(box);
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
