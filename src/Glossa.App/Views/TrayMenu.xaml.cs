using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shapes;
using Glossa.App.Interop;

namespace Glossa.App.Views;

/// <summary>What the tray menu shows when it opens.</summary>
/// <remarks>
/// <paramref name="TranslateOnly"/>: «Только перевод», where the dictionary is hidden and the item opens «Главная».
/// <paramref name="UpdateItem"/>: the text of the item that opens the release page while a newer version waits; null - no item.
/// <paramref name="CanLoad"/>: a local model ready to load, so "Загрузить модель в фон" / "Выгрузить модель" shows.
/// <paramref name="AiBusy"/>: the model loading, or about to leave after a lookup - the item waits.
/// </remarks>
public sealed record TrayState(string Status, bool AiLoaded, string Mode, string Hotkey, bool LookupOn, bool TranslateOnly = false,
    string? UpdateItem = null, bool CanLoad = false, bool AiBusy = false);

/// <summary>The tray icon's menu, drawn like the rest of Glossa; it closes as soon as it loses focus.</summary>
public partial class TrayMenu : Window
{
    /// <summary>Transparent margin around the panel that holds its shadow (Panel's margin).</summary>
    private const double ShadowRight = 18, ShadowBottom = 24;

    private bool _lookupOn;

    public event Action? OpenRequested;
    public event Action? StudyRequested;
    public event Action? SettingsRequested;
    public event Action? UpdateRequested;
    public event Action? ExitRequested;
    public event Action<string>? ModeChanged;
    public event Action<bool>? LookupToggled;

    /// <summary>"Загрузить модель в фон" or "Выгрузить модель".</summary>
    public event Action? AiToggled;

    public TrayMenu()
    {
        InitializeComponent();
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Hide();
            e.Handled = true;
        };
    }

    /// <summary>Opens with the panel's bottom-right corner at the click (physical pixels), kept inside the work area.</summary>
    public void ShowAt(int x, int y, TrayState state)
    {
        Fill(state);
        Show();
        Activate();
        UpdateLayout();
        var hwnd = new WindowInteropHelper(this).Handle;
        var scale = Native.GetDpiForWindow(hwnd) / 96.0;
        var w = (int)Math.Ceiling(ActualWidth * scale);
        var h = (int)Math.Ceiling(ActualHeight * scale);
        var work = Native.WorkAreaAt(new Native.POINT { X = x, Y = y });
        var right = (int)(ShadowRight * scale);
        var bottom = (int)(ShadowBottom * scale);
        var left = Math.Clamp(x - w + right, work.Left - right, Math.Max(work.Left - right, work.Right - w + right));
        var top = Math.Clamp(y - h + bottom, work.Top, Math.Max(work.Top, work.Bottom - h + bottom));
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
        Activate();
    }

    internal void Fill(TrayState state)
    {
        StateText.Text = state.Status;
        UpdateItem.Content = state.UpdateItem;
        UpdateItem.Visibility = state.UpdateItem is null ? Visibility.Collapsed : Visibility.Visible;
        OpenItem.Content = state.TranslateOnly ? "Открыть Glossa" : "Открыть словарь";
        StudyItem.Visibility = state.TranslateOnly ? Visibility.Collapsed : Visibility.Visible;
        AiItem.Visibility = state.CanLoad ? Visibility.Visible : Visibility.Collapsed;
        AiItem.IsEnabled = !state.AiBusy;
        AiItem.Content = state.AiBusy ? (state.AiLoaded ? "Модель выгрузится после поиска" : "Модель загружается...")
            : state.AiLoaded ? "Выгрузить модель" : "Загрузить модель в фон";
        StateDot.SetResourceReference(Shape.FillProperty, state.AiLoaded ? "Good" : "Surface");
        StateDot.SetResourceReference(Shape.StrokeProperty, state.AiLoaded ? "Good" : "Muted");
        ModeAuto.IsChecked = state.Mode == "auto";
        ModeLow.IsChecked = state.Mode == "lowvram";
        ModeOff.IsChecked = state.Mode == "off";
        _lookupOn = state.LookupOn;
        LookupSwitch.IsChecked = state.LookupOn;
        LookupText.Text = $"Поиск по {Settings.KeyCaps.Display(state.Hotkey)}";
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenRequested?.Invoke();
    }

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        Hide();
        UpdateRequested?.Invoke();
    }

    private void OnStudy(object sender, RoutedEventArgs e)
    {
        Hide();
        StudyRequested?.Invoke();
    }

    private void OnAi(object sender, RoutedEventArgs e)
    {
        Hide();
        AiToggled?.Invoke();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        Hide();
        SettingsRequested?.Invoke();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Hide();
        ExitRequested?.Invoke();
    }

    /// <summary>The mode applies at once; the menu stays open so the choice is seen.</summary>
    private void OnMode(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string mode }) ModeChanged?.Invoke(mode);
    }

    private void OnToggleLookup(object sender, RoutedEventArgs e)
    {
        _lookupOn = !_lookupOn;
        LookupSwitch.IsChecked = _lookupOn;
        LookupToggled?.Invoke(_lookupOn);
    }
}
