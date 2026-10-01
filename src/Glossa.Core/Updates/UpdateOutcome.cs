namespace Glossa.Core.Updates;

/// <summary>How a check ended.</summary>
public enum UpdateStatus
{
    /// <summary>The running version is the latest (or newer than the latest release).</summary>
    UpToDate,

    /// <summary>A newer release is out.</summary>
    Available,

    /// <summary>No answer to go by; see <see cref="UpdateFailure"/>.</summary>
    Failed,
}

/// <summary>Why a check failed.</summary>
public enum UpdateFailure
{
    None,

    /// <summary>Not a byte came back by either way (no network, a blocked site, a proxy that does not pass, a timeout).</summary>
    Offline,

    /// <summary>GitHub said no: 403 or 429, the limit of requests from this address.</summary>
    Refused,

    /// <summary>404: the repository has no published release.</summary>
    NoRelease,

    /// <summary>An error status, or an answer that is not a release.</summary>
    BadAnswer,

    /// <summary>The answer was larger than the cap.</summary>
    TooLarge,
}

/// <summary>What a check found: the release it saw (up to date or newer) or why it failed.</summary>
/// <param name="Status">How the check ended.</param>
/// <param name="Release">The release GitHub named; null when the check failed.</param>
/// <param name="Failure">Why it failed; <see cref="UpdateFailure.None"/> otherwise.</param>
/// <param name="Detail">For the log only (at most 200 printable characters, no addresses with a user name): what each way answered. Never shown to the user.</param>
/// <param name="RetryAfter">When GitHub said when to come back (Retry-After, X-RateLimit-Reset), counted from the answer.</param>
public sealed record UpdateOutcome(UpdateStatus Status, ReleaseInfo? Release, UpdateFailure Failure, string? Detail = null, TimeSpan? RetryAfter = null)
{
    public static UpdateOutcome UpToDate(ReleaseInfo release) => new(UpdateStatus.UpToDate, release, UpdateFailure.None);

    public static UpdateOutcome Available(ReleaseInfo release) => new(UpdateStatus.Available, release, UpdateFailure.None);

    public static UpdateOutcome Failed(UpdateFailure why, string? detail = null, TimeSpan? retryAfter = null) =>
        new(UpdateStatus.Failed, null, why, detail is null ? null : UpdateCheck.ForLog(detail), retryAfter);
}
