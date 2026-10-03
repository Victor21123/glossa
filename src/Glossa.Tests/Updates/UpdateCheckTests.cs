using Glossa.Core.Config;
using Glossa.Core.Updates;

namespace Glossa.Tests.Updates;

public class UpdateCheckTests
{
    /// <summary>The shape of GET /repos/Victor21123/glossa/releases/latest (2026-10-01), long fields cut down.</summary>
    private const string Real = """
        {
          "url": "https://api.github.com/repos/Victor21123/glossa/releases/401175112",
          "assets_url": "https://api.github.com/repos/Victor21123/glossa/releases/401175112/assets",
          "html_url": "https://github.com/Victor21123/glossa/releases/tag/v0.0.3",
          "id": 401175112,
          "author": {"login": "Victor21123", "id": 50230403, "type": "User", "site_admin": false},
          "tag_name": "v0.0.3",
          "target_commitish": "main",
          "name": "Glossa 0.0.3",
          "draft": false,
          "immutable": false,
          "prerelease": false,
          "created_at": "2026-10-01T16:59:44Z",
          "published_at": "2026-10-01T17:00:39Z",
          "assets": [{"id": 1, "name": "Glossa-0.0.3-win-x64.zip", "size": 612345678,
                      "browser_download_url": "https://github.com/Victor21123/glossa/releases/download/v0.0.3/Glossa-0.0.3-win-x64.zip"}],
          "body": "Первый релиз.\n\n## Что нового\n- одно\n- два"
        }
        """;

    private static readonly AppVersion V003 = AppVersion.Parse("0.0.3")!.Value;
    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    private static ReleaseInfo Release(string tag, bool draft = false, bool prerelease = false) =>
        new(tag, "Glossa " + tag, UpdateCheck.ReleasesPage + "/tag/" + tag, draft, prerelease, Now);

    // ---- parsing ----

    [Fact]
    public void Parses_a_real_shaped_answer()
    {
        var release = UpdateCheck.ParseRelease(Real);
        Assert.NotNull(release);
        Assert.Equal("v0.0.3", release!.Tag);
        Assert.Equal("Glossa 0.0.3", release.Name);
        Assert.Equal("https://github.com/Victor21123/glossa/releases/tag/v0.0.3", release.Url);
        Assert.False(release.Draft);
        Assert.False(release.Prerelease);
        Assert.Equal(new DateTime(2026, 10, 1, 17, 0, 39, DateTimeKind.Utc), release.PublishedUtc);
        Assert.Equal(DateTimeKind.Utc, release.PublishedUtc!.Value.Kind);
        Assert.Equal(AppVersion.Parse("0.0.3"), release.Version);
    }

    [Fact]
    public void Missing_fields_get_safe_defaults()
    {
        var release = UpdateCheck.ParseRelease("""{"tag_name": "v0.2.0"}""");
        Assert.NotNull(release);
        Assert.Equal("v0.2.0", release!.Tag);
        Assert.Equal("v0.2.0", release.Name);
        Assert.Equal(UpdateCheck.TagPage("v0.2.0"), release.Url);
        Assert.False(release.Draft);
        Assert.False(release.Prerelease);
        Assert.Null(release.PublishedUtc);
    }

    [Fact]
    public void Draft_and_prerelease_flags_are_read()
    {
        var release = UpdateCheck.ParseRelease("""{"tag_name": "v0.2.0", "draft": true, "prerelease": true}""");
        Assert.True(release!.Draft);
        Assert.True(release.Prerelease);
    }

    [Theory]
    [InlineData("""{"tag_name": "v0.2.0", "draft": "yes", "prerelease": null, "name": 5, "html_url": 7, "published_at": "yesterday"}""")]
    [InlineData("""{"tag_name": "v0.2.0", "draft": 1, "prerelease": [], "name": {}, "html_url": [], "published_at": 5}""")]
    public void Fields_of_the_wrong_type_are_ignored_not_fatal(string json)
    {
        var release = UpdateCheck.ParseRelease(json);
        Assert.NotNull(release);
        Assert.False(release!.Draft);
        Assert.False(release.Prerelease);
        Assert.Equal("v0.2.0", release.Name);
        Assert.Equal(UpdateCheck.TagPage("v0.2.0"), release.Url);
        Assert.Null(release.PublishedUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("<html>Rate limited</html>")]
    [InlineData("[]")]
    [InlineData("\"v0.1.0\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"message": "Not Found", "documentation_url": "https://docs.github.com/rest"}""")]
    [InlineData("""{"tag_name": ""}""")]
    [InlineData("""{"tag_name": 5}""")]
    [InlineData("""{"tag_name": null}""")]
    [InlineData("""{"tag_name": "nightly"}""")]
    [InlineData("""{"tag_name": "v1"}""")]
    [InlineData("""{"tag_name": "v0.1.0+build5"}""")]
    [InlineData("""{"tag_name": "v0.1.0 "}""")]
    [InlineData("""{"tag_name": " v0.1.0"}""")]
    [InlineData("""{"tag_name": "v0.1.0-"}""")]
    [InlineData("""{"tag_name": "v0.1.0/../x"}""")]
    [InlineData("""{"tag_name": "v0.1.0.1.2"}""")]
    [InlineData("""{"tag_name": "v0.1.0-abcdefghijklmnopqrstuvwxyzabcdefghijklmnop"}""")]
    [InlineData("""{"tag_name": "v0.2.0""")]
    public void An_answer_without_a_usable_tag_is_no_release(string? json) => Assert.Null(UpdateCheck.ParseRelease(json));

    [Fact]
    public void The_address_of_the_answer_is_never_used_the_page_is_built_from_the_tag()
    {
        var release = UpdateCheck.ParseRelease("""{"tag_name": "v9.9.9", "html_url": "https://evil.example/glossa"}""");
        Assert.Equal(UpdateCheck.TagPage("v9.9.9"), release!.Url);
        // Even an address that would pass the check is not taken: only the tag decides.
        release = UpdateCheck.ParseRelease("""{"tag_name": "v9.9.9", "html_url": "https://github.com/Victor21123/glossa/releases/tag/v1.0.0"}""");
        Assert.Equal(UpdateCheck.TagPage("v9.9.9"), release!.Url);
    }

    [Fact]
    public void A_string_that_cannot_be_read_is_a_bad_answer_not_a_crash()
    {
        // A lone surrogate escape: reading the string throws InvalidOperationException.
        var lone = "{\"tag_name\": \"v0.2.0\", \"name\": \"" + (char)92 + "ud800\"}";
        Assert.Null(UpdateCheck.ParseRelease(lone));
        var inTag = "{\"tag_name\": \"v0.2" + (char)92 + "ud800\"}";
        Assert.Null(UpdateCheck.ParseRelease(inTag));
    }

    [Fact]
    public void The_title_is_printable_text_only()
    {
        var tricky = "Glossa " + (char)0x202E + "0.1.0" + (char)0x200F + (char)7 + " done";
        var json = System.Text.Json.JsonSerializer.Serialize(new { tag_name = "v0.1.0", name = tricky });
        Assert.Equal("Glossa 0.1.0 done", UpdateCheck.ParseRelease(json)!.Name);
        var longName = System.Text.Json.JsonSerializer.Serialize(new { tag_name = "v0.1.0", name = new string('x', 5000) });
        Assert.Equal(100, UpdateCheck.ParseRelease(longName)!.Name.Length);
    }

    [Theory]
    [InlineData("v0.1.0", true)]
    [InlineData("0.1", true)]
    [InlineData("v1.2.3.4", true)]
    [InlineData("v0.1.0-beta.1", true)]
    [InlineData("v1", false)]
    [InlineData("v0.1.0+x", false)]
    [InlineData("v0.1.0-", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("release", false)]
    [InlineData("v99999999999.0", false)]
    public void Only_a_strict_tag_is_a_tag(string? tag, bool ok) => Assert.Equal(ok, UpdateCheck.IsTag(tag));

    [Fact]
    public void A_tag_is_at_most_64_characters()
    {
        var suffix = new string('a', 40);
        var fits = "v0.1.0-" + suffix; // 47
        Assert.True(UpdateCheck.IsTag(fits));
        Assert.False(UpdateCheck.IsTag("v1." + new string('1', 70)));
    }

    // ---- the page to open ----

    [Theory]
    [InlineData("https://github.com/Victor21123/glossa/releases")]
    [InlineData("https://github.com/Victor21123/glossa/releases/latest")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/0.1")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/v1.2.3.4")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/v0.1.0-beta.1")]
    public void A_release_page_of_this_repository_is_kept_as_it_is(string url) => Assert.Equal(url, UpdateCheck.SafeUrl(url));

    [Theory]
    [InlineData("http://github.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("ftp://github.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://evil.example/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://github.com.evil.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://evilgithub.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://www.github.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://GitHub.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://github.com:443/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://github.com:8443/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://api.github.com/repos/Victor21123/glossa/releases/401175112")]
    [InlineData("https://github.com@evil.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://evil.com@github.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://user:pass@github.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://github.com\\@evil.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://github.com/Other/glossa/releases/tag/v0.1.0")]
    [InlineData("https://github.com/Victor21123/other/releases/tag/v0.1.0")]
    [InlineData("https://github.com/victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://github.com/Victor21123/glossa-evil/releases/tag/v0.1.0")]
    [InlineData("https://github.com/Victor21123/glossa/issues/1")]
    [InlineData("https://github.com/Victor21123/glossa/releasesevil")]
    [InlineData("https://github.com/Victor21123/glossa/releases/")]
    [InlineData("https://github.com/Victor21123/glossa/releases/latest/")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/v0.1.0/")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/nightly")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/v0.1.0+x")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/v0.1.0-abcdefghijklmnopqrstuvwxyzabcdefghijklmnop")]
    [InlineData("https://github.com/Victor21123/glossa/releases/download/v0.0.3/Glossa-0.0.3-win-x64.zip")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/v0.1.0?x=1#top")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/v0.1.0?next=//evil.com")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/v0.1.0#frag")]
    [InlineData("https://github.com/Victor21123/glossa/releases?next=https://evil.com")]
    [InlineData("https://github.com/Victor21123/glossa/releases/../../evil/repo/releases/tag/v1.0")]
    [InlineData("https://github.com/Victor21123/glossa/releases/%2e%2e/%2e%2e/evil/repo")]
    [InlineData("https://github.com//evil.com/Victor21123/glossa/releases/")]
    [InlineData("//github.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("//evil.com")]
    [InlineData("/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("github.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/v0.1.0 && calc")]
    [InlineData("https://github.com/Victor21123/glossa/releases/tag/v0.1.0\n")]
    [InlineData("  https://github.com/Victor21123/glossa/releases/tag/v0.1.0")]
    [InlineData("  ")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_falls_back_to_the_releases_page(string? url) => Assert.Equal(UpdateCheck.ReleasesPage, UpdateCheck.SafeUrl(url));

    [Fact]
    public void A_very_long_address_falls_back()
    {
        Assert.Equal(UpdateCheck.ReleasesPage, UpdateCheck.SafeUrl(UpdateCheck.ReleasesPage + "/tag/v0.1.0-" + new string('a', 300)));
    }

    [Fact]
    public void The_constant_page_is_itself_safe_and_names_the_one_repository()
    {
        Assert.Equal("Victor21123/glossa", UpdateCheck.Repository);
        Assert.Equal("https://github.com/Victor21123/glossa/releases", UpdateCheck.ReleasesPage);
        Assert.Equal("https://api.github.com/repos/Victor21123/glossa/releases/latest", UpdateCheck.LatestApi);
        Assert.Equal(UpdateCheck.ReleasesPage, UpdateCheck.SafeUrl(UpdateCheck.ReleasesPage));
        Assert.Equal(UpdateCheck.ReleasesPage + "/tag/v0.1.0", UpdateCheck.TagPage("v0.1.0"));
        Assert.Equal(UpdateCheck.TagPage("v0.1.0"), UpdateCheck.SafeUrl(UpdateCheck.TagPage("v0.1.0")));
    }

    // ---- is it news ----

    [Theory]
    [InlineData("v0.0.4", true)]
    [InlineData("v0.1.0", true)]
    [InlineData("v0.0.10", true)]
    [InlineData("v1.0.0", true)]
    [InlineData("v0.0.3", false)]
    [InlineData("0.0.3", false)]
    [InlineData("v0.0.2", false)]
    public void Only_a_higher_version_is_newer(string tag, bool newer) => Assert.Equal(newer, UpdateCheck.IsNewer(Release(tag), V003));

    [Fact]
    public void A_draft_or_a_prerelease_is_never_newer_for_a_stable_user()
    {
        Assert.False(UpdateCheck.IsNewer(Release("v0.1.0", draft: true), V003));
        Assert.False(UpdateCheck.IsNewer(Release("v0.1.0", prerelease: true), V003));
        Assert.False(UpdateCheck.IsNewer(Release("v0.1.0-beta"), V003)); // flagged stable, but the tag says beta
        Assert.False(UpdateCheck.IsNewer(Release("v0.1.0-rc.1", prerelease: true), V003));
    }

    [Fact]
    public void A_user_on_a_prerelease_is_offered_its_release()
    {
        var beta = AppVersion.Parse("0.1.0-beta")!.Value;
        Assert.True(UpdateCheck.IsNewer(Release("v0.1.0"), beta));
        Assert.False(UpdateCheck.IsNewer(Release("v0.0.9"), beta));
        Assert.False(UpdateCheck.IsNewer(Release("v0.1.0-beta"), beta));
    }

    [Fact]
    public void A_tag_that_is_not_a_version_is_not_news()
    {
        Assert.False(UpdateCheck.IsNewer(new ReleaseInfo("nightly", "n", UpdateCheck.ReleasesPage, false, false, null), V003));
    }

    // ---- when to tell ----

    [Fact]
    public void Tells_once_per_version()
    {
        var updates = new UpdateSettings();
        Assert.True(UpdateCheck.ShouldNotify(Release("v0.1.0"), V003, updates));

        updates.NotifiedVersion = "0.1.0";
        Assert.False(UpdateCheck.ShouldNotify(Release("v0.1.0"), V003, updates));
        Assert.False(UpdateCheck.ShouldNotify(Release("0.1.0"), V003, updates)); // the spelling does not matter
        Assert.True(UpdateCheck.ShouldNotify(Release("v0.1.1"), V003, updates)); // a newer one is news again
    }

    [Fact]
    public void Does_not_tell_about_what_is_not_news()
    {
        var updates = new UpdateSettings();
        Assert.False(UpdateCheck.ShouldNotify(Release("v0.0.3"), V003, updates));
        Assert.False(UpdateCheck.ShouldNotify(Release("v0.2.0", prerelease: true), V003, updates));
        Assert.False(UpdateCheck.ShouldNotify(Release("v0.2.0", draft: true), V003, updates));
    }

    [Fact]
    public void A_garbled_notified_version_still_lets_the_news_through()
    {
        var updates = new UpdateSettings { NotifiedVersion = "????" };
        Assert.True(UpdateCheck.ShouldNotify(Release("v0.1.0"), V003, updates));
    }

    // ---- when to ask ----

    [Fact]
    public void Asks_at_most_once_in_24_hours()
    {
        var updates = new UpdateSettings();
        Assert.True(UpdateCheck.IsDue(updates, Now)); // never asked

        updates.LastCheckUtc = Now.AddHours(-23).AddMinutes(-59);
        Assert.False(UpdateCheck.IsDue(updates, Now));
        updates.LastCheckUtc = Now.AddHours(-24);
        Assert.True(UpdateCheck.IsDue(updates, Now));
        updates.LastCheckUtc = Now.AddDays(-30);
        Assert.True(UpdateCheck.IsDue(updates, Now));
        updates.LastCheckUtc = Now.AddMinutes(-1);
        Assert.False(UpdateCheck.IsDue(updates, Now));
    }

    [Fact]
    public void A_last_check_in_the_future_does_not_silence_the_check()
    {
        // The clock was set back: waiting for it to catch up could mean months without a check.
        var updates = new UpdateSettings { LastCheckUtc = Now.AddDays(40) };
        Assert.True(UpdateCheck.IsDue(updates, Now));
    }

    [Fact]
    public void A_back_off_after_a_failure_holds_the_check_back_even_when_a_day_has_passed()
    {
        var updates = new UpdateSettings { LastCheckUtc = Now.AddDays(-5) };
        Assert.True(UpdateCheck.IsDue(updates, Now, notBeforeUtc: null));
        Assert.False(UpdateCheck.IsDue(updates, Now, notBeforeUtc: Now.AddMinutes(1)));
        Assert.True(UpdateCheck.IsDue(updates, Now, notBeforeUtc: Now));
        Assert.True(UpdateCheck.IsDue(updates, Now, notBeforeUtc: Now.AddMinutes(-1)));
    }

    [Fact]
    public void Offline_is_tried_again_in_6_hours_and_any_other_failure_in_24()
    {
        Assert.Equal(TimeSpan.FromHours(6), UpdateCheck.RetryDelay(UpdateOutcome.Failed(UpdateFailure.Offline)));
        foreach (var failure in new[] { UpdateFailure.NoRelease, UpdateFailure.Refused, UpdateFailure.BadAnswer, UpdateFailure.TooLarge })
            Assert.Equal(TimeSpan.FromHours(24), UpdateCheck.RetryDelay(UpdateOutcome.Failed(failure)));
    }

    [Fact]
    public void What_GitHub_asks_for_is_honoured_between_one_minute_and_24_hours()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), UpdateCheck.RetryDelay(UpdateOutcome.Failed(UpdateFailure.Refused, null, TimeSpan.FromMinutes(30))));
        Assert.Equal(TimeSpan.FromMinutes(1), UpdateCheck.RetryDelay(UpdateOutcome.Failed(UpdateFailure.Refused, null, TimeSpan.FromSeconds(5))));
        Assert.Equal(TimeSpan.FromHours(24), UpdateCheck.RetryDelay(UpdateOutcome.Failed(UpdateFailure.Refused, null, TimeSpan.FromDays(30))));
    }

    [Fact]
    public void The_switch_off_means_never_due()
    {
        var updates = new UpdateSettings { CheckForUpdates = false };
        Assert.False(UpdateCheck.IsDue(updates, Now));
        updates.LastCheckUtc = Now.AddDays(-30);
        Assert.False(UpdateCheck.IsDue(updates, Now));
    }

    [Fact]
    public void The_24_hours_are_kept_apart_from_the_hourly_wake_up()
    {
        Assert.Equal(TimeSpan.FromHours(24), UpdateCheck.Interval);
        Assert.True(UpdateCheck.PollInterval < UpdateCheck.Interval);
        Assert.True(UpdateCheck.FirstCheckDelay >= TimeSpan.FromSeconds(30));
    }

    // ---- what an answer changes ----

    [Fact]
    public void A_good_answer_is_remembered()
    {
        var updates = new UpdateSettings();
        UpdateCheck.Record(updates, UpdateOutcome.Available(Release("v0.1.0")), Now);
        Assert.Equal(Now, updates.LastCheckUtc);
        Assert.Equal("0.1.0", updates.LatestVersion);
        Assert.Equal(UpdateCheck.ReleasesPage + "/tag/v0.1.0", updates.LatestUrl);
    }

    [Fact]
    public void Up_to_date_is_remembered_with_the_version_that_is_latest()
    {
        var updates = new UpdateSettings();
        UpdateCheck.Record(updates, UpdateOutcome.UpToDate(Release("v0.0.3")), Now);
        Assert.Equal(Now, updates.LastCheckUtc);
        Assert.Equal("0.0.3", updates.LatestVersion);
    }

    [Fact]
    public void A_failure_changes_nothing_so_that_the_check_is_tried_again()
    {
        var updates = new UpdateSettings { LastCheckUtc = Now.AddDays(-2), LatestVersion = "0.1.0", LatestUrl = UpdateCheck.ReleasesPage };
        UpdateCheck.Record(updates, UpdateOutcome.Failed(UpdateFailure.Offline), Now);
        Assert.Equal(Now.AddDays(-2), updates.LastCheckUtc);
        Assert.Equal("0.1.0", updates.LatestVersion);
        Assert.True(UpdateCheck.IsDue(updates, Now));
    }

    [Fact]
    public void A_known_newer_version_is_pending_until_the_user_has_it()
    {
        var updates = new UpdateSettings { LatestVersion = "0.1.0", LatestUrl = UpdateCheck.ReleasesPage + "/tag/v0.1.0" };
        var pending = UpdateCheck.Pending(updates, V003);
        Assert.NotNull(pending);
        Assert.Equal("0.1.0", pending!.Version);
        Assert.Equal(UpdateCheck.ReleasesPage + "/tag/v0.1.0", pending.Url);

        Assert.Null(UpdateCheck.Pending(updates, AppVersion.Parse("0.1.0")!.Value)); // updated by hand
        Assert.Null(UpdateCheck.Pending(updates, AppVersion.Parse("0.2.0")!.Value));
        Assert.Null(UpdateCheck.Pending(new UpdateSettings(), V003));
        Assert.Null(UpdateCheck.Pending(new UpdateSettings { LatestVersion = "garbage" }, V003));
        Assert.Null(UpdateCheck.Pending(new UpdateSettings { LatestVersion = "0.2.0-beta" }, V003));
    }

    [Fact]
    public void The_pending_address_comes_from_a_file_a_user_can_edit_so_it_is_checked_again()
    {
        var updates = new UpdateSettings { LatestVersion = "0.1.0", LatestUrl = "https://evil.example/" };
        Assert.Equal(UpdateCheck.ReleasesPage, UpdateCheck.Pending(updates, V003)!.Url);
        updates.LatestUrl = "";
        Assert.Equal(UpdateCheck.ReleasesPage, UpdateCheck.Pending(updates, V003)!.Url);
    }

    // ---- the words shown ----

    [Fact]
    public void The_balloon_says_which_version_and_what_a_click_does()
    {
        Assert.Equal("Вышла Glossa 0.1.0", UpdateTexts.BalloonTitle("0.1.0"));
        Assert.Equal("Щёлкни, чтобы открыть страницу на GitHub", UpdateTexts.BalloonText);
        Assert.Equal("Доступна 0.1.0 - открыть", UpdateTexts.TrayItem("0.1.0"));
    }

    [Fact]
    public void Each_outcome_has_its_own_line_for_the_settings()
    {
        Assert.Equal("Установлена последняя версия", UpdateTexts.For(UpdateOutcome.UpToDate(Release("v0.0.3"))));
        Assert.Equal("Доступна 0.1.0", UpdateTexts.For(UpdateOutcome.Available(Release("v0.1.0"))));
        Assert.Equal("Не удалось проверить: нет сети", UpdateTexts.For(UpdateOutcome.Failed(UpdateFailure.Offline)));
        Assert.Equal("Не удалось проверить: GitHub отказал (слишком много запросов), попробуй позже", UpdateTexts.For(UpdateOutcome.Failed(UpdateFailure.Refused)));
        Assert.StartsWith("Не удалось проверить: ", UpdateTexts.For(UpdateOutcome.Failed(UpdateFailure.BadAnswer)));
        Assert.StartsWith("Не удалось проверить: ", UpdateTexts.For(UpdateOutcome.Failed(UpdateFailure.NoRelease)));
        Assert.StartsWith("Не удалось проверить: ", UpdateTexts.For(UpdateOutcome.Failed(UpdateFailure.TooLarge)));
    }

    [Fact]
    public void Without_a_fresh_answer_the_line_comes_from_what_is_remembered()
    {
        Assert.Equal("Ещё не проверялось", UpdateTexts.Summary(new UpdateSettings(), V003));

        var seen = new UpdateSettings { LastCheckUtc = new DateTime(2026, 10, 1, 17, 5, 0, DateTimeKind.Utc), LatestVersion = "0.0.3" };
        var local = seen.LastCheckUtc!.Value.ToLocalTime();
        Assert.Equal($"Установлена последняя версия (проверено {local.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)})", UpdateTexts.Summary(seen, V003));

        seen.LatestVersion = "0.1.0";
        Assert.Equal("Доступна 0.1.0", UpdateTexts.Summary(seen, V003));
    }

    [Fact]
    public void Every_text_follows_the_style_rules_plain_keyboard_characters_only()
    {
        var texts = new List<string>
        {
            UpdateTexts.BalloonTitle("0.1.0"), UpdateTexts.BalloonText, UpdateTexts.TrayItem("0.1.0"),
            UpdateTexts.Summary(new UpdateSettings(), V003),
            UpdateTexts.Summary(new UpdateSettings { LastCheckUtc = Now, LatestVersion = "0.0.3" }, V003),
            UpdateTexts.For(UpdateOutcome.UpToDate(Release("v0.0.3"))), UpdateTexts.For(UpdateOutcome.Available(Release("v0.1.0"))),
        };
        texts.AddRange(Enum.GetValues<UpdateFailure>().Where(f => f != UpdateFailure.None).Select(f => UpdateTexts.For(UpdateOutcome.Failed(f))));
        foreach (var text in texts)
        {
            Assert.NotEmpty(text);
            foreach (var c in text)
                Assert.True(c is >= ' ' and <= '~' or >= '\u0410' and <= '\u044f' or '\u0401' or '\u0451', $"\"{text}\" has U+{(int)c:X4}");
        }
    }

    // ---- printing ----

    [Fact]
    public void Plain_drops_control_and_bidi_characters_and_cuts_long_text()
    {
        var bidi = new string([(char)0x200E, (char)0x200F, (char)0x202A, (char)0x202B, (char)0x202C, (char)0x202D, (char)0x202E, (char)0x2066, (char)0x2067, (char)0x2068, (char)0x2069, (char)0x200B, (char)0xFEFF]);
        Assert.Equal("Glossa 0.1.0", UpdateCheck.Plain("Glossa " + bidi + "0.1.0\r\n\t" + (char)0));
        Assert.Equal("Привет, мир", UpdateCheck.Plain("Привет, мир"));
        Assert.Equal(10, UpdateCheck.Plain(new string('x', 500), 10).Length);
        Assert.Equal("", UpdateCheck.Plain(null));
    }

    [Fact]
    public void A_log_line_has_no_control_characters_no_credentials_and_a_limit()
    {
        Assert.Equal("proxy: failed for https://proxy.local:8080/ ok", UpdateCheck.ForLog("proxy: failed for https://user:pa55@proxy.local:8080/\r\nok".Replace("\r\n", " ")));
        Assert.DoesNotContain("pa55", UpdateCheck.ForLog("http://user:pa55@host/x and https://other:secret@h2/y"));
        Assert.DoesNotContain("secret", UpdateCheck.ForLog("http://user:pa55@host/x and https://other:secret@h2/y"));
        Assert.Equal("a b c", UpdateCheck.ForLog("a\nb\tc"));
        Assert.Equal(200, UpdateCheck.ForLog(new string('x', 1000)).Length);
        Assert.Equal(200, UpdateOutcome.Failed(UpdateFailure.Offline, new string('y', 1000)).Detail!.Length);
        Assert.DoesNotContain("pass", UpdateOutcome.Failed(UpdateFailure.Offline, "ProxyTunnel https://me:pass@proxy:3128/").Detail);
    }

    // ---- settings ----

    [Fact]
    public void The_check_is_on_by_default_and_nothing_is_known_yet()
    {
        var updates = new AppSettings().Updates;
        Assert.True(updates.CheckForUpdates);
        Assert.Null(updates.LastCheckUtc);
        Assert.Equal("", updates.NotifiedVersion);
        Assert.Equal("", updates.LatestVersion);
        Assert.Equal("", updates.LatestUrl);
    }

    [Fact]
    public void An_old_settings_file_without_the_section_loads_with_defaults()
    {
        using var folders = new TestFolders();
        var path = Path.Combine(folders.New(), "settings.json");
        File.WriteAllText(path, """{"Hotkey": "Alt+W", "CloseToTray": false, "Study": {"NewPerDay": 7}}""");

        var loaded = new SettingsStore(path).Load();
        Assert.Equal("Alt+W", loaded.Hotkey);
        Assert.False(loaded.CloseToTray);
        Assert.NotNull(loaded.Updates);
        Assert.True(loaded.Updates.CheckForUpdates);
        Assert.Null(loaded.Updates.LastCheckUtc);
        Assert.Equal("", loaded.Updates.NotifiedVersion);
    }

    [Fact]
    public void A_section_with_some_keys_keeps_them_and_defaults_the_rest()
    {
        using var folders = new TestFolders();
        var path = Path.Combine(folders.New(), "settings.json");
        File.WriteAllText(path, """{"Updates": {"CheckForUpdates": false, "NotifiedVersion": "0.1.0"}}""");

        var updates = new SettingsStore(path).Load().Updates;
        Assert.False(updates.CheckForUpdates);
        Assert.Equal("0.1.0", updates.NotifiedVersion);
        Assert.Null(updates.LastCheckUtc);
        Assert.Equal("", updates.LatestVersion);
    }

    [Fact]
    public void A_null_section_does_not_break_the_load()
    {
        using var folders = new TestFolders();
        var path = Path.Combine(folders.New(), "settings.json");
        File.WriteAllText(path, """{"Updates": null, "Hotkey": "Alt+W"}""");

        var loaded = new SettingsStore(path).Load();
        Assert.Equal("Alt+W", loaded.Hotkey);
        Assert.NotNull(loaded.Updates);
        Assert.True(loaded.Updates.CheckForUpdates);
    }

    [Fact]
    public void The_section_survives_a_save_and_a_load()
    {
        using var folders = new TestFolders();
        var store = new SettingsStore(Path.Combine(folders.New(), "settings.json"));
        var settings = new AppSettings();
        settings.Updates.CheckForUpdates = false;
        settings.Updates.LastCheckUtc = new DateTime(2026, 10, 1, 17, 5, 0, DateTimeKind.Utc);
        settings.Updates.NotifiedVersion = "0.1.0";
        settings.Updates.LatestVersion = "0.1.0";
        settings.Updates.LatestUrl = UpdateCheck.ReleasesPage + "/tag/v0.1.0";
        store.Save(settings);

        var back = store.Load().Updates;
        Assert.False(back.CheckForUpdates);
        Assert.Equal(settings.Updates.LastCheckUtc, back.LastCheckUtc);
        Assert.Equal(DateTimeKind.Utc, back.LastCheckUtc!.Value.Kind);
        Assert.Equal("0.1.0", back.NotifiedVersion);
        Assert.Equal("0.1.0", back.LatestVersion);
        Assert.Equal(UpdateCheck.ReleasesPage + "/tag/v0.1.0", back.LatestUrl);
    }

    [Fact]
    public void The_window_tells_about_a_newer_version_once_and_again_for_the_next_one()
    {
        var updates = new UpdateSettings { LatestVersion = "0.1.0", LatestUrl = UpdateCheck.ReleasesPage + "/tag/v0.1.0" };

        Assert.Equal("0.1.0", UpdateCheck.Unseen(updates, V003)!.Version);
        updates.SeenVersion = "0.1.0";
        Assert.Null(UpdateCheck.Unseen(updates, V003));
        updates.LatestVersion = "0.1.1";
        Assert.Equal("0.1.1", UpdateCheck.Unseen(updates, V003)!.Version);
        Assert.Null(UpdateCheck.Unseen(updates, AppVersion.Parse("0.1.1")!.Value)); // updated already
    }
}
