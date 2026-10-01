using System.Reflection;
using System.Text.RegularExpressions;

namespace Glossa.Core.Updates;

/// <summary>
/// A version as the app and the GitHub tags write it: "0.0.3", "v0.0.3", "0.0.3+53a7fa4" (what .NET appends to the
/// informational version), "v0.1.0-beta". The number is kept as a 4-part <see cref="System.Version"/> so that "1.2"
/// and "1.2.0.0" are one version; the suffix after a dash makes it a prerelease, which sorts before the same number
/// without it (0.1.0-beta &lt; 0.1.0) and is never offered to a user of a stable build (<see cref="UpdateCheck.IsNewer"/>).
/// </summary>
/// <param name="Number">Major.Minor.Build.Revision, every part present.</param>
/// <param name="Suffix">The prerelease label without the dash ("beta", "rc.1"); empty for a release. Compared as text.</param>
public readonly partial record struct AppVersion(Version Number, string Suffix) : IComparable<AppVersion>
{
    /// <summary>Generated at build time: no regex construction on the UI thread at startup. The suffix is at most 40 characters.</summary>
    [GeneratedRegex(@"^[vV]?(?<number>[0-9]{1,9}(?:\.[0-9]{1,9}){0,3})(?:-(?<suffix>[0-9A-Za-z][0-9A-Za-z.\-]{0,39}))?(?:\+[0-9A-Za-z.\-]{1,64})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    [GeneratedRegex("^[0-9a-f]{7,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitShape();

    /// <summary>The version this build carries (<c>&lt;Version&gt;</c> in Directory.Build.props), the same for every assembly of Glossa.</summary>
    public static AppVersion Current { get; } = FromAssembly(typeof(AppVersion).Assembly);

    /// <summary>True for "0.1.0-beta" and the like.</summary>
    public bool IsPrerelease => Suffix.Length > 0;

    /// <summary>
    /// Reads a tag or a version string; null for anything else (empty, text, five parts, a number too large). A leading
    /// "v" and a "+build" tail are ignored, missing parts count as zero.
    /// </summary>
    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length > 160) return null;
        var match = Shape().Match(text.Trim());
        if (!match.Success) return null;
        var parts = match.Groups["number"].Value.Split('.').Select(int.Parse).ToArray();
        int Part(int i) => i < parts.Length ? parts[i] : 0;
        return new AppVersion(new Version(Part(0), Part(1), Part(2), Part(3)), match.Groups["suffix"].Value);
    }

    /// <summary>"0.0.3+53a7fa4" -> "0.0.3": the text before the commit .NET appends; empty when there is none.</summary>
    public static string Strip(string? informational)
    {
        if (informational is null) return "";
        var plus = informational.IndexOf('+');
        return (plus >= 0 ? informational[..plus] : informational).Trim();
    }

    /// <summary>The short commit (7 characters) of "0.0.3+53a7fa4..." for the About line; empty when there is none or it is not a hash.</summary>
    public static string Commit(string? informational)
    {
        var plus = informational?.IndexOf('+') ?? -1;
        if (plus < 0) return "";
        var build = informational![(plus + 1)..].Trim();
        return CommitShape().IsMatch(build) ? build[..7] : "";
    }

    /// <summary>The commit this build was made from (see <see cref="Commit"/>); empty when the build carries none.</summary>
    public static string CurrentCommit { get; } =
        Commit(typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// The version an assembly was built with: its informational version, else its assembly version, else 0.0.0.
    /// Never throws, so a strange build still starts.
    /// </summary>
    public static AppVersion FromAssembly(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Parse(Strip(informational)) ?? Parse(assembly.GetName().Version?.ToString()) ?? new AppVersion(new Version(0, 0, 0, 0), "");
    }

    /// <summary>The number, then a release after its prereleases; prerelease labels compare as text.</summary>
    public int CompareTo(AppVersion other)
    {
        var byNumber = Number.CompareTo(other.Number);
        if (byNumber != 0) return byNumber;
        if (IsPrerelease != other.IsPrerelease) return IsPrerelease ? -1 : 1;
        return string.CompareOrdinal(Suffix, other.Suffix);
    }

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    /// <summary>"0.0.3", "1.2.3.4" or "0.1.0-beta": the form shown to the user and used in the User-Agent.</summary>
    public override string ToString()
    {
        var number = Number.Revision > 0 ? Number.ToString(4) : Number.ToString(3);
        return IsPrerelease ? number + "-" + Suffix : number;
    }
}
