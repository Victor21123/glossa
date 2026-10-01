using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Glossa.Core.Updates;

/// <summary>
/// One request to GitHub for the latest release, and what it means for the running version. The way is the one the
/// dictionary downloads use (the settings say so): through the system proxy first and directly when there is no answer at
/// all (refused, reset, cut, timed out, a proxy tunnel or TLS error), and once one way failed and the other worked, the
/// other goes first for the rest of the run. A status such as 403 or 404 is GitHub's own answer and ends the check: the
/// other way would hear the same. Each try is cut after <paramref name="timeout"/> (10 s by default) and the answer is
/// read only up to <paramref name="maxBytes"/>. Nothing is sent but the request itself (GET, a User-Agent with the Glossa
/// version, no cookies, no identifiers). <see cref="CheckAsync"/> throws only when the caller cancels.
/// </summary>
public sealed class UpdateChecker(HttpClient proxied, HttpClient direct, TimeSpan? timeout = null, int maxBytes = UpdateChecker.DefaultMaxBytes,
    TimeProvider? time = null)
{
    /// <summary>A release answer is ~10 KB with its notes; GitHub allows notes of 125 000 characters (~250 KB in Russian).</summary>
    public const int DefaultMaxBytes = 512 * 1024;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private volatile bool _proxyFirst = true;

    /// <summary>
    /// A client made for this check alone: no cookies, no redirects, no decompression, no overall timeout (the check cuts each try
    /// itself). <paramref name="useSystemProxy"/> false never uses the proxy.
    /// </summary>
    public static HttpClient CreateClient(bool useSystemProxy) => new(new SocketsHttpHandler
    {
        UseProxy = useSystemProxy,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Asks GitHub and compares with <paramref name="current"/>. Throws only when <paramref name="ct"/> is cancelled.</summary>
    public async Task<UpdateOutcome> CheckAsync(AppVersion current, CancellationToken ct)
    {
        try
        {
            return await CheckCoreAsync(current, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            ct.ThrowIfCancellationRequested();
            return UpdateOutcome.Failed(UpdateFailure.BadAnswer, Describe(e));
        }
    }

    private async Task<UpdateOutcome> CheckCoreAsync(AppVersion current, CancellationToken ct)
    {
        var agent = "Glossa/" + current;
        var proxyFirst = _proxyFirst;
        var (first, second) = proxyFirst ? (proxied, direct) : (direct, proxied);
        Answer answer;
        try
        {
            answer = await AskAsync(first, agent, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsRouteDown(e, ct))
        {
            try
            {
                answer = await AskAsync(second, agent, ct).ConfigureAwait(false);
                _proxyFirst = !proxyFirst;
            }
            catch (Exception e2) when (IsRouteDown(e2, ct))
            {
                return UpdateOutcome.Failed(UpdateFailure.Offline,
                    $"{(proxyFirst ? "proxy" : "direct")}: {Describe(e)}; {(proxyFirst ? "direct" : "proxy")}: {Describe(e2)}");
            }
        }

        var status = $"HTTP {(int)answer.Status}";
        if (answer.TooLarge) return UpdateOutcome.Failed(UpdateFailure.TooLarge, status);
        if (answer.Status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) return UpdateOutcome.Failed(UpdateFailure.Refused, status, answer.RetryAfter);
        if (answer.Status == HttpStatusCode.NotFound) return UpdateOutcome.Failed(UpdateFailure.NoRelease, status, answer.RetryAfter);
        if ((int)answer.Status is < 200 or > 299 || UpdateCheck.ParseRelease(answer.Body) is not { } release)
            return UpdateOutcome.Failed(UpdateFailure.BadAnswer, status, answer.RetryAfter);
        return UpdateCheck.IsNewer(release, current) ? UpdateOutcome.Available(release) : UpdateOutcome.UpToDate(release);
    }

    private static string Describe(Exception e) =>
        e is OperationCanceledException ? "timed out" : $"{e.GetType().Name} {(e as HttpRequestException)?.HttpRequestError} {e.InnerException?.GetType().Name} {e.Message}".Replace("  ", " ").Trim();

    private readonly record struct Answer(HttpStatusCode Status, string? Body, bool TooLarge, TimeSpan? RetryAfter = null);

    private async Task<Answer> AskAsync(HttpClient client, string agent, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, UpdateCheck.LatestApi);
        request.Headers.TryAddWithoutValidation("User-Agent", agent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        // Headers first: the body is read here, up to the cap, instead of being buffered whole by the client.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
            throw new HttpRequestException(HttpRequestError.ProxyTunnelError, "the proxy asked for credentials", null, response.StatusCode);
        if (!response.IsSuccessStatusCode) return new Answer(response.StatusCode, null, false, RetryAfterOf(response));
        if (response.Content.Headers.ContentLength > maxBytes) return new Answer(response.StatusCode, null, true);

        await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > maxBytes) return new Answer(response.StatusCode, null, true);
            body.Write(buffer, 0, read);
        }
        return new Answer(response.StatusCode, Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length), false);
    }

    /// <summary>When GitHub asks to come back: Retry-After (seconds or a date), else X-RateLimit-Reset (epoch seconds); null when absent or not in the future.</summary>
    private TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        TimeSpan? wait = null;
        if (response.Headers.RetryAfter is { } retry) wait = retry.Delta ?? (retry.Date is { } date ? date - now : null);
        else if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
                 && long.TryParse(values.FirstOrDefault(), out var epoch) && epoch is > 0 and < 253_402_300_799)
            wait = DateTimeOffset.FromUnixTimeSeconds(epoch) - now;
        return wait is { } w && w > TimeSpan.Zero ? w : null;
    }

    /// <summary>
    /// No answer at all: refused, reset, cut short, timed out, a proxy tunnel or TLS error, a proxy status (407, 502-504 carried by
    /// the exception). A status code of GitHub's own is an answer; the caller cancelling is not a failure.
    /// </summary>
    private static bool IsRouteDown(Exception e, CancellationToken ct) => e switch
    {
        HttpRequestException h => h.StatusCode is null
            || h.HttpRequestError is HttpRequestError.ProxyTunnelError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError
                or HttpRequestError.NameResolutionError
            || h.StatusCode is HttpStatusCode.ProxyAuthenticationRequired or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout,
        IOException => true,
        OperationCanceledException => !ct.IsCancellationRequested,
        _ => false,
    };
}
