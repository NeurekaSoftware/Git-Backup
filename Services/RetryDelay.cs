using System.Net.Http.Headers;

namespace GitBackup.Services;

/// <summary>
/// Shared computation of an HTTP retry delay from a <c>Retry-After</c> header, falling back to a
/// capped exponential backoff. Parameterized so the provider clients and the S3 no-silent-success
/// handler can keep their respective policies (header capping, jitter) without duplicating the logic.
/// </summary>
internal static class RetryDelay
{
    /// <summary>
    /// Resolves the delay before the next retry. When the response carries a <c>Retry-After</c>
    /// header its value is honored (clamped to <paramref name="maxDelay"/> only when
    /// <paramref name="capRetryAfterToMax"/> is set); otherwise a <c>2^(attempt-1)</c> backoff capped
    /// at <paramref name="maxDelay"/> is used, with up to 1s of randomness added when
    /// <paramref name="jitter"/> is set.
    /// </summary>
    public static TimeSpan Resolve(
        RetryConditionHeaderValue? retryAfter,
        int attempt,
        TimeSpan maxDelay,
        bool capRetryAfterToMax,
        bool jitter)
    {
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return capRetryAfterToMax && delta > maxDelay ? maxDelay : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero)
            {
                return capRetryAfterToMax && until > maxDelay ? maxDelay : until;
            }
        }

        var backoffSeconds = Math.Min(maxDelay.TotalSeconds, Math.Pow(2, attempt - 1));
        var backoff = TimeSpan.FromSeconds(backoffSeconds);
        return jitter ? backoff + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)) : backoff;
    }
}
