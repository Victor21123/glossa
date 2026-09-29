using System.Diagnostics;
using System.Windows.Threading;
using Glossa.App.Capture;
using Glossa.App.Games;
using Glossa.App.Input;
using Glossa.App.Interop;
using Glossa.App.Views;
using Glossa.Core.Config;
using Glossa.Core.Games;
using Glossa.Core.Input;
using Glossa.Core.Library;
using Glossa.Core.Llm;
using Glossa.Core.Logging;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.App.Lookup;

/// <summary>
/// What a lookup does to the game (Во время поиска, general or per game): leave it alone; show a still frame of the
/// screen to click words on (the game runs on beneath it); or pause the game's process while the card is open. The
/// gamepad combination always opens a still frame whose words are chosen with the D-pad. Every way back gives the game
/// its focus back and, if it was paused, wakes it.
/// </summary>
public sealed class LookupSessions
{
    internal const string MouseHint = "Щёлкни слово, Esc или правый щелчок - вернуться в игру";
    internal const string ZoneHint = "Обведи мышью текст для перевода, щелчок - абзац под курсором, Esc - вернуться в игру";
    internal const string PadHint = "Крестовина - слово, A - искать, B - назад, X - сохранить, Y - произнести";

    private readonly Func<AppSettings> _settings;
    private readonly GameRegistry _games;
    private readonly ScreenCapture _capture;
    private readonly OcrEngine _ocr;
    private readonly WordLookup _words;
    private readonly LookupController _controller;
    private readonly LookupPopup _popup;
    private readonly ResumeGuardClient _guard;
    private readonly GamepadHub _pad;
    private readonly ILog _log;
    private readonly Func<IEnumerable<SavedWord>> _saved;
    private readonly LiveTranslator _live;
    private readonly PadRepeat _repeat = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _repeatTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly HashSet<string> _told = [];
    private FrozenFrame? _frame;
    private Session? _session;

    private sealed class Session
    {
        public required GameWindow Game { get; init; }
        public required LookupContext Context { get; init; }
        public required string Cjk { get; init; }
        public CapturedFrame? Still { get; init; }
        public Task<OcrPage>? Page { get; init; }
        public FrameWords? Words { get; set; }
        public bool Pad { get; init; }
        public bool Paused { get; set; }

        /// <summary>Перевод экрана: clicks toggle plates instead of looking words up; closing stops the translations.</summary>
        public bool Translating { get; init; }

        /// <summary>«Зона»: the still waits for a rectangle (or a click) to translate; nothing is recognized before.</summary>
        public bool Zone { get; init; }

        public CancellationTokenSource Stop { get; } = new();
        public PadButtons Held { get; set; }
    }

    public LookupSessions(Func<AppSettings> settings, GameRegistry games, ScreenCapture capture, OcrEngine ocr, WordLookup words,
        LookupController controller, LookupPopup popup, ResumeGuardClient guard, GamepadHub pad, ILog log, Func<IEnumerable<SavedWord>> saved)
    {
        _saved = saved;
        _live = new LiveTranslator(settings, capture, ocr, controller, Dispatcher.CurrentDispatcher, log);
        _live.Notice += text => Notice?.Invoke(text);
        _settings = settings;
        _games = games;
        _capture = capture;
        _ocr = ocr;
        _words = words;
        _controller = controller;
        _popup = popup;
        _guard = guard;
        _pad = pad;
        _log = log;
        popup.Dismissed += OnCardClosed;
        pad.Changed += OnPad;
        _repeatTimer.Tick += (_, _) => Step();
    }

    /// <summary>Something the user should know once (a game in exclusive full screen, a pause that was refused).</summary>
    public event Action<string>? Notice;

    public bool FrameOpen => _session?.Still is not null;

    /// <summary>
    /// The monitor at the point without Glossa's own card in it. With «Скрывать карточку от захвата» off the card is in
    /// every screenshot, and the next lookup read the old card along with the game (2026-09-29: "観察 4"); an open card
    /// steps aside first, the new lookup replaces it anyway, and the desktop is let to redraw.
    /// </summary>
    private async Task<CapturedFrame> CaptureAsync(int x, int y)
    {
        if (!_settings().Popup.HideFromCapture && _popup.IsVisible)
        {
            _popup.StepAside();
            await Task.Run(() =>
            {
                Native.DwmFlush();
                Native.DwmFlush();
            });
        }
        return await Task.Run(() => _capture.CaptureMonitorAt(x, y));
    }

    /// <summary>Alt+Q or the mouse button: the word under the cursor. Pressed again over a still frame, back to the game.</summary>
    public async void Pointer(string trigger)
    {
        try
        {
            if (FrameOpen)
            {
                End();
                return;
            }
            var s = _settings();
            if (s.Purpose == "translate")
            {
                await TranslateAsync(trigger, s.TranslateMode);
                return;
            }
            var sw = Stopwatch.StartNew();
            Native.GetCursorPos(out var cursor);
            var (game, context, cjk) = await BeginAsync(trigger);
            var frame = await CaptureAsync(cursor.X, cursor.Y);

            switch (context.Choices!.DuringLookup)
            {
                case "frame":
                {
                    var session = OpenFrame(game, context, cjk, frame, pad: false, paused: false);
                    var page = await session.Page!;
                    if (_session != session) return;
                    MarkKnown(session, FrameWords.Build(page, _words.Matcher(cjk)));
                    if (_words.Hit(page, cursor.X, cursor.Y, cjk) is null) return; // nothing under the cursor: click a word
                    await _controller.LookupAsync(frame, cursor.X, cursor.Y, context, sw, page);
                    break;
                }
                case "pause":
                    _session = new Session { Game = game, Context = context, Cjk = cjk, Paused = await PauseAsync(game) };
                    await _controller.LookupAsync(frame, cursor.X, cursor.Y, context, sw);
                    break;
                default:
                    _session = null;
                    await _controller.LookupAsync(frame, cursor.X, cursor.Y, context, sw);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error("lookup session", ex);
        }
    }

    /// <summary>The gamepad combination: a still frame of the game's monitor with the first word of the dialogue marked.</summary>
    public async void PadCombo(string trigger)
    {
        try
        {
            if (FrameOpen)
            {
                End();
                return;
            }
            // Только перевод: the gamepad has no cursor to point at a line, so it translates the whole screen.
            if (_settings().Purpose == "translate")
            {
                await TranslateAsync(trigger, "screen");
                return;
            }
            var (game, context, cjk) = await BeginAsync(trigger);
            var frame = await CaptureAsync((int)game.Bounds.CenterX, (int)game.Bounds.CenterY);
            // The still takes the focus first, then the game is paused: focus never moves away from a frozen window.
            var session = OpenFrame(game, context, cjk, frame, pad: true, paused: false);
            if (context.Choices!.DuringLookup == "pause")
            {
                var paused = await PauseAsync(game);
                if (_session == session) session.Paused = paused;
                else if (paused) _ = Task.Run(_guard.Resume); // closed meanwhile
            }
            var page = await session.Page!;
            if (_session != session) return;
            session.Words = FrameWords.Build(page, _words.Matcher(cjk));
            MarkKnown(session, session.Words);
            if (session.Words.Current is { } first)
            {
                _frame!.Mark(first.Box);
                _frame.SetHint(PadHint);
            }
            else
            {
                _frame!.SetHint("На снимке нет текста, B - вернуться в игру");
            }
        }
        catch (Exception ex)
        {
            _log.Error("gamepad session", ex);
        }
    }

    /// <summary>Esc while the card is open: over a still frame back to the game, otherwise just the card.</summary>
    public void Escape()
    {
        if (FrameOpen) End();
        else _popup.Dismiss();
    }

    /// <summary>Back to the game: the card and the still frame close, a paused game wakes and gets its focus back.</summary>
    public void End()
    {
        var s = _session;
        _session = null;
        s?.Stop.Cancel();
        _repeatTimer.Stop();
        _pad.Steering = false;
        _controller.Cancel();
        _popup.Dismiss();
        if (s?.Still is not null)
        {
            _frame?.Leave();
            s.Game.Focus();
        }
        if (s?.Paused == true) Task.Run(_guard.Resume);
    }

    /// <summary>The game in front, its profile touched (created at the first lookup), its window prepared.</summary>
    private async Task<(GameWindow Game, LookupContext Context, string Cjk)> BeginAsync(string trigger)
    {
        // A word being corrected in the card has the keyboard: it goes back first, or Glossa would pass for the game.
        _popup.CancelCorrection();
        var game = GameWindow.Foreground();
        // A lookup in another program wakes a game paused by the previous one; in the same game it stays paused.
        if (_session is { Paused: true, Still: null } paused && paused.Game.Pid != game.Pid) End();
        var (profile, choices) = _games.Touch(game);
        if (profile is not null)
        {
            if (profile is { Borderless: true, IsProtected: false } && game.Mode == WindowMode.Windowed && game.MakeBorderless())
            {
                await Task.Delay(150); // let the game draw itself stretched before the screenshot
                game = GameWindow.Of(game.Hwnd);
            }
            if (game.Mode == WindowMode.Exclusive && _games.WarnExclusiveOnce(profile))
                Notice?.Invoke($"\"{profile.Name}\" идёт в эксклюзивном полноэкранном режиме - карточку может быть не видно. " +
                               "Включите в настройках игры \"Окно без рамки\" (Borderless).");
            if (choices.PauseRefused is { } why && _told.Add(profile.Name + why))
                Notice?.Invoke($"\"{profile.Name}\": {why}. Вместо паузы - стоп-кадр.");
        }
        var s = _settings();
        var cjk = choices.Language is "ja" or "zh" ? choices.Language : s.PreferredCjk;
        return (game, new LookupContext(trigger, game.Title, game.ExeName, choices), cjk);
    }

    private Session OpenFrame(GameWindow game, LookupContext context, string cjk, CapturedFrame still, bool pad, bool paused,
        bool translating = false, bool zone = false)
    {
        if (_frame is null)
        {
            _frame = new FrozenFrame();
            _frame.Clicked += (x, y) =>
            {
                if (_session is { Zone: true } z) _ = TranslateChosenAsync(z, null, x, y);
                else _ = LookAtAsync(x, y);
            };
            _frame.ZoneSelected += box =>
            {
                if (_session is { Zone: true } z) _ = TranslateChosenAsync(z, box, 0, 0);
            };
            _frame.CloseRequested += End;
        }
        var family = context.Choices?.Language == "ru" ? OcrModelFamily.Cyrillic : OcrModelFamily.CjkLatin;
        var session = new Session
        {
            Game = game, Context = context, Cjk = cjk, Still = still, Pad = pad, Paused = paused, Translating = translating, Zone = zone,
            // The whole still is recognized once (about as long as the region around a cursor); every word on it is then at hand.
            // «Зона» recognizes only what is drawn around, at full size.
            Page = zone ? null
                : _ocr.RecognizeAsync(still.Bgra, still.Width, still.Height, still.Stride, still.Bounds, family, CancellationToken.None),
        };
        _session = session;
        _frame.SetHint(zone ? ZoneHint : pad ? "Распознаю текст..." : MouseHint);
        _frame.ShowFrame(still, _settings().Popup.HideFromCapture);
        _frame.SelectZone(zone);
        _pad.Steering = pad;
        return session;
    }

    /// <summary>The words of the still that are already in the dictionary get a frame (Во время поиска: «Отмечать слова из словаря»).</summary>
    private void MarkKnown(Session session, FrameWords words)
    {
        if (!_settings().MarkKnownWords || _frame is null || _session != session) return;
        try
        {
            var known = new KnownWords(_saved());
            var marks = words.All
                .Select(w => (w.Box, Pinned: known.Find(w.Text)))
                .Where(m => m.Pinned is not null)
                .Select(m => (m.Box, m.Pinned!.Value))
                .ToList();
            _frame.ShowKnown(marks);
        }
        catch (Exception ex)
        {
            _log.Error("known words", ex); // the still works without the marks
        }
    }

    /// <summary>A word clicked on the still, or chosen with A.</summary>
    private async Task LookAtAsync(int x, int y)
    {
        if (_session is not { Still: { } still, Page: { } pending, Translating: false } s) return;
        var sw = Stopwatch.StartNew();
        var page = await pending;
        if (_session != s) return;
        if (_words.Hit(page, x, y, s.Cjk) is null)
        {
            _popup.Dismiss();
            _frame?.SetHint("Здесь нет текста - " + (s.Pad ? PadHint : MouseHint));
            return;
        }
        _frame?.SetHint(s.Pad ? PadHint : MouseHint);
        await _controller.LookupAsync(still, x, y, s.Context with { Trigger = s.Pad ? "A" : "Щелчок" }, sw, page);
    }

    /// <summary>
    /// Только перевод by the chosen way: the paragraph under the cursor in a small card (line), every paragraph of a still
    /// of the screen (screen), or subtitles while playing, switched on and off by the same keys (live).
    /// </summary>
    private async Task TranslateAsync(string trigger, string mode)
    {
        if (mode == "live" && _live.Running)
        {
            _live.Stop();
            Notice?.Invoke("Живой перевод выключен.");
            return;
        }
        var sw = Stopwatch.StartNew();
        Native.GetCursorPos(out var cursor);
        var (game, context, cjk) = await BeginAsync(trigger);
        if (mode == "live" && context.Choices?.IsProtected == true)
        {
            Notice?.Invoke($"\"{game.Title}\": игра под античитом - живой перевод поверх неё не включается. Перевожу реплику под курсором.");
            mode = "line";
        }
        switch (mode)
        {
            case "screen":
                await TranslateScreenAsync(game, context, cjk, cursor);
                break;
            case "live":
                _live.Start(game, context, cjk);
                break;
            default:
            {
                var still = await CaptureAsync(cursor.X, cursor.Y);
                OpenFrame(game, context, cjk, still, pad: false, paused: false, zone: true);
                break;
            }
        }
    }

    /// <summary>
    /// «Зона»: a rectangle drawn on the still (or a click, the paragraph under it). The still goes, the game gets its
    /// focus back, and the card beside the text translates what was chosen.
    /// </summary>
    private async Task TranslateChosenAsync(Session s, PixelRect? zone, int x, int y)
    {
        if (_session != s || s.Still is not { } still) return;
        var sw = Stopwatch.StartNew();
        _session = null;
        _frame?.Leave();
        s.Game.Focus();
        if (zone is { } z) await _controller.TranslateZoneAsync(still, z, s.Context with { Trigger = s.Context.Trigger + ", зона" }, sw);
        else await _controller.TranslateLineAsync(still, x, y, s.Context with { Trigger = s.Context.Trigger + ", щелчок" }, sw);
    }

    /// <summary>
    /// Перевод экрана: a still of the monitor, recognized once; every paragraph worth it gets a plate with its translation,
    /// the one under the cursor first, then the dialogue, then the rest from the top. Esc or the same keys go back.
    /// </summary>
    private async Task TranslateScreenAsync(GameWindow game, LookupContext context, string cjk, Native.POINT cursor)
    {
        var still = await CaptureAsync(cursor.X, cursor.Y);
        var session = OpenFrame(game, context, cjk, still, pad: false, paused: false, translating: true);
        _frame!.SetHint("Распознаю текст...");
        try
        {
            var page = await session.Page!;
            if (_session != session) return;
            var s = _settings();
            var language = context.Choices?.Language ?? s.ScreenLanguage;
            var forced = language is "en" or "ja" or "zh" ? language : null;
            string Lang(TextBlock b) => forced ?? Languages.DetectText(b.Text, cjk);
            var blocks = TextBlocks.Of(page).Where(b => TextBlocks.Translatable(b, Languages.TargetFor(Lang(b), s.NativeLanguage))).ToList();
            var first = TextBlocks.At(page, cursor.X, cursor.Y);
            var dialogue = TextBlocks.Dialogue(blocks);
            var order = blocks.OrderBy(b => b == first ? 0 : b == dialogue ? 1 : 2).ThenBy(b => b.Box.Top).ToList();
            if (order.Count == 0)
            {
                _frame.SetHint("На снимке нет текста для перевода, Esc - вернуться в игру");
                return;
            }
            var done = 0;
            foreach (var block in order)
            {
                var show = _frame.AddTranslation(block.Box, block.Lines);
                _frame.SetHint($"Перевожу {done + 1} из {order.Count}...");
                var lang = Lang(block);
                var (text, _) = await _controller.TranslateTextAsync(block.Text, lang, Languages.TargetFor(lang, s.NativeLanguage),
                    context.Choices?.Ai, show, session.Stop.Token);
                show(text);
                done++;
            }
            _frame.SetHint($"Переведено: {done}, щелчок по переводу - оригинал, Esc - вернуться в игру");
        }
        catch (OperationCanceledException)
        {
        }
        catch (LlmException ex)
        {
            if (_session == session) _frame?.SetHint(ex.Message.TrimEnd('.') + ". Esc - вернуться в игру");
        }
    }

    private async Task<bool> PauseAsync(GameWindow game)
    {
        var error = await Task.Run(() => _guard.Pause(game.Pid));
        if (error is null)
        {
            _log.Info($"paused {game.ExeName} ({game.Pid})");
            return true;
        }
        _log.Warn($"pause refused for {game.ExeName}: {error}");
        if (_told.Add(game.ExePath + error)) Notice?.Invoke($"Игру не удалось поставить на паузу: {error}.");
        return false;
    }

    /// <summary>The card closed by itself (Esc, a click beside it): a pause without a still frame ends with it.</summary>
    private void OnCardClosed()
    {
        if (_session is { Paused: true, Still: null } s)
        {
            _session = null;
            Task.Run(_guard.Resume);
            _log.Info($"resumed {s.Game.ExeName}");
        }
    }

    private void OnPad(PadButtons state)
    {
        if (_session is not { Pad: true } s || _frame is null) return;
        var pressed = state & ~s.Held;
        s.Held = state;
        if (pressed.HasFlag(PadButtons.B))
        {
            if (_popup.IsVisible) _popup.Dismiss();
            else End();
            return;
        }
        if (pressed.HasFlag(PadButtons.A) && s.Words?.Current is { } w)
        {
            // The first character of the word: for Japanese and Chinese the lookup draws the same word from there.
            _ = LookAtAsync((int)(w.Box.Left + Math.Min(w.Box.Width, w.Box.Height) / 2), (int)w.Box.CenterY);
            return;
        }
        if (pressed.HasFlag(PadButtons.X)) _controller.SaveCurrent();
        if (pressed.HasFlag(PadButtons.Y)) _controller.Speak();
        Step();
        if ((state & (Pad.Up | Pad.Down | Pad.Left | Pad.Right)) != 0) _repeatTimer.Start();
        else _repeatTimer.Stop();
    }

    /// <summary>One step of the highlight for a held direction (at once, then repeating while held).</summary>
    private void Step()
    {
        if (_session is not { Pad: true, Words: { } words } s || _frame is null) return;
        var step = _repeat.Step(s.Held, _clock.ElapsedMilliseconds);
        if (step != PadButtons.None && words.Move(step)) _frame.Mark(words.Current!.Box);
    }
}
