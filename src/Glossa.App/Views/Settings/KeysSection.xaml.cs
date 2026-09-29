using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glossa.App.Lookup;
using Glossa.App.ViewModels;

namespace Glossa.App.Views.Settings;

public partial class KeysSection : UserControl
{
    /// <summary>The card's own keys (App.OnPopupVisibility registers them while the card is open).</summary>
    private static readonly (string Name, string Key)[] CardKeyList =
    [
        ("Подробнее или короче", "Tab"), ("Показать перевод", "Space"), ("Произнести", "P"), ("Сохранить в словарь", "S"),
        ("Закрыть", "Escape"),
    ];

    private readonly AppServices _services;
    private readonly SettingsViewModel _model;

    /// <summary>Which combination the pressed keys are for: the lookup or «Открыть Glossa».</summary>
    private bool _forWindow;

    public KeysSection(AppServices services, SettingsViewModel model)
    {
        InitializeComponent();
        _services = services;
        _model = model;
        DataContext = model;
        Focusable = true;
        PreviewKeyDown += OnCaptureKey;
        foreach (var (name, key) in CardKeyList)
        {
            var row = new DockPanel { Height = 44, Margin = new Thickness(0, 0, 32, 0) };
            var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
            line.SetResourceReference(Border.BorderBrushProperty, "RuleSoft");
            var caps = KeyCaps.Build(key, big: false);
            DockPanel.SetDock(caps, Dock.Right);
            row.Children.Add(caps);
            var label = new TextBlock { Text = name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "InkSoft");
            row.Children.Add(label);
            CardKeys.Children.Add(line);
        }
        ShowHotkey();
        ShowPad();
        ShowLog();
        Loaded += (_, _) =>
        {
            _services.LookupReported += OnReported;
            ShowLog();
        };
        Unloaded += (_, _) =>
        {
            _services.LookupReported -= OnReported;
            StopCapture();
            _services.CancelGamepadRecording?.Invoke();
        };
    }

    /// <summary>True while waiting for the new combination (the window leaves its own shortcuts alone).</summary>
    public bool Capturing { get; private set; }

    private void ShowHotkey()
    {
        HotkeyCaps.Content = KeyCaps.Build(_model.Hotkey, big: true);
        CheckTitle.Text = $"Нажми {KeyCaps.Display(_model.Hotkey)} где угодно";
        HotkeyState.Style = (Style)FindResource(_model.HotkeyActive ? "OkText" : "WarnText");
        HotkeyState.Content = _model.HotkeyActive
            ? $"{KeyCaps.Display(_model.Hotkey)} свободно, Glossa его слушает"
            : $"{KeyCaps.Display(_model.Hotkey)} занято другой программой - выбери другое";

        var window = _model.WindowHotkey;
        WindowCaps.Content = window.Length > 0 ? KeyCaps.Build(window, big: true) : NotSet();
        WindowOffButton.Visibility = window.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        WindowHotkeyState.Visibility = window.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        WindowHotkeyState.Style = (Style)FindResource(_model.WindowHotkeyActive ? "OkText" : "WarnText");
        WindowHotkeyState.Content = _model.WindowHotkeyActive
            ? $"{KeyCaps.Display(window)} открывает Glossa"
            : $"{KeyCaps.Display(window)} занято другой программой";
    }

    /// <summary>The gamepad combination as key caps, or «Не назначено».</summary>
    private void ShowPad()
    {
        var combo = Glossa.Core.Input.Pad.Parse(_model.GamepadCombo);
        PadCaps.Content = combo == Glossa.Core.Input.PadButtons.None
            ? NotSet()
            : KeyCaps.Build(string.Join("+", Glossa.Core.Input.Pad.Captions(combo)), big: true);
        PadOffButton.Visibility = combo == Glossa.Core.Input.PadButtons.None ? Visibility.Collapsed : Visibility.Visible;
    }

    private TextBlock NotSet()
    {
        var text = new TextBlock { Text = "Не назначено", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return text;
    }

    private void OnChange(object sender, RoutedEventArgs e) => StartCapture(forWindow: false);

    private void OnChangeWindow(object sender, RoutedEventArgs e) => StartCapture(forWindow: true);

    private void StartCapture(bool forWindow)
    {
        StopCapture();
        _forWindow = forWindow;
        Capturing = true;
        var (change, cancel, result) = forWindow ? (WindowChangeButton, WindowCancelButton, WindowResult) : (ChangeButton, CancelButton, HotkeyResult);
        change.Content = "Нажми новое сочетание...";
        change.IsEnabled = false;
        cancel.Visibility = Visibility.Visible;
        result.Visibility = Visibility.Collapsed;
        Keyboard.Focus(this);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => StopCapture();

    private void StopCapture()
    {
        Capturing = false;
        foreach (var (change, cancel) in new[] { (ChangeButton, CancelButton), (WindowChangeButton, WindowCancelButton) })
        {
            change.Content = "Изменить";
            change.IsEnabled = true;
            cancel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnWindowOff(object sender, RoutedEventArgs e)
    {
        StopCapture();
        _model.ChangeWindowHotkey("");
        ShowHotkey();
        Result(WindowResult, ok: true, "Сочетание убрано: окно открывается из трея или ярлыком.");
    }

    private void OnRecordPad(object sender, RoutedEventArgs e)
    {
        if (_services.RecordGamepad is null) return;
        PadRecordButton.Content = "Нажми сочетание на геймпаде...";
        PadRecordButton.IsEnabled = false;
        PadResult.Visibility = Visibility.Collapsed;
        _services.RecordGamepad((combo, error) =>
        {
            PadRecordButton.Content = "Записать";
            PadRecordButton.IsEnabled = true;
            if (combo is not null)
            {
                _model.GamepadCombo = combo;
                ShowPad();
                Result(PadResult, ok: true, $"Готово: стоп-кадр по {string.Join(" + ", Glossa.Core.Input.Pad.Captions(Glossa.Core.Input.Pad.Parse(combo)))}.");
            }
            else
            {
                Result(PadResult, ok: false, error ?? "Сочетание не записано.");
            }
        });
    }

    private void OnPadOff(object sender, RoutedEventArgs e)
    {
        _model.GamepadCombo = "";
        ShowPad();
        Result(PadResult, ok: true, "Геймпад больше не вызывает Glossa.");
    }

    /// <summary>Takes the pressed combination ("Ctrl+Shift+D"); Esc alone cancels.</summary>
    private void OnCaptureKey(object sender, KeyEventArgs e)
    {
        if (!Capturing) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.None) return;
        var mods = Keyboard.Modifiers;
        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            StopCapture();
            return;
        }

        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
        var spec = string.Join("+", parts);
        var forWindow = _forWindow;
        var target = forWindow ? WindowResult : HotkeyResult;
        StopCapture();

        // A plain letter as a system-wide hotkey would stop that letter from typing anywhere.
        var function = key is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll;
        if (!function && (mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
        {
            Result(target, ok: false, $"{KeyCaps.Display(spec)} не подходит: нужна Ctrl, Alt или Win, иначе клавиша перестанет работать везде.");
            return;
        }
        var other = forWindow ? _model.Hotkey : _model.WindowHotkey;
        if (string.Equals(spec, other, StringComparison.OrdinalIgnoreCase))
        {
            Result(target, ok: false, $"{KeyCaps.Display(spec)} уже занято другим действием Glossa.");
            return;
        }
        if (forWindow)
        {
            var ok = _model.ChangeWindowHotkey(spec);
            ShowHotkey();
            Result(target, ok, ok
                ? $"Готово: {KeyCaps.Display(spec)} открывает Glossa."
                : $"{KeyCaps.Display(spec)} занято другой программой.");
        }
        else
        {
            var ok = _model.ChangeHotkey(spec);
            ShowHotkey();
            Result(target, ok, ok
                ? $"Готово: теперь поиск по {KeyCaps.Display(spec)}."
                : $"{KeyCaps.Display(spec)} занято другой программой - осталось {KeyCaps.Display(_model.Hotkey)}.");
        }
    }

    private void Result(ContentControl target, bool ok, string text)
    {
        target.Style = (Style)FindResource(ok ? "OkText" : "WarnText");
        target.Content = text;
        target.Visibility = Visibility.Visible;
    }

    private void OnReported(LookupReport report) => Dispatcher.Invoke(ShowLog);

    private void ShowLog()
    {
        Log.Children.Clear();
        var recent = _services.RecentLookups.Take(6).ToList();
        LogEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var r in recent)
        {
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
            line.SetResourceReference(Border.BorderBrushProperty, "RuleSoft");
            var time = new TextBlock { Text = r.At.ToString("HH:mm:ss"), Width = 62, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            time.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            row.Children.Add(time);
            var caps = KeyCaps.Build(r.Keys, big: false);
            caps.Margin = new Thickness(0, 0, 12, 0);
            row.Children.Add(caps);
            FrameworkElement result;
            if (r.Ok)
            {
                result = new ContentControl { Content = r.Result, Style = (Style)FindResource("OkText"), FontSize = 13 };
            }
            else
            {
                var text = new TextBlock { Text = r.Result, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                result = text;
            }
            row.Children.Add(result);
            Log.Children.Add(line);
        }
    }
}

/// <summary>Key caps for a combination such as "Alt+Q": one boxed key per part, joined by pluses.</summary>
public static class KeyCaps
{
    public static string Name(string part) => part switch
    {
        "Escape" => "Esc",
        "Space" => "Пробел",
        "Return" or "Enter" => "Enter",
        "Next" => "PageDown",
        "Prior" => "PageUp",
        "Scroll" => "ScrollLock",
        _ => part,
    };

    /// <summary>"Ctrl+Shift+D" as text for messages.</summary>
    public static string Display(string spec) => string.Join("+", spec.Split('+').Select(Name));

    public static StackPanel Build(string spec, bool big)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                var plus = new TextBlock { Text = "+", FontSize = 13, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                plus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                panel.Children.Add(plus);
            }
            var cap = new Border
            {
                Style = (Style)Application.Current.FindResource("KbdBox"),
                MinWidth = big ? 34 : 24,
                MinHeight = big ? 34 : 24,
                Padding = big ? new Thickness(11, 0, 11, 0) : new Thickness(7, 0, 7, 0),
                Child = new TextBlock
                {
                    Text = Name(parts[i]), FontSize = big ? 15 : 12.5, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            panel.Children.Add(cap);
        }
        return panel;
    }
}
