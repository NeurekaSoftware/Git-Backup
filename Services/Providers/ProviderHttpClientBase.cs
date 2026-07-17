using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using GitBackup.Runtime;
using GitBackup.Services.Paths;

namespace GitBackup.Services.Providers;

public abstract class ProviderHttpClientBase
{
    private const string ProductName = "GitBackup";

    protected static HttpClient CreateClient(string? token)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(CreateUserAgent());
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        return client;
    }

    private static ProductInfoHeaderValue CreateUserAgent()
    {
        try
        {
            return new ProductInfoHeaderValue(ProductName, BuildMetadata.Version);
        }
        catch (FormatException)
        {
            // BuildMetadata.Version comes from the GIT_TAG build argument, which is not
            // guaranteed to be a valid HTTP token: git tags may contain '/' and arbitrary
            // build arguments may contain spaces. Report the product without a version
            // rather than failing every provider request.
            return new ProductInfoHeaderValue(ProductName, string.Empty);
        }
    }

    protected static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Issues a GET, retrying on rate-limit (429) and transient server errors (5xx). Rate limits
    /// honor the <c>Retry-After</c> header when present, otherwise a capped exponential backoff is
    /// used. The returned response is the caller's to dispose; intermediate retried responses are
    /// disposed here. The caller still validates the final status (e.g. EnsureSuccessStatusCode).
    /// </summary>
    protected static async Task<HttpResponseMessage> GetWithRetryAsync(
        HttpClient client,
        string requestUri,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            var response = await client.GetAsync(requestUri, cancellationToken);

            var status = (int)response.StatusCode;
            var isRetryable = status == 429 || status >= 500;
            if (!isRetryable || attempt >= maxAttempts)
            {
                return response;
            }

            var delay = ResolveRetryDelay(response, attempt);
            response.Dispose();

            AppLogger.Debug(
                "Retrying provider request. status={StatusCode}, attempt={Attempt}, delaySeconds={DelaySeconds}.",
                status,
                attempt,
                Math.Round(delay.TotalSeconds, 1));

            await Task.Delay(delay, cancellationToken);
        }
    }

    private static TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt)
    {
        var maxDelay = TimeSpan.FromSeconds(60);

        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta < maxDelay ? delta : maxDelay;
        }

        if (retryAfter?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero)
            {
                return until < maxDelay ? until : maxDelay;
            }
        }

        var backoffSeconds = Math.Min(maxDelay.TotalSeconds, Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(backoffSeconds);
    }

    protected static string ResolveBaseUrl(string? configuredBaseUrl, string defaultBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return defaultBaseUrl.TrimEnd('/');
        }

        return configuredBaseUrl.Trim().TrimEnd('/');
    }

    protected static string EnsureApiSuffix(string baseUrl, string apiSuffix)
    {
        if (baseUrl.EndsWith(apiSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return baseUrl;
        }

        return $"{baseUrl}{apiSuffix}";
    }

    protected static string? GetStringOrNull(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    protected static long? GetInt64OrNull(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    protected static bool GetBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
    }

    protected static DateTimeOffset? GetDateTimeOffsetOrNull(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Reads a nested string, e.g. <c>author.username</c>. Returns null when either the object or
    /// the string property is missing.
    /// </summary>
    protected static string? GetNestedStringOrNull(JsonElement element, string objectProperty, string stringProperty)
    {
        if (!element.TryGetProperty(objectProperty, out var nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetStringOrNull(nested, stringProperty);
    }

    /// <summary>
    /// Reads a label array. Handles both a plain array of strings (GitLab) and an array of objects
    /// with a <c>name</c> property (GitHub, Forgejo).
    /// </summary>
    protected static IReadOnlyList<string> GetLabelNames(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var labels = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var name = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object => GetStringOrNull(item, "name"),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(name))
            {
                labels.Add(name);
            }
        }

        return labels;
    }

    /// <summary>
    /// Extracts the owner and repository name from a clone URL (last path segment as the repository,
    /// with any <c>.git</c> suffix removed). Used by providers whose metadata endpoints address a
    /// project as <c>/repos/{owner}/{repo}</c>. Segments are returned unescaped; callers escape them
    /// when building request URIs.
    /// </summary>
    protected static (string Owner, string Repository) ResolveOwnerAndRepository(string cloneUrl)
    {
        if (!Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid repository URL '{cloneUrl}'.");
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        if (segments.Length < 2)
        {
            throw new InvalidOperationException($"Repository URL '{cloneUrl}' does not contain owner and repository segments.");
        }

        var repository = GitRepositoryUrl.TrimGitSuffix(segments[^1]);
        return (segments[0], repository);
    }

    /// <summary>
    /// Removes repositories that share a clone URL, keeping first occurrence. Used when owned and
    /// starred results are merged, since a starred repository can also be an owned one.
    /// </summary>
    protected static IReadOnlyList<DiscoveredRepository> DistinctByCloneUrl(
        IEnumerable<DiscoveredRepository> repositories)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DiscoveredRepository>();
        foreach (var repository in repositories)
        {
            if (seen.Add(repository.CloneUrl))
            {
                result.Add(repository);
            }
        }

        return result;
    }
}
