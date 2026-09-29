using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shapes;
using Glossa.App.Interop;

namespace Glossa.App.Views;

/// <summary>What the tray menu shows when it opens.</summary>
public sealed record TrayState(string Status, bool AiLoaded, string Mode, string Hotkey, bool LookupOn);

/// <summary>The tray icon's menu, drawn like the rest of Glossa; it closes as soon as it loses focus.</summary>
public partial class TrayMenu : Window
{
    /// <summary>Transparent margin around the panel that holds its shadow (Panel's margin).</summary>
    private const double ShadowRight = 18, ShadowBottom = 24;

    private bool _lookupOn;

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<string>? ModeChanged;
    public event Action<bool>? LookupToggled;

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
