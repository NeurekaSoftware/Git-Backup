using System.Net;
using GitBackup.Runtime;

namespace GitBackup.Services.Storage;

/// <summary>
/// Genbox.SimpleS3 classifies responses with a blocklist of 14 error status codes; every other
/// non-2xx (429, 401, 408, 413, 502, …) is reported as a <i>successful</i> response, so a failed
/// request can be silently reported as done (e.g. an upload that never stored anything). This
/// handler sits outermost in the HTTP pipeline — so it observes every response, including each
/// multipart part and each list page — and closes that gap: it retries 429 (honoring
/// <c>Retry-After</c>), and rewrites any other library-misclassified non-2xx into a 503 so the
/// client raises <c>S3RequestException</c>. Real 2xx/304 and the codes the client already treats as
/// errors pass through untouched.
/// </summary>
internal sealed class NoSilentSuccessHandler : DelegatingHandler
{
    private const int MaxAttempts = 5;
    private const double MaxBackoffSeconds = 30;

    // Status codes SimpleS3's DefaultResponseHandler already treats as errors (and throws on).
    private static readonly HashSet<int> LibraryErrorCodes =
    [
        301, 307, 400, 403, 404, 405, 409, 411, 412, 416, 500, 501, 503, 504
    ];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer any request body once so a 429 retry can re-send it. HttpClient consumes (and disposes)
        // the request content on each send, and upload bodies — multipart parts and PUT streams — are
        // single-use, so re-sending the original request after a 429 would fail on a spent body. We send
        // a fresh clone with a re-readable body each attempt and never reuse the original request.
        byte[]? bufferedBody = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            using var attemptRequest = CloneRequest(request, bufferedBody);
            var response = await base.SendAsync(attemptRequest, cancellationToken);
            var statusCode = (int)response.StatusCode;

            if (statusCode == 429 && attempt < MaxAttempts)
            {
                var delay = RetryDelay.Resolve(
                    response.Headers.RetryAfter,
                    attempt,
                    TimeSpan.FromSeconds(MaxBackoffSeconds),
                    capRetryAfterToMax: false,
                    jitter: true);
                AppLogger.Warn(
                    "Storage request was rate limited (429). Backing off before retry. method={Method}, uri={Uri}, attempt={Attempt}, delaySeconds={DelaySeconds}.",
                    request.Method.Method,
                    request.RequestUri,
                    attempt,
                    Math.Round(delay.TotalSeconds, 1));
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            if (IsRealSuccess(statusCode) || LibraryErrorCodes.Contains(statusCode))
            {
                return response;
            }

            // A non-2xx the client would otherwise report as success. Rewrite it to a status the
            // client treats as an error so it raises S3RequestException instead of succeeding
            // silently. The original <Error> body is preserved for the error detail.
            AppLogger.Warn(
                "Storage returned HTTP {StatusCode}, which the S3 client would misreport as success. Failing the operation. method={Method}, uri={Uri}.",
                statusCode,
                request.Method.Method,
                request.RequestUri);
            response.Headers.TryAddWithoutValidation("X-Original-StatusCode", statusCode.ToString());
            response.StatusCode = HttpStatusCode.ServiceUnavailable;
            return response;
        }
    }

    private static bool IsRealSuccess(int statusCode)
    {
        return statusCode is (>= 200 and <= 299) or 304;
    }

    // Builds a fresh, independently-sendable copy of the request for one attempt: same method, URI,
    // version, headers, and options, with a re-readable body rebuilt from the buffered bytes (and the
    // original content headers, e.g. Content-Type / Content-MD5, preserved for the S3 signature).
    private static HttpRequestMessage CloneRequest(HttpRequestMessage request, byte[]? bufferedBody)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        if (bufferedBody is not null)
        {
            var content = new ByteArrayContent(bufferedBody);
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            clone.Content = content;
        }

        return clone;
    }
}
