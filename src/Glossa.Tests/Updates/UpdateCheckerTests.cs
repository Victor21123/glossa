using System.Net;
using System.Text;
using Glossa.Core.Updates;

namespace Glossa.Tests.Updates;

public class UpdateCheckerTests
{
    private static readonly AppVersion V003 = AppVersion.Parse("0.0.3")!.Value;

    private static string Json(string tag, bool draft = false, bool prerelease = false) =>
        $$"""{"tag_name": "{{tag}}", "name": "Glossa {{tag}}", "html_url": "https://github.com/Victor21123/glossa/releases/tag/{{tag}}", "draft": {{draft.ToString().ToLowerInvariant()}}, "prerelease": {{prerelease.ToString().ToLowerInvariant()}}, "published_at": "2026-10-01T17:00:39Z"}""";

    /// <summary>One site that answers as GitHub does, or fails like a blocked route; remembers what it was asked.</summary>
    private sealed class Site(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request);
            return answer(request, ct);
        }

        public static Site Ok(string body) => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

        public static Site Status(HttpStatusCode code) => new((_, _) => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("{}") }));

        public static Site Down() => new((_, _) => throw new HttpRequestException("unreachable"));

        /// <summary>Never answers until cancelled, like a connection that hangs.</summary>
        public static Site Hangs() => new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
    }

    private static UpdateChecker Checker(Site direct, Site proxied, TimeSpan? timeout = null, int maxBytes = 512 * 1024) =>
        new(new HttpClient(proxied), new HttpClient(direct), timeout, maxBytes);

    [Fact]
    public async Task Asks_the_latest_release_of_the_repository_and_says_who_is_asking()
    {
        var direct = Site.Ok(Json("v0.0.3"));
        var outcome = await Checker(direct, Site.Down()).CheckAsync(V003, CancellationToken.None);

        var request = Assert.Single(direct.Asked);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/repos/Victor21123/glossa/releases/latest", request.RequestUri!.ToString());
        Assert.Equal("Glossa/0.0.3", request.Headers.UserAgent.ToString());
        Assert.Contains(request.Headers.Accept, h => h.MediaType == "application/vnd.github+json");
        Assert.Equal(UpdateStatus.UpToDate, outcome.Status);
    }

    [Fact]
    public async Task The_user_agent_carries_the_running_version_without_the_commit()
    {
        var direct = Site.Ok(Json("v0.0.3"));
        await Checker(direct, Site.Down()).CheckAsync(AppVersion.Parse("0.2.0+abcdef")!.Value, CancellationToken.None);
        Assert.Equal("Glossa/0.2.0", direct.Asked[0].Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task A_newer_release_is_available()
    {
        var outcome = await Checker(Site.Ok(Json("v0.1.0")), Site.Down()).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateStatus.Available, outcome.Status);
        Assert.Equal("v0.1.0", outcome.Release!.Tag);
        Assert.Equal(UpdateFailure.None, outcome.Failure);
    }

    [Fact]
    public async Task The_same_or_an_older_release_is_up_to_date()
    {
        Assert.Equal(UpdateStatus.UpToDate, (await Checker(Site.Ok(Json("v0.0.3")), Site.Down()).CheckAsync(V003, CancellationToken.None)).Status);
        var older = await Checker(Site.Ok(Json("v0.0.2")), Site.Down()).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateStatus.UpToDate, older.Status);
        Assert.Equal("v0.0.2", older.Release!.Tag);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_draft_or_a_prerelease_is_not_an_update(bool draft, bool prerelease)
    {
        var outcome = await Checker(Site.Ok(Json("v0.9.0", draft, prerelease)), Site.Down()).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateStatus.UpToDate, outcome.Status);
    }

    [Fact]
    public async Task Goes_through_the_system_proxy_first_as_the_settings_say()
    {
        var direct = Site.Ok(Json("v0.1.0"));
        var proxied = Site.Ok(Json("v0.1.0"));
        await Checker(direct, proxied).CheckAsync(V003, CancellationToken.None);
        Assert.Single(proxied.Asked);
        Assert.Empty(direct.Asked);
    }

    [Fact]
    public async Task Goes_direct_when_the_proxy_is_blocked_and_then_starts_there()
    {
        var proxied = Site.Down();
        var direct = Site.Ok(Json("v0.1.0"));
        var checker = Checker(direct, proxied);

        var first = await checker.CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateStatus.Available, first.Status);
        Assert.Single(proxied.Asked);
        Assert.Single(direct.Asked);

        await checker.CheckAsync(V003, CancellationToken.None);
        Assert.Single(proxied.Asked); // the way that worked goes first now
        Assert.Equal(2, direct.Asked.Count);
    }

    [Theory]
    [InlineData(HttpRequestError.ProxyTunnelError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    public async Task A_proxy_tunnel_connection_or_tls_error_tries_the_other_way(HttpRequestError error)
    {
        var proxied = new Site((_, _) => throw new HttpRequestException(error, "tunnel", null, HttpStatusCode.BadGateway));
        var direct = Site.Ok(Json("v0.1.0"));
        var outcome = await Checker(direct, proxied).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateStatus.Available, outcome.Status);
        Assert.Single(direct.Asked);
    }

    [Theory]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task An_exception_that_carries_a_proxy_status_tries_the_other_way(HttpStatusCode status)
    {
        var proxied = new Site((_, _) => throw new HttpRequestException(HttpRequestError.Unknown, "proxy", null, status));
        var direct = Site.Ok(Json("v0.1.0"));
        Assert.Equal(UpdateStatus.Available, (await Checker(direct, proxied).CheckAsync(V003, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_proxy_that_answers_407_is_a_blocked_way_not_githubs_answer()
    {
        var proxied = Site.Status(HttpStatusCode.ProxyAuthenticationRequired);
        var direct = Site.Ok(Json("v0.1.0"));
        var outcome = await Checker(direct, proxied).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateStatus.Available, outcome.Status);
        Assert.Single(direct.Asked);
    }

    [Fact]
    public async Task Another_exception_never_escapes_it_is_a_bad_answer()
    {
        var odd = new Site((_, _) => throw new InvalidOperationException("surprise"));
        var outcome = await Checker(odd, odd).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateStatus.Failed, outcome.Status);
        Assert.Equal(UpdateFailure.BadAnswer, outcome.Failure);
        Assert.Contains("InvalidOperationException", outcome.Detail);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_it_is_a_bad_answer()
    {
        var moved = new Site((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MovedPermanently)));
        var outcome = await Checker(moved, Site.Down()).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateFailure.BadAnswer, outcome.Failure);
    }

    [Fact]
    public void The_clients_made_for_the_check_take_no_cookies_redirects_or_compression()
    {
        foreach (var useProxy in new[] { true, false })
        {
            using var client = UpdateChecker.CreateClient(useProxy);
            var handler = typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(client) as SocketsHttpHandler;
            Assert.NotNull(handler);
            Assert.False(handler!.UseCookies);
            Assert.False(handler.AllowAutoRedirect);
            Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
            Assert.Equal(useProxy, handler.UseProxy);
        }
    }

    [Fact]
    public async Task Retry_after_in_seconds_is_passed_on()
    {
        var limited = new Site((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        var outcome = await Checker(limited, Site.Down()).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateFailure.Refused, outcome.Failure);
        Assert.Equal(TimeSpan.FromSeconds(120), outcome.RetryAfter);
    }

    [Fact]
    public async Task The_rate_limit_reset_time_is_turned_into_a_wait()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var limited = new Site((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Reset", now.AddMinutes(25).ToUnixTimeSeconds().ToString());
            return Task.FromResult(response);
        });
        var checker = new UpdateChecker(new HttpClient(limited), new HttpClient(Site.Down()), null, 512 * 1024, new FixedClock(now));
        var outcome = await checker.CheckAsync(V003, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(25), outcome.RetryAfter);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("99999999999999999999")]
    [InlineData("9999999999999")]
    public async Task A_reset_time_in_the_past_or_nonsense_is_ignored(string reset)
    {
        var limited = new Site((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Reset", reset);
            return Task.FromResult(response);
        });
        var outcome = await Checker(limited, Site.Down()).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateFailure.Refused, outcome.Failure);
        Assert.Null(outcome.RetryAfter);
    }

    [Fact]
    public async Task Both_ways_blocked_means_no_network()
    {
        var outcome = await Checker(Site.Down(), Site.Down()).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateStatus.Failed, outcome.Status);
        Assert.Equal(UpdateFailure.Offline, outcome.Failure);
        Assert.Null(outcome.Release);
    }

    [Fact]
    public async Task The_log_detail_says_what_each_way_answered_and_stays_out_of_the_user_text()
    {
        var outcome = await Checker(Site.Down(), Site.Hangs(), TimeSpan.FromMilliseconds(100)).CheckAsync(V003, CancellationToken.None);
        Assert.Contains("proxy: timed out", outcome.Detail);
        Assert.Contains("direct: HttpRequestException", outcome.Detail);
        Assert.DoesNotContain("HttpRequestException", UpdateTexts.For(outcome));

        var refused = await Checker(Site.Status(HttpStatusCode.Forbidden), Site.Down()).CheckAsync(V003, CancellationToken.None);
        Assert.Equal("HTTP 403", refused.Detail);
    }

    [Fact]
    public async Task A_hanging_connection_is_cut_by_the_timeout_and_the_other_way_gets_its_turn()
    {
        var proxied = Site.Hangs();
        var direct = Site.Ok(Json("v0.1.0"));
        var started = DateTime.UtcNow;
        var outcome = await Checker(direct, proxied, TimeSpan.FromMilliseconds(150)).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateStatus.Available, outcome.Status);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Hanging_on_both_ways_is_no_network_after_two_timeouts()
    {
        var outcome = await Checker(Site.Hangs(), Site.Hangs(), TimeSpan.FromMilliseconds(100)).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateFailure.Offline, outcome.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, UpdateFailure.Refused)]
    [InlineData(HttpStatusCode.TooManyRequests, UpdateFailure.Refused)]
    [InlineData(HttpStatusCode.NotFound, UpdateFailure.NoRelease)]
    [InlineData(HttpStatusCode.InternalServerError, UpdateFailure.BadAnswer)]
    [InlineData(HttpStatusCode.BadGateway, UpdateFailure.BadAnswer)]
    public async Task GitHubs_own_refusal_is_reported_and_the_other_way_is_not_tried(HttpStatusCode code, UpdateFailure expected)
    {
        var proxied = Site.Status(code);
        var direct = Site.Ok(Json("v0.1.0"));
        var outcome = await Checker(direct, proxied).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateStatus.Failed, outcome.Status);
        Assert.Equal(expected, outcome.Failure);
        Assert.Empty(direct.Asked);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Sign in</html>")]
    [InlineData("{}")]
    [InlineData("""{"message": "Not Found"}""")]
    [InlineData("[]")]
    public async Task An_answer_that_is_not_a_release_is_a_bad_answer(string body)
    {
        var outcome = await Checker(Site.Ok(body), Site.Down()).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateFailure.BadAnswer, outcome.Failure);
    }

    [Fact]
    public async Task A_huge_answer_is_refused_by_its_declared_size()
    {
        var big = new string('x', 5000);
        var outcome = await Checker(Site.Ok($$"""{"tag_name": "v0.1.0", "body": "{{big}}"}"""), Site.Down(), maxBytes: 1000).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateFailure.TooLarge, outcome.Failure);
    }

    [Fact]
    public async Task A_huge_answer_of_unknown_size_is_cut_while_it_is_read()
    {
        // No Content-Length: a chunked stream that never ends must not fill the memory.
        var endless = new Site((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new EndlessContent() }));
        var outcome = await Checker(endless, Site.Down(), maxBytes: 4096).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateFailure.TooLarge, outcome.Failure);
    }

    [Fact]
    public async Task An_answer_within_the_cap_but_with_non_ascii_text_is_read_whole()
    {
        var body = $$"""{"tag_name": "v0.1.0", "body": "{{string.Concat(Enumerable.Repeat("Привет, мир. ", 40))}}"}""";
        var outcome = await Checker(Site.Ok(body), Site.Down(), maxBytes: 4096).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateStatus.Available, outcome.Status);
    }

    [Fact]
    public async Task The_caller_cancelling_is_not_a_failed_check()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Checker(Site.Hangs(), Site.Hangs()).CheckAsync(V003, cts.Token));
    }

    [Fact]
    public async Task The_caller_cancelling_while_waiting_stops_both_ways()
    {
        var direct = Site.Hangs();
        var proxied = Site.Hangs();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Checker(direct, proxied, TimeSpan.FromSeconds(30)).CheckAsync(V003, cts.Token));
        Assert.Empty(direct.Asked);
    }

    [Fact]
    public async Task An_address_in_the_answer_from_elsewhere_is_ignored_the_page_comes_from_the_tag()
    {
        var body = """{"tag_name": "v0.1.0", "html_url": "https://github.com.evil.com/Victor21123/glossa/releases/tag/v0.1.0"}""";
        var outcome = await Checker(Site.Ok(body), Site.Down()).CheckAsync(V003, CancellationToken.None);
        Assert.Equal(UpdateCheck.TagPage("v0.1.0"), outcome.Release!.Url);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A body with no length that keeps producing bytes.</summary>
    private sealed class EndlessContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Endless());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class Endless : Stream
        {
            public override int Read(byte[] buffer, int offset, int count)
            {
                Array.Fill(buffer, (byte)'x', offset, count);
                return count;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
