using System.Windows.Threading;
using Glossa.Core.Config;
using Glossa.Core.Input;

namespace Glossa.App.Input;

/// <summary>
/// The gamepad and mouse side buttons on the window's thread: the lookup combination, «Записать» in Вызов и клавиши,
/// and the buttons a still frame is steered with. Turns the input thread's sources on only while one of them is needed.
/// </summary>
public sealed class GamepadHub
{
    private readonly InputThread _input;
    private readonly Func<AppSettings> _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ComboWatcher _combo = new();
    private readonly DispatcherTimer _recordLimit = new() { Interval = TimeSpan.FromSeconds(15) };
    private ComboRecorder? _recorder;
    private Action<string?, string?>? _recorded;
    private bool _steering;

    public GamepadHub(InputThread input, Func<AppSettings> settings, Dispatcher dispatcher)
    {
        _input = input;
        _settings = settings;
        _dispatcher = dispatcher;
        input.PadChanged += state => _dispatcher.BeginInvoke(() => OnPad(state));
        input.MouseButton += button => _dispatcher.BeginInvoke(() => OnMouse(button));
        _recordLimit.Tick += (_, _) => Finish(null, "Геймпад не ответил — проверь, что он подключён, и нажми «Записать» ещё раз.");
        Apply();
    }

    /// <summary>The lookup combination was pressed (label: "LB+RB").</summary>
    public event Action<string>? ComboPressed;

    /// <summary>The chosen mouse button was pressed (label: "Кнопка 5").</summary>
    public event Action<string>? MousePressed;

    /// <summary>Every change of the held buttons while a still frame is steered by the gamepad.</summary>
    public event Action<PadButtons>? Changed;

    /// <summary>A still frame is open for the gamepad: its buttons are wanted even without a combination set.</summary>
    public bool Steering
    {
        get => _steering;
        set
        {
            _steering = value;
            Apply();
        }
    }

    public bool Recording => _recorder is not null;

    /// <summary>Settings changed: takes the combination and mouse button, and switches the sources to match.</summary>
    public void Apply()
    {
        var s = _settings();
        _combo.Combo = Pad.Parse(s.GamepadCombo);
        _input.Configure(mouse: s.MouseButton is "x1" or "x2", pad: _combo.Combo != PadButtons.None || _recorder is not null || _steering);
    }

    /// <summary>«Записать»: the next buttons held together; <paramref name="done"/> gets the combination or why none.</summary>
    public void Record(Action<string?, string?> done)
    {
        _recorder = new ComboRecorder();
        _recorded = done;
        _recordLimit.Stop();
        _recordLimit.Start();
        Apply();
    }

    public void CancelRecording()
    {
        if (_recorder is null) return;
        _recorder = null;
        _recorded = null;
        _recordLimit.Stop();
        Apply();
    }

    private void Finish(string? combo, string? error)
    {
        var done = _recorded;
        _recorder = null;
        _recorded = null;
        _recordLimit.Stop();
        Apply();
        done?.Invoke(combo, error);
    }

    private void OnPad(PadButtons state)
    {
        if (_recorder is { } recorder)
        {
            if (recorder.Update(state, out var error) is { } taken) Finish(Pad.Format(taken), null);
            else if (error is not null) Finish(null, error);
            return;
        }
        if (_combo.Update(state)) ComboPressed?.Invoke(Pad.Format(_combo.Combo));
        if (_steering) Changed?.Invoke(state);
    }

    private void OnMouse(int button)
    {
        var chosen = _settings().MouseButton switch { "x1" => 4, "x2" => 5, _ => 0 };
        if (button == chosen) MousePressed?.Invoke($"Кнопка {button}");
    }
}
