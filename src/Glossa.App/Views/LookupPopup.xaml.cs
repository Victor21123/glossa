using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using Glossa.App.Interop;
using Glossa.App.Theme;
using Glossa.Core.Config;
using Glossa.Core.Ocr;

namespace Glossa.App.Views;

/// <summary>
/// The word card. Never takes focus from the game (WS_EX_NOACTIVATE), is excluded from screen capture so it
/// never ends up in its own OCR input, and is positioned in physical pixels next to the word.
/// </summary>
public partial class LookupPopup : Window
{
    /// <summary>Transparent margin around the card that holds its shadow (Root's left margin).</summary>
    private const int ShadowMargin = 16;

    private readonly LookupViewModel _vm;
    private readonly DispatcherTimer _outsideClick = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private PixelRect _anchor;
    private IntPtr _hwnd;
    private bool _mouseWasDown;

    public event Action? SaveRequested;
    public event Action? SpeakRequested;
    public event Action? Dismissed;

    public LookupPopup(LookupViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Card.SaveRequested += () => SaveRequested?.Invoke();
        Card.SpeakRequested += () => SpeakRequested?.Invoke();
        SizeChanged += (_, _) => { if (IsVisible) Place(); };
        _outsideClick.Tick += (_, _) => CheckOutsideClick();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
            new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST));
        SetHiddenFromCapture(true);
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Glossa.exe --selftest: the card is laid out and filled as usual but kept off every screen.</summary>
    public bool Offscreen { get; set; }

    /// <summary>When hidden, the card is invisible to screen capture (and so to its own OCR and to recordings).</summary>
    public void SetHiddenFromCapture(bool hidden) =>
        Native.SetWindowDisplayAffinity(_hwnd, hidden ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);

    /// <summary>Preset, theme, accent, text size and training mode from the settings.</summary>
    public void ApplyLook(PopupSettings settings, ThemeManager theme) => Card.ApplyLook(settings, theme);

    /// <summary>Shows the card below (or above) the word without activating it.</summary>
    public void ShowNear(PixelRect wordBox)
    {
        _anchor = wordBox;
        if (!IsVisible) Show();
        UpdateLayout();
        Place();
        _mouseWasDown = Native.IsKeyDown(Native.VK_LBUTTON);
        _outsideClick.Start();
    }

    public void Dismiss()
    {
        if (!IsVisible) return;
        _outsideClick.Stop();
        Hide();
        Dismissed?.Invoke();
    }

    /// <summary>Tab: a Less card opens as Standard until it closes; in the others it folds the dictionaries.</summary>
    public void ToggleDetails()
    {
        if (_vm.Preset == "less") _vm.Expanded = !_vm.Expanded;
        else _vm.DictionariesOpen = !_vm.DictionariesOpen;
    }

    /// <summary>Space in training mode.</summary>
    public void RevealTranslation() => _vm.Revealed = true;

    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (Offscreen)
        {
            Native.SetWindowPos(_hwnd, IntPtr.Zero, -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            return;
        }
        var scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        var w = (int)Math.Ceiling(ActualWidth * scale);
        var h = (int)Math.Ceiling(ActualHeight * scale);
        var pt = new Native.POINT { X = (int)_anchor.CenterX, Y = (int)_anchor.CenterY };
        var work = Native.WorkAreaAt(pt);

        const int gap = 2;
        var x = (int)_anchor.Left - (int)(ShadowMargin * scale); // align the card edge (inside the shadow margin) with the word
        var y = (int)_anchor.Bottom + gap;
        if (y + h > work.Bottom) y = (int)_anchor.Top - h - gap;     // no room below: go above
        if (y < work.Top) y = Math.Max(work.Top, work.Bottom - h);    // no room at all: pin to the edge
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private void CheckOutsideClick()
    {
        if (Offscreen) return;
        var down = Native.IsKeyDown(Native.VK_LBUTTON) || Native.IsKeyDown(Native.VK_RBUTTON);
        if (down && !_mouseWasDown)
        {
            Native.GetCursorPos(out var p);
            Native.GetWindowRect(_hwnd, out var r);
            if (p.X < r.Left || p.X > r.Right || p.Y < r.Top || p.Y > r.Bottom) Dismiss();
        }
        _mouseWasDown = down;
    }
}

/// <summary>Collapses an element whose bound value is null or empty; "invert" does the opposite.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value is null || value is string { Length: 0 };
        if (parameter as string == "invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound flag is false.</summary>
public sealed class FalseToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Enables a control while the bound flag (usually Busy) is false.</summary>
public sealed class InverseBool : IValueConverter
{
    public static InverseBool Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
