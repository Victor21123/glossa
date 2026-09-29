using System.Windows;

namespace Glossa.App.Theme;

/// <summary>Placeholder text for text boxes (WPF has none); the TextBox template shows it while the box is empty.</summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Hint), new FrameworkPropertyMetadata(null));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);

    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);
}
