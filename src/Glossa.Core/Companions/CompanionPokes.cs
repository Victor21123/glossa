namespace Glossa.Core.Companions;

/// <summary>
/// Clicks in a row (the user, 2026-10-03): <see cref="Many"/> within <see cref="Window"/> is pestering, and the
/// companion says it has had enough (<see cref="SpeechEvents.Enough"/>) - its own boundary, whatever is clicked.
/// </summary>
public sealed class CompanionPokes
{
    public const int Many = 4;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    private readonly Queue<DateTime> _clicks = new();

    /// <summary>A click now; true when it makes the pestering (the count starts again after).</summary>
    public bool Pester(DateTime utc)
    {
        _clicks.Enqueue(utc);
        while (_clicks.Count > 0 && utc - _clicks.Peek() > Window)
            _clicks.Dequeue();
        if (_clicks.Count < Many)
            return false;
        _clicks.Clear();
        return true;
    }
}
