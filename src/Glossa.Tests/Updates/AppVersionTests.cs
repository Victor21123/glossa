using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using Glossa.Core.Updates;

namespace Glossa.Tests.Updates;

public class AppVersionTests
{
    [Theory]
    [InlineData("0.0.3", "0.0.3")]
    [InlineData("v0.0.3", "0.0.3")]
    [InlineData("V0.1.0", "0.1.0")]
    [InlineData("  v1.2.3  ", "1.2.3")]
    [InlineData("0.0.3+53a7fa4", "0.0.3")]
    [InlineData("0.0.3+53a7fa4c1ce0a8f", "0.0.3")]
    [InlineData("0.0.3.0", "0.0.3")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("7", "7.0.0")]
    public void Parses_what_a_tag_or_an_assembly_says(string text, string shown)
    {
        var version = AppVersion.Parse(text);
        Assert.NotNull(version);
        Assert.Equal(shown, version!.Value.ToString());
        Assert.False(version.Value.IsPrerelease);
    }

    [Theory]
    [InlineData("v0.1.0-beta", "0.1.0-beta")]
    [InlineData("0.1.0-rc.1", "0.1.0-rc.1")]
    [InlineData("0.1.0-beta+53a7fa4", "0.1.0-beta")]
    public void A_suffix_after_a_dash_makes_it_a_prerelease(string text, string shown)
    {
        var version = AppVersion.Parse(text);
        Assert.NotNull(version);
        Assert.True(version!.Value.IsPrerelease);
        Assert.Equal(shown, version.Value.ToString());
        Assert.Equal(new Version(0, 1, 0, 0), version.Value.Number);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("v")]
    [InlineData("vv1.0.0")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2")]
    [InlineData("1.2.")]
    [InlineData(".1.2")]
    [InlineData("-1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0 beta")]
    [InlineData("release-0.1.0")]
    [InlineData("99999999999.0.0")]
    [InlineData("0.1.0\n0.2.0")]
    [InlineData("1.0.0-abcdefghijklmnopqrstuvwxyzabcdefghijklmnop")] // a suffix of 41 characters
    [InlineData("1.0.0+")]
    public void Garbage_is_not_a_version(string? text) => Assert.Null(AppVersion.Parse(text));

    [Fact]
    public void Compares_numbers_not_text()
    {
        var order = new[] { "0.0.2", "0.0.3", "0.0.10", "0.1.0", "0.10.0", "1.0.0" }.Select(t => AppVersion.Parse(t)!.Value).ToList();
        for (var i = 0; i < order.Count; i++)
            for (var j = 0; j < order.Count; j++)
                Assert.Equal(Math.Sign(i.CompareTo(j)), Math.Sign(order[i].CompareTo(order[j])));
    }

    [Fact]
    public void Missing_parts_count_as_zero_and_the_tag_prefix_does_not_matter()
    {
        Assert.Equal(AppVersion.Parse("0.0.3"), AppVersion.Parse("v0.0.3"));
        Assert.Equal(AppVersion.Parse("0.0.3"), AppVersion.Parse("0.0.3.0"));
        Assert.Equal(AppVersion.Parse("1.2"), AppVersion.Parse("1.2.0"));
        Assert.Equal(0, AppVersion.Parse("0.0.3+abc")!.Value.CompareTo(AppVersion.Parse("0.0.3+def")!.Value));
    }

    [Fact]
    public void A_prerelease_is_older_than_its_release_and_newer_than_the_one_before()
    {
        var beta = AppVersion.Parse("0.1.0-beta")!.Value;
        Assert.True(beta < AppVersion.Parse("0.1.0")!.Value);
        Assert.True(beta > AppVersion.Parse("0.0.10")!.Value);
        Assert.True(AppVersion.Parse("0.1.0-alpha")!.Value < beta);
        Assert.NotEqual(beta, AppVersion.Parse("0.1.0")!.Value);
    }

    [Theory]
    [InlineData("0.0.3+53a7fa4", "0.0.3")]
    [InlineData("0.0.3", "0.0.3")]
    [InlineData("0.1.0-beta+abc+def", "0.1.0-beta")]
    [InlineData("+abc", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Strips_the_commit_that_dotnet_appends(string? informational, string expected) =>
        Assert.Equal(expected, AppVersion.Strip(informational));

    [Fact]
    public void The_running_version_is_the_one_in_directory_build_props()
    {
        // One place for the number: the props file feeds every assembly, so the app can never disagree with the release script.
        var props = Path.Combine(RepositoryRoot(), "Directory.Build.props");
        var declared = Regex.Match(File.ReadAllText(props), "<Version>([^<]+)</Version>");
        Assert.True(declared.Success, "Directory.Build.props has no <Version>");
        Assert.Equal(AppVersion.Parse(declared.Groups[1].Value), AppVersion.Current);
        Assert.NotEqual(AppVersion.Parse("0.0.0"), AppVersion.Current);
    }

    [Fact]
    public void Reads_the_informational_version_of_an_assembly_and_ignores_the_commit()
    {
        var current = AppVersion.FromAssembly(typeof(AppVersion).Assembly);
        Assert.Equal(AppVersion.Current, current);
        Assert.DoesNotContain("+", current.ToString());
    }

    [Fact]
    public void An_assembly_without_the_attribute_falls_back_to_its_assembly_version()
    {
        // A dynamic assembly really has no informational version attribute.
        var bare = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Bare") { Version = new Version(1, 2, 3, 4) }, AssemblyBuilderAccess.Run);
        Assert.Empty(bare.GetCustomAttributes<AssemblyInformationalVersionAttribute>());
        Assert.Equal(AppVersion.Parse("1.2.3.4"), AppVersion.FromAssembly(bare));
    }

    [Fact]
    public void An_assembly_with_an_unreadable_informational_version_falls_back_too()
    {
        var odd = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Odd") { Version = new Version(2, 0, 0, 0) }, AssemblyBuilderAccess.Run,
            [new CustomAttributeBuilder(typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, ["not a version"])]);
        Assert.Equal(AppVersion.Parse("2.0.0"), AppVersion.FromAssembly(odd));
    }

    [Theory]
    [InlineData("0.0.3+53a7fa422e75a424bd1fb201ab16efac79eb70b3", "53a7fa4")]
    [InlineData("0.0.3+53a7fa4", "53a7fa4")]
    [InlineData("0.0.3+abc", "")]
    [InlineData("0.0.3+zzzzzzzzz", "")]
    [InlineData("0.0.3", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void The_short_commit_comes_from_the_build_part(string? informational, string expected) =>
        Assert.Equal(expected, AppVersion.Commit(informational));

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
        throw new FileNotFoundException("Directory.Build.props above " + AppContext.BaseDirectory);
    }
}
