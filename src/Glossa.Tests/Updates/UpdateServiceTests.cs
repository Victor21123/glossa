using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Glossa.Core.Config;
using Glossa.Core.Logging;
using Glossa.Core.Updates;

namespace Glossa.Tests.Updates;

public class UpdateServiceTests
{
    private static readonly AppVersion V003 = AppVersion.Parse("0.0.3")!.Value;
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static string Json(string tag) =>
        $$"""{"tag_name": "{{tag}}", "name": "Glossa {{tag}}", "html_url": "https://github.com/Victor21123/glossa/releases/tag/{{tag}}"}""";

    /// <summary>GitHub, as far as the service can tell: counts requests, can be held, refuse or fail like a dead line.</summary>
    private sealed class Github : HttpMessageHandler
    {
        public string Latest = "v0.1.0";
        public bool Down;
        public HttpStatusCode? Status;
        public TimeSpan? RetryAfter;
        public volatile TaskCompletionSource? Hold;
        public int Requests;
        public int MaxInFlight;
        private int _inFlight;
        private TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes at the next request (take it before the action that causes the request).</summary>
        public Task NextRequest()
        {
            lock (this) return _asked.Task;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var now = Interlocked.Increment(ref _inFlight);
            TaskCompletionSource signal;
            lock (this)
            {
                MaxInFlight = Math.Max(MaxInFlight, now);
                signal = _asked;
                _asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            signal.TrySetResult();
            try
            {
                if (Hold is { } hold) await hold.Task.WaitAsync(ct);
                if (Down) throw new HttpRequestException("unreachable");
                if (Status is { } status)
                {
                    var refusal = new HttpResponseMessage(status);
                    if (RetryAfter is { } wait) refusal.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
                    return refusal;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json(Latest), Encoding.UTF8, "application/json") };
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private sealed class Log : ILog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) { lock (Lines) Lines.Add("INFO " + message); }
        public void Warn(string message) { lock (Lines) Lines.Add("WARN " + message); }
        public void Error(string message, Exception? ex = null) { lock (Lines) Lines.Add("ERROR " + message); }
    }

    private sealed class Rig
    {
        public Github Web { get; } = new();
        public FakeTime Time { get; } = new(T0);
        public AppSettings Settings { get; } = new();
        public Log Log { get; } = new();
        public int Saves;
        public List<PendingUpdate> Announced { get; } = [];
        public int Changes;
        public UpdateService Service { get; }
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Rig(AppVersion? current = null)
        {
            var client = new HttpClient(Web);
            Service = new UpdateService(() => Settings, () => Saves++, new UpdateChecker(client, client), current ?? V003, Log, Time);
            Service.Announce += Announced.Add;
            Service.Changed += () =>
            {
                Changes++;
                _changed.TrySetResult();
            };
        }

        /// <summary>Completes at the next end of a check.</summary>
        public Task NextChanged()
        {
            lock (this) return _changed.Task;
        }

        public void ResetChanged()
        {
            lock (this) _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>One wake-up of the loop: time moves, the loop checks (or finds nothing to do) and goes back to waiting.</summary>
        public async Task Tick(TimeSpan by)
        {
            var waiting = Time.NextTimer();
            Time.Advance(by);
            await waiting;
        }
    }

    // ---- when it asks ----

    [Fact]
    public async Task Does_not_even_ask_while_the_switch_is_off()
    {
        var rig = new Rig();
        rig.Settings.Updates.CheckForUpdates = false;
        Assert.Null(await rig.Service.CheckIfDueAsync(CancellationToken.None));
        Assert.Equal(0, rig.Web.Requests);
        Assert.Equal(0, rig.Saves);
        Assert.Empty(rig.Announced);
    }

    [Fact]
    public async Task Does_not_ask_again_within_24_hours_even_after_a_restart()
    {
        var rig = new Rig();
        rig.Settings.Updates.LastCheckUtc = T0.UtcDateTime.AddHours(-3);
        Assert.Null(await rig.Service.CheckIfDueAsync(CancellationToken.None));
        Assert.Equal(0, rig.Web.Requests);
    }

    [Fact]
    public async Task A_newer_release_is_told_once_and_remembered()
    {
        var rig = new Rig();
        var outcome = await rig.Service.CheckIfDueAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.Available, outcome!.Status);
        var told = Assert.Single(rig.Announced);
        Assert.Equal("0.1.0", told.Version);
        Assert.Equal("https://github.com/Victor21123/glossa/releases/tag/v0.1.0", told.Url);
        Assert.Equal(T0.UtcDateTime, rig.Settings.Updates.LastCheckUtc);
        Assert.Equal("0.1.0", rig.Settings.Updates.NotifiedVersion);
        Assert.Equal("0.1.0", rig.Settings.Updates.LatestVersion);
        Assert.True(rig.Saves >= 1);
        Assert.Equal("Доступна 0.1.0", rig.Service.Status);
        Assert.Equal("0.1.0", rig.Service.Pending!.Version);

        // The next day the same release is still the latest: no second balloon, but it stays pending for the settings and the tray.
        rig.Time.SetUtcNow(T0.AddHours(25));
        await rig.Service.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(2, rig.Web.Requests);
        Assert.Single(rig.Announced);
        Assert.Equal("0.1.0", rig.Service.Pending!.Version);
    }

    [Fact]
    public async Task A_still_newer_release_is_told_again()
    {
        var rig = new Rig();
        await rig.Service.CheckIfDueAsync(CancellationToken.None);
        rig.Web.Latest = "v0.1.1";
        rig.Time.SetUtcNow(T0.AddDays(2));
        await rig.Service.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(["0.1.0", "0.1.1"], rig.Announced.Select(a => a.Version));
    }

    [Fact]
    public async Task Nothing_is_told_when_the_user_has_the_latest()
    {
        var rig = new Rig();
        rig.Web.Latest = "v0.0.3";
        var outcome = await rig.Service.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(UpdateStatus.UpToDate, outcome!.Status);
        Assert.Empty(rig.Announced);
        Assert.Equal("", rig.Settings.Updates.NotifiedVersion);
        Assert.Equal(T0.UtcDateTime, rig.Settings.Updates.LastCheckUtc);
        Assert.Null(rig.Service.Pending);
        Assert.Equal("Установлена последняя версия", rig.Service.Status);
    }

    // ---- failures and the back-off ----

    [Fact]
    public async Task A_failed_check_is_quiet_and_not_remembered_as_an_answer()
    {
        var rig = new Rig();
        rig.Web.Down = true;
        var outcome = await rig.Service.CheckIfDueAsync(CancellationToken.None);

        Assert.Equal(UpdateFailure.Offline, outcome!.Failure);
        Assert.Empty(rig.Announced);
        Assert.Null(rig.Settings.Updates.LastCheckUtc);
        Assert.Equal(0, rig.Saves);
        Assert.Equal("Не удалось проверить: нет сети", rig.Service.Status);
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("WARN ") && l.Contains("update check failed: Offline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Offline_is_not_retried_for_6_hours()
    {
        var rig = new Rig();
        rig.Web.Down = true;
        await rig.Service.CheckIfDueAsync(CancellationToken.None);
        rig.Web.Down = false;

        rig.Time.SetUtcNow(T0.AddHours(5).AddMinutes(59));
        Assert.Null(await rig.Service.CheckIfDueAsync(CancellationToken.None));
        Assert.Equal(2, rig.Web.Requests); // both ways were tried once

        rig.Time.SetUtcNow(T0.AddHours(6));
        Assert.Equal(UpdateStatus.Available, (await rig.Service.CheckIfDueAsync(CancellationToken.None))!.Status);
        Assert.Equal(3, rig.Web.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_unusable_answer_from_github_counts_as_an_attempt_and_waits_a_full_day(HttpStatusCode status)
    {
        var rig = new Rig();
        rig.Web.Status = status;
        Assert.Equal(UpdateStatus.Failed, (await rig.Service.CheckIfDueAsync(CancellationToken.None))!.Status);
        rig.Web.Status = null;

        rig.Time.SetUtcNow(T0.AddHours(23).AddMinutes(59));
        Assert.Null(await rig.Service.CheckIfDueAsync(CancellationToken.None));
        Assert.Equal(1, rig.Web.Requests);
        rig.Time.SetUtcNow(T0.AddHours(24));
        Assert.Equal(UpdateStatus.Available, (await rig.Service.CheckIfDueAsync(CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task A_bad_json_answer_waits_a_full_day_too()
    {
        var rig = new Rig();
        rig.Web.Latest = "nightly"; // the fake wraps it as a tag that is not a version: not a release
        Assert.Equal(UpdateFailure.BadAnswer, (await rig.Service.CheckIfDueAsync(CancellationToken.None))!.Failure);
        rig.Web.Latest = "v0.1.0";
        rig.Time.SetUtcNow(T0.AddHours(12));
        Assert.Null(await rig.Service.CheckIfDueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_retry_after_from_github_is_honoured()
    {
        var rig = new Rig();
        rig.Web.Status = HttpStatusCode.TooManyRequests;
        rig.Web.RetryAfter = TimeSpan.FromMinutes(30);
        await rig.Service.CheckIfDueAsync(CancellationToken.None);
        rig.Web.Status = null;

        rig.Time.SetUtcNow(T0.AddMinutes(29));
        Assert.Null(await rig.Service.CheckIfDueAsync(CancellationToken.None));
        rig.Time.SetUtcNow(T0.AddMinutes(30));
        Assert.NotNull(await rig.Service.CheckIfDueAsync(CancellationToken.None));
    }

    // ---- manual ----

    [Fact]
    public async Task The_manual_check_ignores_the_switch_the_interval_and_the_back_off()
    {
        var rig = new Rig();
        rig.Web.Down = true;
        await rig.Service.CheckIfDueAsync(CancellationToken.None); // back-off for 6 hours
        rig.Web.Down = false;
        rig.Settings.Updates.CheckForUpdates = false;
        rig.Settings.Updates.LastCheckUtc = T0.UtcDateTime.AddMinutes(-1);

        var outcome = await rig.Service.CheckNowAsync(CancellationToken.None);
        Assert.Equal(UpdateStatus.Available, outcome.Status);
        Assert.Equal(3, rig.Web.Requests); // 2 while offline, 1 now
        Assert.Equal(T0.UtcDateTime, rig.Settings.Updates.LastCheckUtc);
        Assert.Equal("0.1.0", rig.Service.Pending!.Version);
    }

    [Fact]
    public async Task What_the_user_saw_in_the_settings_is_not_told_again_in_a_balloon()
    {
        var rig = new Rig();
        await rig.Service.CheckNowAsync(CancellationToken.None);
        Assert.Empty(rig.Announced);
        Assert.Equal("0.1.0", rig.Settings.Updates.NotifiedVersion);

        rig.Time.SetUtcNow(T0.AddDays(2));
        await rig.Service.CheckIfDueAsync(CancellationToken.None);
        Assert.Empty(rig.Announced);
    }

    [Fact]
    public async Task The_manual_check_reports_a_failure_to_the_caller()
    {
        var rig = new Rig();
        rig.Web.Down = true;
        var outcome = await rig.Service.CheckNowAsync(CancellationToken.None);
        Assert.Equal(UpdateStatus.Failed, outcome.Status);
        Assert.Equal("Не удалось проверить: нет сети", UpdateTexts.For(outcome));
    }

    [Fact]
    public async Task A_manual_check_while_one_runs_joins_it_instead_of_asking_twice()
    {
        var rig = new Rig();
        rig.Web.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = rig.Web.NextRequest();
        var first = rig.Service.CheckNowAsync(CancellationToken.None);
        await asked;

        var second = rig.Service.CheckNowAsync(CancellationToken.None);
        var due = rig.Service.CheckIfDueAsync(CancellationToken.None);
        rig.Web.Hold.SetResult();
        var outcomes = new[] { await first, await second, await due };

        Assert.Equal(1, rig.Web.Requests);
        Assert.Equal(1, rig.Web.MaxInFlight);
        Assert.All(outcomes, o => Assert.Equal(UpdateStatus.Available, o!.Status));
        Assert.Same(outcomes[0], outcomes[1]);

        // Once it is over, the next click is a new request.
        await rig.Service.CheckNowAsync(CancellationToken.None);
        Assert.Equal(2, rig.Web.Requests);
    }

    [Fact]
    public async Task The_caller_leaving_stops_waiting_but_not_the_check()
    {
        var rig = new Rig();
        rig.Web.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var asked = rig.Web.NextRequest();
        var waiting = rig.Service.CheckNowAsync(cts.Token);
        await asked;
        var done = rig.NextChanged();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        rig.Web.Hold.SetResult();
        await done; // the check went on and recorded its answer
        Assert.Equal("0.1.0", rig.Settings.Updates.LatestVersion);
    }

    [Fact]
    public async Task Stopping_the_service_cancels_a_check_in_flight_and_the_ones_to_come()
    {
        var rig = new Rig();
        rig.Web.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = rig.Web.NextRequest();
        var waiting = rig.Service.CheckNowAsync(CancellationToken.None);
        await asked;
        rig.Service.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Null(rig.Settings.Updates.LastCheckUtc);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.CheckNowAsync(CancellationToken.None));
    }

    // ---- the switch ----

    [Fact]
    public async Task A_switch_turned_off_during_a_check_means_no_announcement()
    {
        var rig = new Rig();
        rig.Web.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = rig.Web.NextRequest();
        var check = rig.Service.CheckIfDueAsync(CancellationToken.None);
        await asked;

        rig.Settings.Updates.CheckForUpdates = false;
        rig.Web.Hold.SetResult();
        var outcome = await check;

        Assert.Equal(UpdateStatus.Available, outcome!.Status);
        Assert.Empty(rig.Announced);
        Assert.Equal("", rig.Settings.Updates.NotifiedVersion); // not told: it will be when the switch is on and the news is still news
        Assert.Equal("0.1.0", rig.Service.Pending!.Version); // but it is known
    }

    [Fact]
    public async Task Turning_the_switch_on_runs_a_due_check_at_once_after_the_start_wait()
    {
        var rig = new Rig();
        rig.Settings.Updates.CheckForUpdates = false;
        using var cts = new CancellationTokenSource();
        var loop = rig.Service.RunAsync(TimeSpan.FromSeconds(60), TimeSpan.FromHours(1), cts.Token);
        await rig.Tick(TimeSpan.FromSeconds(60));
        Assert.Equal(0, rig.Web.Requests);

        rig.Settings.Updates.CheckForUpdates = true;
        await rig.Service.CheckSoon();
        Assert.Equal(1, rig.Web.Requests);
        Assert.Single(rig.Announced);

        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task Turning_the_switch_on_during_the_start_wait_does_not_hurry_the_first_check()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var loop = rig.Service.RunAsync(TimeSpan.FromSeconds(60), TimeSpan.FromHours(1), cts.Token);
        await rig.Service.CheckSoon();
        Assert.Equal(0, rig.Web.Requests);
        Assert.Equal(1, rig.Time.Waiting); // still waiting for the first minute
        cts.Cancel();
        await loop;
        Assert.Equal(0, rig.Web.Requests);
    }

    [Fact]
    public async Task Turning_the_switch_on_when_nothing_is_due_asks_nothing()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var loop = rig.Service.RunAsync(TimeSpan.FromSeconds(60), TimeSpan.FromHours(1), cts.Token);
        await rig.Tick(TimeSpan.FromSeconds(60)); // asks once
        Assert.Equal(1, rig.Web.Requests);

        await rig.Service.CheckSoon(); // a day has not passed
        Assert.Equal(1, rig.Web.Requests);
        cts.Cancel();
        await loop;
    }

    // ---- listeners and threads ----

    [Fact]
    public async Task A_user_who_updated_by_hand_stops_seeing_the_pending_update()
    {
        var rig = new Rig();
        await rig.Service.CheckIfDueAsync(CancellationToken.None);
        Assert.NotNull(rig.Service.Pending);

        var updated = new Rig(AppVersion.Parse("0.1.0"));
        updated.Settings.Updates.LatestVersion = "0.1.0";
        Assert.Null(updated.Service.Pending);
    }

    [Fact]
    public async Task Changed_is_raised_after_every_check_so_the_settings_and_the_tray_follow()
    {
        var rig = new Rig();
        await rig.Service.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(1, rig.Changes);
        rig.Web.Down = true;
        await rig.Service.CheckNowAsync(CancellationToken.None);
        Assert.Equal(2, rig.Changes);
    }

    [Fact]
    public async Task A_listener_that_throws_does_not_break_the_check_or_the_memory_of_it()
    {
        var rig = new Rig();
        rig.Service.Announce += _ => throw new InvalidOperationException("boom");
        rig.Service.Changed += () => throw new InvalidOperationException("boom");

        var outcome = await rig.Service.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(UpdateStatus.Available, outcome!.Status);
        Assert.Equal("0.1.0", rig.Settings.Updates.NotifiedVersion);
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("ERROR "));
    }

    [Fact]
    public async Task Every_touch_of_the_settings_happens_on_the_thread_that_post_names()
    {
        // One dedicated thread stands for the UI thread.
        var queue = new BlockingCollection<Action>();
        var thread = new Thread(() =>
        {
            foreach (var work in queue.GetConsumingEnumerable()) work();
        }) { IsBackground = true };
        thread.Start();
        var owner = thread.ManagedThreadId;

        var touched = new ConcurrentBag<int>();
        var settings = new AppSettings();
        var saves = new ConcurrentBag<int>();
        var web = new Github();
        var client = new HttpClient(web);
        var service = new UpdateService(
            () =>
            {
                touched.Add(Environment.CurrentManagedThreadId);
                return settings;
            },
            () => saves.Add(Environment.CurrentManagedThreadId),
            new UpdateChecker(client, client), V003, new Log(), new FakeTime(T0),
            work =>
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                queue.Add(() =>
                {
                    try
                    {
                        work();
                        done.SetResult();
                    }
                    catch (Exception e)
                    {
                        done.SetException(e);
                    }
                });
                return done.Task;
            });
        service.Announce += _ => touched.Add(Environment.CurrentManagedThreadId);
        service.Changed += () => touched.Add(Environment.CurrentManagedThreadId);

        await service.CheckIfDueAsync(CancellationToken.None);
        await service.CheckNowAsync(CancellationToken.None);
        queue.CompleteAdding();

        Assert.Equal(2, web.Requests);
        Assert.True(touched.Count >= 4, "the due test, the results and the events all touch the settings");
        Assert.All(touched, id => Assert.Equal(owner, id));
        Assert.All(saves, id => Assert.Equal(owner, id));
        Assert.NotEqual(Environment.CurrentManagedThreadId, owner);
        Assert.Equal(T0.UtcDateTime, settings.Updates.LastCheckUtc); // applied by the time the call returned
    }

    // ---- the loop ----

    [Fact]
    public async Task The_loop_waits_then_checks_when_due_and_stops_when_cancelled()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var loop = rig.Service.RunAsync(TimeSpan.FromSeconds(60), TimeSpan.FromHours(1), cts.Token);

        Assert.Equal(0, rig.Web.Requests); // not at once: the first check waits
        rig.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, rig.Web.Requests);
        await rig.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(1, rig.Web.Requests);
        Assert.Single(rig.Announced);

        for (var hour = 1; hour <= 23; hour++) await rig.Tick(TimeSpan.FromHours(1)); // all find the 24 hours not over
        Assert.Equal(1, rig.Web.Requests);
        await rig.Tick(TimeSpan.FromHours(1)); // the day is over
        Assert.Equal(2, rig.Web.Requests);
        Assert.Single(rig.Announced); // the same release is not told twice

        cts.Cancel();
        await loop; // ends quietly
    }

    [Fact]
    public async Task The_loop_picks_up_the_switch_being_turned_on_at_the_next_wake_up()
    {
        var rig = new Rig();
        rig.Settings.Updates.CheckForUpdates = false;
        using var cts = new CancellationTokenSource();
        var loop = rig.Service.RunAsync(TimeSpan.FromSeconds(10), TimeSpan.FromHours(1), cts.Token);

        await rig.Tick(TimeSpan.FromSeconds(10));
        await rig.Tick(TimeSpan.FromHours(1));
        Assert.Equal(0, rig.Web.Requests);
        rig.Settings.Updates.CheckForUpdates = true;
        await rig.Tick(TimeSpan.FromHours(1));
        Assert.Equal(1, rig.Web.Requests);

        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task The_loop_goes_on_asking_hourly_only_once_the_back_off_is_over()
    {
        var rig = new Rig();
        rig.Web.Down = true;
        using var cts = new CancellationTokenSource();
        var loop = rig.Service.RunAsync(TimeSpan.FromSeconds(60), TimeSpan.FromHours(1), cts.Token);

        await rig.Tick(TimeSpan.FromSeconds(60));
        Assert.Equal(2, rig.Web.Requests); // fails (both ways): 6 hours of quiet
        for (var hour = 1; hour <= 5; hour++) await rig.Tick(TimeSpan.FromHours(1));
        Assert.Equal(2, rig.Web.Requests);

        rig.Web.Down = false;
        await rig.Tick(TimeSpan.FromHours(1)); // 6 hours and 60 seconds after the failure
        Assert.Equal(3, rig.Web.Requests);
        Assert.Single(rig.Announced);

        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task Cancelling_during_the_first_wait_ends_the_loop_without_a_request()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var loop = rig.Service.RunAsync(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), cts.Token);
        cts.Cancel();
        await loop;
        Assert.Equal(0, rig.Web.Requests);
        Assert.Equal(0, rig.Time.Waiting);
    }

    [Fact]
    public async Task Cancelling_during_a_request_ends_the_loop_and_leaves_the_settings_alone()
    {
        var rig = new Rig();
        rig.Web.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var loop = rig.Service.RunAsync(TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), cts.Token);
        var asked = rig.Web.NextRequest();
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await asked;

        cts.Cancel();
        await loop;
        Assert.Null(rig.Settings.Updates.LastCheckUtc);
        Assert.Empty(rig.Announced);
    }
}
