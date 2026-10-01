namespace Glossa.Core.Notices;

/// <summary>What a click on the tray notice that is on screen opens.</summary>
public enum NoticeKind
{
    /// <summary>A plain notice: a click does nothing.</summary>
    Nothing,

    /// <summary>The evening reminder: opens «Учёба».</summary>
    Study,

    /// <summary>A newer version: opens its release page (<see cref="NoticeTarget.TakeClick"/> returns the address).</summary>
    Update,
}

/// <summary>
/// Which tray notice a click belongs to. The tray reports a click (BalloonTipClicked) and an end (BalloonTipClosed) with no
/// word about which balloon, and Windows 10/11 raises the end late or not at all, so the state is kept here: every notice
/// that is shown says what a click does (plain ones say nothing), a click counts only while the notice can still be on
/// screen (its time plus <see cref="ClickGrace"/>), and an end reported within <see cref="CloseGrace"/> of a show is the
/// end of the balloon that was replaced, not of the new one. Thread-safe.
/// </summary>
public sealed class NoticeTarget(TimeProvider? time = null)
{
    /// <summary>A Closed event this soon after a show belongs to the previous balloon and is ignored.</summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(1);

    /// <summary>A click is taken this long after the notice's own time is over.</summary>
    public static readonly TimeSpan ClickGrace = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private NoticeKind _kind;
    private string? _target;
    private DateTimeOffset _shown;
    private DateTimeOffset _expires;

    /// <summary>A notice is being shown for <paramref name="duration"/>: remember what a click on it does (<see cref="NoticeKind.Nothing"/> for a plain one).</summary>
    public void Show(NoticeKind kind, string? target, TimeSpan duration)
    {
        lock (_gate)
        {
            _kind = kind;
            _target = target;
            _shown = _time.GetUtcNow();
            _expires = _shown + duration + ClickGrace;
        }
    }

    /// <summary>The tray reported a click: what it opens, once; <see cref="NoticeKind.Nothing"/> when the notice was plain or is over.</summary>
    public (NoticeKind Kind, string? Target) TakeClick()
    {
        lock (_gate)
        {
            var result = _time.GetUtcNow() <= _expires ? (_kind, _target) : (NoticeKind.Nothing, null);
            _kind = NoticeKind.Nothing;
            _target = null;
            return result;
        }
    }

    /// <summary>The tray reported the end of a notice: forgets the target, unless that is the late end of a balloon this one replaced.</summary>
    public void Closed()
    {
        lock (_gate)
        {
            if (_time.GetUtcNow() - _shown < CloseGrace) return;
            _kind = NoticeKind.Nothing;
            _target = null;
        }
    }
}
