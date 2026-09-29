using System.Runtime.InteropServices;
using System.Windows.Threading;
using Glossa.App.Capture;
using Glossa.App.Games;
using Glossa.App.Interop;
using Glossa.App.Views;
using Glossa.Core.Config;
using Glossa.Core.Llm;
using Glossa.Core.Logging;
using Glossa.Core.Lookup;
using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.App.Lookup;

/// <summary>
/// Только перевод, «Живой перевод»: while it is on, the game's picture is looked at about twice a second; when it
/// settles on a new dialogue line, that line is recognized and its translation shown as a subtitle above it. It works
/// only while the game is in front, stops with the game, and is never started for a game behind an anti-cheat.
/// </summary>
public sealed class LiveTranslator(
    Func<AppSettings> settings, ScreenCapture capture, OcrEngine ocr, LookupController controller, Dispatcher ui, ILog log)
{
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(450);
    private CancellationTokenSource? _cts;
    private SubtitleOverlay? _overlay;

    public bool Running => _cts is not null;

    /// <summary>Something the user should know (it stopped, the AI is missing).</summary>
    public event Action<string>? Notice;

    public void Start(GameWindow game, LookupContext context, string cjk)
    {
        Stop();
        _overlay ??= new SubtitleOverlay();
        var cts = _cts = new CancellationTokenSource();
        var hint = new PixelRect(game.Bounds.Left + game.Bounds.Width * 0.1, game.Bounds.Bottom - 40, game.Bounds.Right - game.Bounds.Width * 0.1, game.Bounds.Bottom);
        _overlay.ShowFor(hint, game.Bounds, "ЖИВОЙ ПЕРЕВОД", $"Включён. Новые реплики появятся здесь; {settings().Hotkey} — выключить.");
        log.Info($"live translation on: {game.ExeName}");
        _ = Task.Run(() => LoopAsync(game, context, cjk, cts));
    }

    public void Stop()
    {
        if (_cts is not { } cts) return;
        _cts = null;
        cts.Cancel();
        _overlay?.Conceal();
        log.Info("live translation off");
    }

    /// <summary>Stops only the run it belongs to: a newer one started meanwhile keeps going.</summary>
    private void StopRun(CancellationTokenSource cts, string? why = null)
    {
        if (_cts != cts) return;
        Stop();
        if (why is not null) Notice?.Invoke(why);
    }

    private async Task LoopAsync(GameWindow game, LookupContext context, string cjk, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var watcher = new LiveTextWatcher();
        ScreenCapture.WatchSession? watch = null;
        var away = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Period, ct);
                var s = settings();
                if (s.Purpose != "translate" || s.TranslateMode != "live")
                {
                    ui.Invoke(() => StopRun(cts));
                    return;
                }
                if (!Native.IsWindow(game.Hwnd))
                {
                    ui.Invoke(() => StopRun(cts, "Игра закрыта — живой перевод выключен."));
                    return;
                }
                // Only the game in front is read; its subtitle waits while another window is.
                if (Native.GetForegroundWindow() != game.Hwnd)
                {
                    if (!away) ui.Invoke(() => _overlay?.Conceal());
                    away = true;
                    continue;
                }
                if (away) ui.Invoke(() => _overlay?.Reveal());
                away = false;

                var bounds = GameWindow.Of(game.Hwnd).Bounds;
                watch ??= capture.Watch((int)bounds.CenterX, (int)bounds.CenterY);
                if (watch is null)
                {
                    ui.Invoke(() => StopRun(cts, "Экран этой игры не удаётся снимать непрерывно — живой перевод выключен."));
                    return;
                }
                CapturedFrame? frame;
                try
                {
                    frame = watch.Grab();
                }
                catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException)
                {
                    watch.Dispose(); // a mode switch: the next turn opens a new one
                    watch = null;
                    continue;
                }
                if (frame is null) continue;

                var crop = frame.Crop(bounds);
                if (crop.Width < 64 || crop.Height < 36) continue;
                if (!watcher.ShouldRecognize(ScreenFingerprint.Of(crop.Bgra, crop.Width, crop.Height, crop.Stride), DateTime.UtcNow)) continue;

                var language = context.Choices?.Language ?? s.ScreenLanguage;
                var family = language == "ru" ? OcrModelFamily.Cyrillic : OcrModelFamily.CjkLatin;
                var page = await ocr.RecognizeAsync(crop.Bgra, crop.Width, crop.Height, crop.Stride, crop.Region, family, ct);
                var forced = language is "en" or "ja" or "zh" ? language : null;
                string Lang(TextBlock b) => forced ?? Languages.DetectText(b.Text, cjk);
                if (watcher.NewLine(TextBlocks.Of(page), b => Languages.TargetFor(Lang(b), s.NativeLanguage)) is not { } line) continue;

                var lang = Lang(line);
                ui.Invoke(() => _overlay?.ShowFor(line.Box, bounds, "ПЕРЕВОД", "…"));
                var (text, _) = await controller.TranslateTextAsync(line.Text, lang, Languages.TargetFor(lang, s.NativeLanguage),
                    context.Choices?.Ai, t => ui.BeginInvoke(() => _overlay?.SetText(t)), ct);
                _ = ui.BeginInvoke(() => _overlay?.SetText(text));
                log.Info($"live: [{lang}] {line.Text.Length} chars translated");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (LlmException ex)
        {
            ui.Invoke(() => StopRun(cts, "Живой перевод выключен: " + ex.Message));
        }
        catch (Exception ex)
        {
            log.Error("live translation", ex);
            ui.Invoke(() => StopRun(cts, "Живой перевод остановился из-за ошибки — подробности в журнале."));
        }
        finally
        {
            watch?.Dispose();
        }
    }
}
