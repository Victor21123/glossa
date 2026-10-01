namespace Glossa.Tests.Updates;

/// <summary>
/// A clock and timers that move only when a test says so: no sleeps, no races with the real time. <c>Task.Delay(delay, this, ct)</c>
/// waits on <see cref="CreateTimer"/>, so a loop that waits is moved by <see cref="Advance"/>. <see cref="NextTimer"/> completes when
/// the code under test starts its next wait: after it, the loop is known to be idle again.
/// </summary>
public sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = start;
    private TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <summary>Sets the clock without firing any timer.</summary>
    public void SetUtcNow(DateTimeOffset now)
    {
        lock (_gate) _now = now;
    }

    /// <summary>Moves the clock and fires the timers that fell due.</summary>
    public void Advance(TimeSpan by)
    {
        List<FakeTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.DueAt is { } at && at <= _now).ToList();
            foreach (var t in due) t.DueAt = null;
        }
        foreach (var t in due) t.Fire();
    }

    /// <summary>Completes when the next timer is created (the code under test begins its next wait).</summary>
    public Task NextTimer()
    {
        lock (_gate) return _created.Task;
    }

    /// <summary>How many timers are waiting to fire.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate) return _timers.Count(t => t.DueAt is not null);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        TaskCompletionSource signal;
        lock (_gate)
        {
            _timers.Add(timer);
            signal = _created;
            _created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
        return timer;
    }

    private sealed class FakeTimer(FakeTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate) DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate) DueAt = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
