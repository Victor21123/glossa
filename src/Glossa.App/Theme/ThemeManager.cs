using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Glossa.App.Theme;

/// <summary>
/// Owns the colour resources. The window palette lives in the application resources; the word card gets its own
/// dictionary (its theme can differ from the window's), so DynamicResource lookups inside the card find the card
/// colours first and everything else the window colours.
/// </summary>
public sealed class ThemeManager
{
    private readonly ResourceDictionary _window = new();
    private Application? _app;
    private string _setting = "system";
    private bool _filled;

    public ThemeKind Kind { get; private set; }

    public event Action? Changed;

    public void Install(Application app, string setting)
    {
        _app = app;
        app.Resources.MergedDictionaries.Add(_window);
        app.Resources["UiFont"] = UiFonts.Ui;
        app.Resources["SerifFont"] = UiFonts.Serif;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Apply(setting);
    }

    /// <summary>system (follow Windows), dark, light or disco. Repaints only when the resulting palette changes.</summary>
    public void Apply(string setting)
    {
        var kind = Resolve(setting);
        if (_filled && kind == Kind)
        {
            _setting = setting;
            return;
        }
        _setting = setting;
        Kind = kind;
        Fill(_window, Palettes.Window(Kind));
        _filled = true;
        Changed?.Invoke();
    }

    /// <summary>The card's theme: "app" follows the window, otherwise dark, light or disco.</summary>
    public ThemeKind CardKind(string cardSetting) => cardSetting switch
    {
        "dark" => ThemeKind.Dark,
        "light" => ThemeKind.Light,
        "disco" => ThemeKind.Disco,
        _ => Kind,
    };

    public static ThemeKind Resolve(string setting) => setting switch
    {
        "dark" => ThemeKind.Dark,
        "light" => ThemeKind.Light,
        "disco" => ThemeKind.Disco,
        _ => SystemUsesLightTheme() ? ThemeKind.Light : ThemeKind.Dark,
    };

    /// <summary>Writes each token as a frozen brush (key) and its colour (key + "Color").</summary>
    public static void Fill(ResourceDictionary target, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var (key, value) in tokens)
        {
            var color = Oklch.Parse(value);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            target[key + "Color"] = color;
            target[key] = brush;
        }
    }

    private static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _setting == "system")
            _app?.Dispatcher.BeginInvoke(() => Apply(_setting));
    }
}

/// <summary>
/// Onest for the interface and Literata for the card's serif translation are bundled (OFL, Assets/Fonts);
/// Japanese and Chinese fall back to the Windows fonts, picked per language so Han characters get the right forms.
/// </summary>
public static class UiFonts
{
    private static readonly Uri Base = new("pack://application:,,,/");

    public static readonly FontFamily Ui = new(Base, "./Assets/Fonts/#Onest, Yu Gothic UI, Microsoft YaHei UI");
    public static readonly FontFamily Japanese = new(Base, "./Assets/Fonts/#Onest, Yu Gothic UI");
    public static readonly FontFamily Chinese = new(Base, "./Assets/Fonts/#Onest, Microsoft YaHei UI");
    public static readonly FontFamily Serif = new(Base, "./Assets/Fonts/#Literata, Georgia");

    public static FontFamily For(string? language) => language switch
    {
        "ja" => Japanese,
        "zh" => Chinese,
        _ => Ui,
    };
}
