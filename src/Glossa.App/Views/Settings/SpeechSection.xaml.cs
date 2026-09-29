using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Glossa.App.ViewModels;

namespace Glossa.App.Views.Settings;

public partial class SpeechSection : UserControl
{
    /// <summary>What «Прослушать» says in each language.</summary>
    private static readonly Dictionary<string, string> Samples = new() { ["en"] = "reconsider", ["ja"] = "合体する", ["zh"] = "房东" };

    private readonly AppServices _services;
    private readonly SettingsViewModel _model;

    public SpeechSection(AppServices services, SettingsViewModel model)
    {
        InitializeComponent();
        _services = services;
        _model = model;
        DataContext = model;
        CacheFolder.Content = services.Speech.CacheDir;
        Loaded += async (_, _) => await ShowCache();
    }

    private async Task ShowCache() => CacheSize.Text = Sizes.Format(await Task.Run(_services.Speech.CacheBytes));

    private async void OnListen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VoiceRow row) await Say(Samples[row.Language], row.Language);
    }

    private async void OnSay(object sender, RoutedEventArgs e)
    {
        var text = SayBox.Text.Trim();
        if (text.Length > 0) await Say(text, Guess(text));
    }

    private async Task Say(string text, string language)
    {
        try
        {
            if (!await _services.Speech.SpeakAsync(text, language))
                SayNote.Text = $"Нет голоса для языка «{language}» — как добавить, написано ниже.";
            await ShowCache();
        }
        catch (Exception ex)
        {
            SayNote.Text = "Не получилось: " + ex.Message;
            _services.Log.Error("TTS check", ex);
        }
    }

    /// <summary>Kana means Japanese; Han alone follows «Строка из одних иероглифов»; the rest is English.</summary>
    private string Guess(string text)
    {
        if (text.Any(c => c is >= '぀' and <= 'ヿ')) return "ja";
        if (text.Any(c => c is >= '一' and <= '鿿')) return _services.Settings.PreferredCjk;
        return "en";
    }

    private async void OnClearCache(object sender, RoutedEventArgs e)
    {
        _services.Speech.ClearCache();
        await ShowCache();
    }

    private void OnOpenSpeechSettings(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true });

    private void OnRecheck(object sender, RoutedEventArgs e)
    {
        foreach (var row in _model.Voices) row.Refresh();
    }
}
