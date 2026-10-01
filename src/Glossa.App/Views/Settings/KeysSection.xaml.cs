using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Glossa.App.Lookup;
using Glossa.App.ViewModels;
using Glossa.Core.Input;

namespace Glossa.App.Views.Settings;

public partial class KeysSection : UserControl
{
    /// <summary>The card's own keys (App.OnPopupVisibility registers them while the card is open).</summary>
    private static readonly (string Name, string Key)[] CardKeyList =
    [
        ("Подробнее или короче", "Tab"), ("Показать перевод", "Space"), ("Произнести", "P"), ("Сохранить в словарь", "S"),
        ("Исправить слово", "F2"), ("Закрыть", "Escape"),
    ];

    private readonly AppServices _services;
    private readonly SettingsViewModel _model;

    /// <summary>Which combination the pressed keys are for: the lookup or «Открыть Glossa».</summary>
    private bool _forWindow;

    /// <summary>Takes the key events while recording; a fresh one per recording.</summary>
    private KeyRecorder _recorder = new();

    /// <summary>The lookup and window hotkeys are let go while recording (RegisterHotKey would swallow them).</summary>
    private bool _suspended;

    /// <summary>Design snapshots only: skips the check that the recorder got the keyboard.</summary>
    private bool _designPreview;

    public KeysSection(AppServices services, SettingsViewModel model)
    {
        InitializeComponent();
        _services = services;
        _model = model;
        DataContext = model;
        // With one monitor there is nowhere else to put the card: only what works is shown.
        OtherMonitorRow.Visibility = Glossa.App.Interop.Native.MonitorBounds().Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Focusable = true;
        Recorder.PreviewKeyDown += OnCaptureDown;
        Recorder.PreviewKeyUp += OnCaptureUp;
        Recorder.RequestBringIntoView += (_, e) => e.Handled = true; // the 1px recorder sits at the top: do not scroll the page there
        Recorder.LostKeyboardFocus += (_, _) =>
        {
            if (Capturing) StopCapture(); // the keyboard went elsewhere (Alt+Tab, a click)
        };
        PreviewMouseDown += OnCaptureMouse;
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
        _recorder = new KeyRecorder();
        Capturing = true;
        var (change, cancel, result) = forWindow ? (WindowChangeButton, WindowCancelButton, WindowResult) : (ChangeButton, CancelButton, HotkeyResult);
        change.Content = "Жду нажатия...";
        change.IsEnabled = false;
        cancel.Visibility = Visibility.Visible;
        result.Visibility = Visibility.Collapsed;
        ShowHeld();
        if (_services.SuspendHotkeys is { } suspend)
        {
            suspend(true);
            _suspended = true;
        }
        // The clicked button is disabled now: the keyboard moves once the click is through.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!Capturing || _designPreview) return;
            Keyboard.Focus(Recorder);
            if (Recorder.IsKeyboardFocused) return;
            // The keyboard did not move: nothing would be heard, so do not leave hotkeys let go.
            var target = _forWindow ? WindowResult : HotkeyResult;
            StopCapture();
            Result(target, ok: false, "Не удалось начать запись: нажми \"Изменить\" ещё раз.");
        });
    }

    private void OnCancel(object sender, RoutedEventArgs e) => StopCapture();

    private void StopCapture()
    {
        var was = Capturing;
        Capturing = false;
        foreach (var (change, cancel) in new[] { (ChangeButton, CancelButton), (WindowChangeButton, WindowCancelButton) })
        {
            change.Content = "Изменить";
            change.IsEnabled = true;
            cancel.Visibility = Visibility.Collapsed;
        }
        if (_suspended)
        {
            _suspended = false;
            _services.SuspendHotkeys?.Invoke(false);
        }
        if (was) ShowHotkey();
    }

    /// <summary>While recording the key caps show what is held right now.</summary>
    private void ShowHeld()
    {
        var held = _recorder.Held;
        ContentControl slot = _forWindow ? WindowCaps : HotkeyCaps;
        if (held.Length > 0)
        {
            slot.Content = KeyCaps.Build(held, big: true);
            return;
        }
        var hint = new TextBlock { Text = "Нажми клавишу или сочетание", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        slot.Content = hint;
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

    /// <summary>The WPF key behind a key event: Alt and IME or dead keys arrive wrapped.</summary>
    private static string? KeyName(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        return key == Key.None ? null : key.ToString();
    }

    private void OnCaptureDown(object sender, KeyEventArgs e)
    {
        if (!Capturing) return;
        e.Handled = true;
        // A repeat can be the Enter that clicked "Изменить", still held: only a real press counts.
        if (e.IsRepeat || KeyName(e) is not { } key) return;
        _recorder.Down(key);
        ShowHeld();
    }

    /// <summary>
    /// The key-up ends the recording once everything is released. It is handled too: an Alt release nobody handled puts
    /// the window into the menu mode of Windows and knocks the recording off.
    /// </summary>
    private void OnCaptureUp(object sender, KeyEventArgs e)
    {
        if (!Capturing) return;
        e.Handled = true;
        if (KeyName(e) is not { } key) return;
        if (_recorder.Up(key) is { } record) Take(record);
        else ShowHeld();
    }

    /// <summary>A side mouse button while recording binds the mouse button; the keyboard key stays as it was.</summary>
    private void OnCaptureMouse(object sender, MouseButtonEventArgs e)
    {
        if (!Capturing) return;
        var button = e.ChangedButton switch { MouseButton.XButton1 => "x1", MouseButton.XButton2 => "x2", _ => "" };
        if (_recorder.Mouse(button) is not { } record) return;
        e.Handled = true;
        Take(record);
    }

    /// <summary>What the recording ended with: save the key, bind the mouse button, or say why not.</summary>
    private void Take(KeyRecord record)
    {
        var forWindow = _forWindow;
        var target = forWindow ? WindowResult : HotkeyResult;
        StopCapture();
        switch (record.Kind)
        {
            case KeyRecordKind.Cancelled:
                return;
            case KeyRecordKind.Mouse:
                if (forWindow)
                {
                    Result(target, ok: false, "Кнопка мыши открывает только поиск слова: здесь нужна клавиша.");
                    return;
                }
                _model.MouseButton = record.MouseButton!;
                Result(target, ok: true, $"Готово: кнопка мыши {(record.MouseButton == "x1" ? "4" : "5")} тоже ищет слово. Клавиша осталась {KeyCaps.Display(_model.Hotkey)}.");
                return;
            case KeyRecordKind.Refused:
                Result(target, ok: false, record.Refusal == RecordRefusal.ModifiersOnly
                    ? "Одной Ctrl, Alt, Shift или Win мало: нажми клавишу, одну или вместе с ними."
                    : "Две обычные клавиши сразу пока нельзя: нажми одну клавишу, одну или вместе с Ctrl, Alt, Shift или Win.");
                return;
        }

        var spec = record.Spec!.Format();
        var shown = KeyCaps.Display(spec);
        if (record.Verdict == KeyVerdict.CardKey)
        {
            Result(target, ok: false, $"{shown} занята карточкой слова: добавь Ctrl или Alt, либо выбери другую клавишу.");
            return;
        }
        var other = forWindow ? _model.Hotkey : _model.WindowHotkey;
        if (KeySpec.Parse(other) == record.Spec) // parsed: a hand-edited "alt+q" or "Control+G" matches too
        {
            Result(target, ok: false, $"{shown} уже занято другим действием Glossa.");
            return;
        }
        var saved = forWindow ? _model.ChangeWindowHotkey(spec) : _model.ChangeHotkey(spec);
        ShowHotkey();
        if (!saved)
        {
            Result(target, ok: false, forWindow
                ? $"{shown} занято другой программой."
                : $"{shown} занято другой программой - осталось {KeyCaps.Display(_model.Hotkey)}.");
            return;
        }
        var done = forWindow ? $"Готово: {shown} открывает Glossa." : $"Готово: теперь поиск по {shown}.";
        // A bare typing key is saved and works, but nowhere else: said in the warning style, with what avoids it.
        if (record.Verdict == KeyVerdict.Typing) Result(target, ok: false, TypingNote(done, shown));
        else Result(target, ok: true, done);
    }

    private static string TypingNote(string done, string shown)
        => $"{done} Пока Glossa работает, {shown} не печатается ни в одной программе. Если это мешает, запиши с Ctrl или Alt.";

    /// <summary>Design snapshots: the section as it looks while the given keys are held.</summary>
    internal void PreviewRecording(params string[] held)
    {
        _designPreview = true; // an off-screen window never gets the keyboard: do not read that as a failed start
        StartCapture(forWindow: false);
        foreach (var key in held) _recorder.Down(key);
        ShowHeld();
    }

    /// <summary>Design snapshots: the warning after a typing key was taken.</summary>
    internal void PreviewTypingWarning()
    {
        StopCapture();
        Result(HotkeyResult, ok: false, TypingNote("Готово: теперь поиск по Q.", "Q"));
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
