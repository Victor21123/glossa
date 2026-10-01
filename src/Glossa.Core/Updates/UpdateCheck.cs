using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glossa.Core.Config;

namespace Glossa.Core.Updates;

/// <summary>
/// A release as GitHub's "latest release" answer describes it. <see cref="Url"/> is made here from the validated tag
/// (<see cref="UpdateCheck.TagPage"/>): the answer's own html_url is not trusted.
/// </summary>
/// <param name="Tag">"v0.1.0".</param>
/// <param name="Name">The release title (printable text only); the tag when the answer has none.</param>
/// <param name="Url">The release page to open: built from the tag.</param>
/// <param name="Draft">An unpublished release.</param>
/// <param name="Prerelease">Marked as a prerelease on GitHub.</param>
/// <param name="PublishedUtc">When it was published, if the answer says.</param>
public sealed record ReleaseInfo(string Tag, string Name, string Url, bool Draft, bool Prerelease, DateTime? PublishedUtc)
{
    /// <summary>The version the tag names; null for a tag that is not a version.</summary>
    public AppVersion? Version => AppVersion.Parse(Tag);

    /// <summary>Published, not marked a prerelease, and not a prerelease by its tag ("v0.1.0-beta") either.</summary>
    public bool IsStable => !Draft && !Prerelease && Version is { IsPrerelease: false };
}

/// <summary>A newer version that is known and waiting: what the tray item, the settings and a click on the balloon need.</summary>
/// <param name="Version">"0.1.0".</param>
/// <param name="Url">The release page, safe to open.</param>
public sealed record PendingUpdate(string Version, string Url);

/// <summary>
/// The rules of the update check, apart from the network and the screen: reading GitHub's answer, what counts as news,
/// when to ask and when to tell, and which address may be opened. Decided 2026-10-01: Glossa only tells, it never
/// downloads or installs; the answer comes from the one repository named here; prereleases and drafts are never news
/// for a user of a stable build (a user of "0.1.0-beta" is offered the release 0.1.0 and later ones).
/// </summary>
public static partial class UpdateCheck
{
    /// <summary>The one repository the app asks and links to.</summary>
    public const string Repository = "Victor21123/glossa";

    /// <summary>The page of every release: where a link falls back to.</summary>
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    /// <summary>GitHub's answer for the latest published, non-draft, non-prerelease release.</summary>
    public const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    /// <summary>At most one automatic check in this time, also across restarts (<see cref="UpdateSettings.LastCheckUtc"/>).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>How often a running Glossa looks whether the <see cref="Interval"/> is over.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    /// <summary>The first check waits this long after the start: the start and the first lookup do not share the network or the processor with it.</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(60);

    /// <summary>After a check that reached no server (offline): the next automatic try is this much later.</summary>
    public static readonly TimeSpan OfflineRetry = TimeSpan.FromHours(6);

    /// <summary>The longest tag taken.</summary>
    public const int MaxTagLength = 64;

    /// <summary>A tag as the repository writes them: v0.1.0, 0.1, v1.2.3.4, v0.1.0-beta.1. No "+", no other characters.</summary>
    [GeneratedRegex(@"^v?[0-9]+(?:\.[0-9]+){1,3}(?:-[0-9A-Za-z.]{1,40})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagShape();

    /// <summary>The only addresses that may be opened: the releases page, its "latest", or the page of one tag. Exact: no query, no fragment.</summary>
    [GeneratedRegex(@"^https://github\.com/Victor21123/glossa/releases(?:/latest|/tag/v?[0-9]+(?:\.[0-9]+){1,3}(?:-[0-9A-Za-z.]{1,40})?)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PageShape();

    [GeneratedRegex(@"(?<=://)[^/\s@]*@", RegexOptions.CultureInvariant)]
    private static partial Regex Credentials();

    /// <summary>Whether the text is a tag this app takes: at most <see cref="MaxTagLength"/> characters, a version with 2 to 4 numbers.</summary>
    public static bool IsTag(string? tag) =>
        tag is { Length: > 0 and <= MaxTagLength } && TagShape().IsMatch(tag) && AppVersion.Parse(tag) is not null;

    /// <summary>The release page of a tag, built here: <see cref="ReleasesPage"/>/tag/&lt;tag&gt;.</summary>
    public static string TagPage(string tag) => ReleasesPage + "/tag/" + tag;

    /// <summary>
    /// Reads the "latest release" JSON. Missing or oddly typed fields get defaults (name -> tag, flags -> false, date -> none);
    /// null when it is not JSON, not an object, has a string that cannot be read (a lone surrogate), or has no strict tag
    /// (<see cref="IsTag"/>): GitHub's error answers, an HTML page from a captive portal. The release page is built from the
    /// tag; the answer's html_url is ignored.
    /// </summary>
    public static ReleaseInfo? ParseRelease(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Text(root, "tag_name") is not { } tag || !IsTag(tag)) return null;
            DateTime? published = Text(root, "published_at") is { } at
                && DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed : null;
            return new ReleaseInfo(tag, Plain(Text(root, "name"), 100) is { Length: > 0 } name ? name : tag, TagPage(tag),
                Flag(root, "draft"), Flag(root, "prerelease"), published);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            // A lone surrogate in a string throws InvalidOperationException when it is read: a bad answer, not a crash.
            return null;
        }
    }

    private static string? Text(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement o, string key) => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The address if it is exactly this repository's releases page, its "latest", or the page of one tag
    /// (https://github.com/Victor21123/glossa/releases[/latest|/tag/&lt;tag&gt;]); anything else gives <see cref="ReleasesPage"/>.
    /// The whole string must match: no query, fragment, user name, port, other case or trailing slash. The result goes to the shell.
    /// </summary>
    public static string SafeUrl(string? url) =>
        url is { Length: <= 200 } && PageShape().IsMatch(url) ? url : ReleasesPage;

    /// <summary>The text without control and invisible formatting characters (bidi marks, zero-width), cut to <paramref name="max"/>: safe to print.</summary>
    public static string Plain(string? text, int max = 200)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clean = new StringBuilder(Math.Min(text.Length, max));
        foreach (var c in text)
        {
            if (clean.Length >= max) break;
            if (char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue;
            clean.Append(c);
        }
        return clean.ToString().Trim();
    }

    /// <summary>A line for the log: control characters become spaces, "user:pass@" is cut out of addresses, invisible formatting is dropped, at most 200 characters.</summary>
    public static string ForLog(string? text, int max = 200)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var spaced = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return Plain(Credentials().Replace(spaced, ""), max);
    }

    /// <summary>Whether the release is news for this version: published, stable, and higher. A tag that is not a version is never news.</summary>
    public static bool IsNewer(ReleaseInfo release, AppVersion current) =>
        release.IsStable && release.Version is { } version && version > current;

    /// <summary>News the user has not been told about yet: a newer release whose version is not the one already announced.</summary>
    public static bool ShouldNotify(ReleaseInfo release, AppVersion current, UpdateSettings updates) =>
        IsNewer(release, current) && AppVersion.Parse(updates.NotifiedVersion) != release.Version;

    /// <summary>
    /// Whether an automatic check is due: the switch is on, the back-off after a failure (<paramref name="notBeforeUtc"/>) is
    /// over, and the last answer is 24 hours old or more (or from the future, a clock set back).
    /// </summary>
    public static bool IsDue(UpdateSettings updates, DateTime nowUtc, DateTime? notBeforeUtc = null) =>
        updates.CheckForUpdates && (notBeforeUtc is not { } wait || nowUtc >= wait)
        && (updates.LastCheckUtc is not { } last || last > nowUtc || nowUtc - last >= Interval);

    /// <summary>
    /// How long to leave GitHub alone after a failed check: 6 hours when no way reached it (offline), a full 24 hours when it
    /// did answer with something unusable (404, 403/429, an error, not a release, too large), so a standing problem is not
    /// asked about every hour. A Retry-After or X-RateLimit-Reset the answer carried is honoured, from one minute to 24 hours.
    /// </summary>
    public static TimeSpan RetryDelay(UpdateOutcome failed)
    {
        if (failed.RetryAfter is { } asked) return asked < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : asked > Interval ? Interval : asked;
        return failed.Failure == UpdateFailure.Offline ? OfflineRetry : Interval;
    }

    /// <summary>
    /// Remembers an answer: when it came and what the latest release is. A failure changes nothing here (the service keeps
    /// the back-off in memory).
    /// </summary>
    public static void Record(UpdateSettings updates, UpdateOutcome outcome, DateTime nowUtc)
    {
        if (outcome.Status == UpdateStatus.Failed || outcome.Release is not { } release) return;
        updates.LastCheckUtc = nowUtc;
        updates.LatestVersion = release.IsStable ? release.Version!.Value.ToString() : "";
        updates.LatestUrl = release.Url;
    }

    /// <summary>The newer version the settings remember, until the running one catches up; the address is checked again (the file can be edited by hand).</summary>
    public static PendingUpdate? Pending(UpdateSettings updates, AppVersion current) =>
        AppVersion.Parse(updates.LatestVersion) is { IsPrerelease: false } latest && latest > current
            ? new PendingUpdate(latest.ToString(), SafeUrl(updates.LatestUrl))
            : null;
}
