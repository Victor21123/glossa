using Glossa.Core.Config;
using Glossa.Core.Logging;

namespace Glossa.Core.Updates;

/// <summary>
/// The update check as the app runs it: a loop that waits a minute after the start, then wakes every hour and asks
/// GitHub when 24 hours have passed since the last answer, the switch is on and no back-off after a failure is running
/// (6 h offline, 24 h after an unusable answer, or what GitHub asked; kept in memory); plus the manual «Проверить сейчас»
/// and <see cref="CheckSoon"/> for the switch being turned on. A failed check is only a log line. A newer release is
/// announced once per version (<see cref="Announce"/>); what was shown in the settings counts as told. One check runs at
/// a time and a request that comes while one is running joins it. Every touch of the settings goes through
/// <paramref name="post"/> (the app passes its UI thread), so the network thread never reads or writes them, and the
/// events are raised there too. Time (the 24 hours, the waits) comes from <paramref name="time"/>. Nothing is downloaded
/// or installed.
/// </summary>
/// <param name="settings">The live settings.</param>
/// <param name="save">Writes them to disk (called after an answer that changed them).</param>
/// <param name="checker">The network part.</param>
/// <param name="current">The running version.</param>
/// <param name="log">Where a failed check is noted.</param>
/// <param name="time">The clock and timers; tests pass a fake.</param>
/// <param name="post">Runs an action on the thread that owns the settings and completes when it is done; default: right here.</param>
public sealed class UpdateService(Func<AppSettings> settings, Action save, UpdateChecker checker, AppVersion current, ILog log,
    TimeProvider? time = null, Func<Action, Task>? post = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private Task<UpdateOutcome?>? _running;
    private readonly object _waitLock = new();
    private DateTime? _notBefore;
    private volatile bool _started;
    private CancellationToken _loop;

    /// <summary>The running version.</summary>
    public AppVersion Current => current;

    /// <summary>The line for the last answer of this run; null before any (then <see cref="UpdateTexts.Summary"/>).</summary>
    public string? Status { get; private set; }

    /// <summary>The newer version that is known and waiting, if any. Read on the settings' thread.</summary>
    public PendingUpdate? Pending => UpdateCheck.Pending(settings().Updates, current);

    /// <summary>A newer release has been found that the user has not been told about: show the notice. Raised on the settings' thread.</summary>
    public event Action<PendingUpdate>? Announce;

    /// <summary>A check has ended (any result): the settings page and the tray menu refresh. Raised on the settings' thread.</summary>
    public event Action? Changed;

    private DateTime? NotBefore
    {
        get { lock (_waitLock) return _notBefore; }
        set { lock (_waitLock) _notBefore = value; }
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>The app is closing: cancels a check in flight and the ones to come.</summary>
    public void Stop() => _stop.Cancel();

    /// <summary>
    /// The loop: waits <paramref name="firstDelay"/>, then every <paramref name="poll"/> checks if the check is due.
    /// Ends quietly when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task RunAsync(TimeSpan firstDelay, TimeSpan poll, CancellationToken ct)
    {
        _loop = ct;
        try
        {
            await Task.Delay(firstDelay, _time, ct).ConfigureAwait(false);
            _started = true;
            while (true)
            {
                try
                {
                    await CheckIfDueAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    // Anything but the loop being stopped (a closing dispatcher cancels its work too): note it, try again next time.
                    log.Error("update check", ex);
                }
                await Task.Delay(poll, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// The switch was turned on: a check that is due runs now instead of at the next hourly wake-up. Does nothing before the
    /// loop's first wait is over (the start stays quiet) or when no check is due. The task ends with the check (the app ignores it).
    /// </summary>
    public Task CheckSoon()
    {
        if (!_started) return Task.CompletedTask;
        var ct = _loop;
        return Task.Run(async () =>
        {
            try
            {
                await CheckIfDueAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                log.Error("update check", ex);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>The automatic check: null (and no request) when the switch is off, a back-off runs or the last answer is less than a day old.</summary>
    public async Task<UpdateOutcome?> CheckIfDueAsync(CancellationToken ct) => await Start(manual: false, ct).WaitAsync(ct).ConfigureAwait(false);

    /// <summary>«Проверить сейчас»: asks whatever the switch, the back-off and the last answer say; joins a check that is already running.</summary>
    public async Task<UpdateOutcome> CheckNowAsync(CancellationToken ct)
    {
        while (true)
        {
            // A due-check that found nothing to do may be what was joined: then ask again, now for real.
            if (await Start(manual: true, ct).WaitAsync(ct).ConfigureAwait(false) is { } outcome) return outcome;
        }
    }

    /// <summary>One check at a time: starts one, or hands back the one that is running.</summary>
    private Task<UpdateOutcome?> Start(bool manual, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false } running) return running;
            // A click's token must not kill a check others may have joined; the loop's token ends its own checks.
            var linked = manual ? CancellationTokenSource.CreateLinkedTokenSource(_stop.Token) : CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, ct);
            var task = CheckAsync(manual, linked.Token);
            _running = task;
            _ = task.ContinueWith(_ => linked.Dispose(), TaskScheduler.Default);
            return task;
        }
    }

    private async Task<UpdateOutcome?> CheckAsync(bool manual, CancellationToken ct)
    {
        if (!manual && !await OnSettingsThread(() => UpdateCheck.IsDue(settings().Updates, Now, NotBefore)).ConfigureAwait(false)) return null;
        UpdateOutcome outcome;
        try
        {
            outcome = await checker.CheckAsync(current, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error("update check", ex);
            outcome = UpdateOutcome.Failed(UpdateFailure.BadAnswer);
        }
        if (outcome.Status == UpdateStatus.Failed)
        {
            var wait = Now + UpdateCheck.RetryDelay(outcome);
            NotBefore = wait;
            log.Warn($"update check failed: {outcome.Failure} ({outcome.Detail}), next try after {wait:u}");
        }
        else
        {
            NotBefore = null;
            log.Info($"update check: latest {outcome.Release!.Tag}, {outcome.Status}");
        }

        await PostAsync(() => Apply(outcome, manual)).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Runs on the settings' thread: remember the answer, decide on the notice, then tell the listeners.</summary>
    private void Apply(UpdateOutcome outcome, bool manual)
    {
        var updates = settings().Updates;
        UpdateCheck.Record(updates, outcome, Now);
        Status = UpdateTexts.For(outcome);
        PendingUpdate? announce = null;
        if (outcome is { Status: UpdateStatus.Available, Release: { } release })
        {
            var fresh = UpdateCheck.ShouldNotify(release, current, updates);
            // An automatic check whose switch was turned off while it was running says nothing.
            var allowed = manual || updates.CheckForUpdates;
            // A manual check showed the answer in the settings: that is telling, so the balloon is not repeated tomorrow.
            if (fresh && allowed) updates.NotifiedVersion = release.Version!.Value.ToString();
            if (fresh && allowed && !manual) announce = Pending;
        }
        if (outcome.Status != UpdateStatus.Failed) Raise(save, "save");
        if (announce is not null) Raise(() => Announce?.Invoke(announce), "announce");
        Raise(() => Changed?.Invoke(), "changed");
    }

    /// <summary>A failing listener (or a full disk) must not break the check or hide the result from the others.</summary>
    private void Raise(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log.Error("update check: " + what, ex);
        }
    }

    private Task PostAsync(Action work) => (post ?? Inline)(work);

    private async Task<T> OnSettingsThread<T>(Func<T> work)
    {
        T result = default!;
        await PostAsync(() => result = work()).ConfigureAwait(false);
        return result;
    }

    private static Task Inline(Action work)
    {
        work();
        return Task.CompletedTask;
    }
}
