using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitBackup.Configuration.Models;
using GitBackup.Runtime;
using GitBackup.Services.Paths;

namespace GitBackup.Services.Providers;

public abstract class ProviderHttpClientBase
{
    private const string ProductName = "GitBackup";

    // A single shared handler pools TCP/TLS connections across every provider client and every
    // per-call HttpClient. Previously each `using var client = CreateClient(...)` disposed its own
    // handler, tearing down the socket pool and leaving sockets in TIME_WAIT — which on an
    // attachment-heavy sync risked ephemeral-port exhaustion. The clients stay cheap wrappers
    // (disposeHandler: false) so per-request auth headers remain isolated per client.
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    // Attachment downloads follow provider-supplied URLs and must validate every redirect hop
    // themselves (see AttachmentDownloader), so this handler never auto-follows redirects.
    private static readonly SocketsHttpHandler SharedNoRedirectHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false
    };

    // The product/version is constant for the process lifetime, so build the header once instead of on
    // every CreateClient call. ProductInfoHeaderValue is immutable and safe to share across clients.
    private static readonly ProductInfoHeaderValue UserAgent = CreateUserAgent();

    // Builds a bare client (user-agent + Accept) over the given handler; the provider's authentication
    // is layered on separately by ApplyAuthentication.
    private static HttpClient CreateClient(SocketsHttpHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false);
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>
    /// Applies the provider's authentication scheme to a client's default headers. The auth scheme is
    /// the only per-provider difference (GitHub/GitLab a Bearer token, Forgejo a <c>token</c> header),
    /// so it is the single abstract seam over the shared client setup.
    /// </summary>
    protected abstract void ApplyAuthentication(HttpClient client, CredentialConfig credential);

    /// <summary>Creates a connection-pooling, redirect-following client carrying the provider's auth.</summary>
    protected HttpClient CreateAuthenticatedClient(CredentialConfig credential)
    {
        var client = CreateClient(SharedHandler);
        ApplyAuthentication(client, credential);
        return client;
    }

    /// <summary>
    /// True when a credential carries a usable API key. The early-out guard every provider list method
    /// uses so a blank key never reaches an authenticated request.
    /// </summary>
    protected static bool HasApiKey(CredentialConfig credential)
    {
        return !string.IsNullOrWhiteSpace(credential.ApiKey);
    }

    // Creates an auth-carrying client that never auto-redirects, so attachment downloads can validate
    // each redirect hop before following it.
    private HttpClient CreateAuthenticatedAttachmentClient(CredentialConfig credential)
    {
        var client = CreateClient(SharedNoRedirectHandler);
        ApplyAuthentication(client, credential);
        return client;
    }

    /// <summary>
    /// Opens an issue/MR/release attachment as a stream for direct upload to storage. The returned
    /// stream owns the authenticated client and disposes it once the caller finishes, so the attachment
    /// is never buffered fully in memory. Identical across providers once the auth scheme is supplied by
    /// <see cref="ApplyAuthentication"/>.
    /// </summary>
    public async Task<Stream> OpenAttachmentAsync(
        ProjectMetadataContext context,
        CredentialConfig credential,
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        var client = CreateAuthenticatedAttachmentClient(credential);
        try
        {
            return await AttachmentDownloader.OpenStreamAsync(
                client,
                downloadUrl,
                ResolveInstanceHost(context),
                cancellationToken);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The host of the forge this repository came from, or an empty string when it cannot be determined.
    /// </summary>
    protected static string ResolveInstanceHost(ProjectMetadataContext context)
    {
        var reference = context.WebUrl ?? context.CloneUrl;
        return Uri.TryCreate(reference, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
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

            var delay = RetryDelay.Resolve(
                response.Headers.RetryAfter,
                attempt,
                TimeSpan.FromSeconds(60),
                capRetryAfterToMax: true,
                jitter: false);
            response.Dispose();

            AppLogger.Debug(
                "Retrying provider request. status={StatusCode}, attempt={Attempt}, delaySeconds={DelaySeconds}.",
                status,
                attempt,
                Math.Round(delay.TotalSeconds, 1));

            await Task.Delay(delay, cancellationToken);
        }
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

    /// <summary>
    /// Resolves a provider API base URL from an optional configured value, a provider default, and the
    /// provider's API path suffix (e.g. <c>/api/v4</c>).
    /// </summary>
    protected static string ComposeApiBaseUrl(string? configuredBaseUrl, string defaultBaseUrl, string apiSuffix)
    {
        return EnsureApiSuffix(ResolveBaseUrl(configuredBaseUrl, defaultBaseUrl), apiSuffix);
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

        var segments = GitRepositoryUrl.SplitUnescapedSegments(uri);

        if (segments.Length < 2)
        {
            throw new InvalidOperationException($"Repository URL '{cloneUrl}' does not contain owner and repository segments.");
        }

        var repository = GitRepositoryUrl.TrimGitSuffix(segments[^1]);
        return (segments[0], repository);
    }

    /// <summary>
    /// Builds the URL-escaped <c>{owner}/{repo}</c> path segment for providers that address a project
    /// as <c>/repos/{owner}/{repo}</c>.
    /// </summary>
    protected static string BuildOwnerRepoPath(string cloneUrl)
    {
        var (owner, repository) = ResolveOwnerAndRepository(cloneUrl);
        return $"{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}";
    }

    /// <summary>
    /// Removes items that share a key, keeping the first occurrence (ordinal comparison). Centralizes
    /// the first-wins dedupe idiom used across providers.
    /// </summary>
    protected static List<T> DistinctByKey<T>(IEnumerable<T> items, Func<T, string> keySelector)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<T>();
        foreach (var item in items)
        {
            if (seen.Add(keySelector(item)))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>
    /// Removes repositories that share a clone URL, keeping first occurrence. Used when owned and
    /// starred results are merged, since a starred repository can also be an owned one.
    /// </summary>
    protected static IReadOnlyList<DiscoveredRepository> DistinctByCloneUrl(
        IEnumerable<DiscoveredRepository> repositories)
    {
        return DistinctByKey(repositories, repository => repository.CloneUrl);
    }

    /// <summary>
    /// Decides whether a paginated endpoint has another page, given the response and the number of
    /// raw items on the page just read.
    /// </summary>
    protected delegate bool NextPageStrategy(HttpResponseMessage response, int page, int itemCount);

    /// <summary>
    /// Walks a paginated JSON-array endpoint, mapping each element and collecting the non-null
    /// results. <paramref name="hasNextPage"/> encapsulates the provider's paging signal (a full page
    /// for GitHub/Forgejo, an <c>X-Next-Page</c> header for GitLab). <paramref name="mapItem"/> may
    /// carry a side effect (e.g. collecting per-comment attachments) and returns null to skip.
    /// </summary>
    /// <summary>
    /// Reads and maps a single page of a JSON-array endpoint, returning the mapped items and whether a
    /// further page follows (per the provider's <paramref name="hasNextPage"/> signal).
    /// </summary>
    private static async Task<(List<T> Items, bool HasNextPage)> FetchPageAsync<T>(
        HttpClient client,
        string requestUri,
        int page,
        Func<JsonElement, T?> mapItem,
        NextPageStrategy hasNextPage,
        CancellationToken cancellationToken)
        where T : class
    {
        using var response = await GetWithRetryAsync(client, requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = await ReadJsonDocumentAsync(response, cancellationToken);
        var items = new List<T>();
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return (items, false);
        }

        var itemCount = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            itemCount++;
            var mapped = mapItem(item);
            if (mapped is not null)
            {
                items.Add(mapped);
            }
        }

        return (items, hasNextPage(response, page, itemCount));
    }

    protected static async Task<List<T>> CollectAsync<T>(
        HttpClient client,
        Func<int, string> buildRequestUri,
        Func<JsonElement, T?> mapItem,
        NextPageStrategy hasNextPage,
        CancellationToken cancellationToken)
        where T : class
    {
        var items = new List<T>();

        for (var page = 1; ; page++)
        {
            var (pageItems, hasNext) = await FetchPageAsync(client, buildRequestUri(page), page, mapItem, hasNextPage, cancellationToken);
            items.AddRange(pageItems);

            if (!hasNext)
            {
                break;
            }
        }

        return items;
    }

    /// <summary>
    /// Streams a paginated JSON-array endpoint page by page, yielding mapped items in order without
    /// ever holding the whole result set in memory.
    /// </summary>
    protected static async IAsyncEnumerable<T> CollectStreamAsync<T>(
        HttpClient client,
        Func<int, string> buildRequestUri,
        Func<JsonElement, T?> mapItem,
        NextPageStrategy hasNextPage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where T : class
    {
        for (var page = 1; ; page++)
        {
            var (items, hasNext) = await FetchPageAsync(client, buildRequestUri(page), page, mapItem, hasNextPage, cancellationToken);
            foreach (var item in items)
            {
                yield return item;
            }

            if (!hasNext)
            {
                break;
            }
        }
    }

    /// <summary>
    /// A <see cref="NextPageStrategy"/> for offset-paginated APIs that signal "more" by returning a
    /// full page of <paramref name="pageSize"/> items.
    /// </summary>
    protected static NextPageStrategy PageIsFull(int pageSize)
    {
        return (_, _, itemCount) => itemCount >= pageSize;
    }

    /// <summary>
    /// Lists a paginated collection, then populates each item (its comments and attachments) in
    /// parallel up to <paramref name="concurrency"/>. The list/map/paging and the parallel fan-out are
    /// shared here; <paramref name="populateItem"/> carries the only per-provider work — which comment
    /// endpoint to read and how attachments are gathered.
    /// </summary>
    protected static async IAsyncEnumerable<TItem> CollectWithCommentsAsync<TItem>(
        HttpClient client,
        int concurrency,
        Func<int, string> buildListUri,
        Func<JsonElement, TItem?> mapItem,
        NextPageStrategy hasNextPage,
        Func<TItem, CancellationToken, Task> populateItem,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TItem : class
    {
        // Stream one page at a time: fetch a page, populate its items (comments/attachments) in parallel
        // up to the configured degree, then yield them in order. Peak memory is one page rather than the
        // whole collection, while the per-page fan-out preserves the fetch parallelism.
        for (var page = 1; ; page++)
        {
            var (items, hasNext) = await FetchPageAsync(client, buildListUri(page), page, mapItem, hasNextPage, cancellationToken);

            await Parallel.ForEachAsync(
                items,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = cancellationToken },
                async (item, token) => await populateItem(item, token));

            foreach (var item in items)
            {
                yield return item;
            }

            if (!hasNext)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Scans an item body and its comment bodies for attachment references matching <paramref
    /// name="pattern"/>, builds each match via <paramref name="build"/>, and dedupes by
    /// <see cref="BackedUpAttachment.OriginalPath"/> (first wins). Shared by providers that embed
    /// attachments inline in markdown (GitHub, GitLab).
    /// </summary>
    protected static IReadOnlyList<BackedUpAttachment> ScanBodyAndComments(
        string? body,
        IEnumerable<BackedUpComment> comments,
        Regex pattern,
        Func<Match, BackedUpAttachment?> build)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var attachments = new List<BackedUpAttachment>();

        void Scan(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (Match match in pattern.Matches(text))
            {
                // build returns null for a match the provider decides not to trust (e.g. an upload
                // reference carrying a path separator); skip those rather than record them.
                var attachment = build(match);
                if (attachment is not null && seen.Add(attachment.OriginalPath))
                {
                    attachments.Add(attachment);
                }
            }
        }

        Scan(body);
        foreach (var comment in comments)
        {
            Scan(comment.Body);
        }

        return attachments;
    }

    // --- Shared Gitea-lineage (GitHub + Forgejo) JSON mappers. GitLab uses its own field names. ---

    protected static DiscoveredRepository? MapGiteaRepository(JsonElement item, bool isStarred)
    {
        var cloneUrl = GetStringOrNull(item, "clone_url");
        if (string.IsNullOrWhiteSpace(cloneUrl))
        {
            return null;
        }

        return new DiscoveredRepository
        {
            CloneUrl = cloneUrl,
            WebUrl = GetStringOrNull(item, "html_url"),
            IsStarred = isStarred
        };
    }

    /// <summary>
    /// Maps a comment, reading the author from <paramref name="authorObject"/>.<paramref
    /// name="authorProperty"/>. Providers differ only in that path (GitHub/Forgejo <c>user.login</c>,
    /// GitLab <c>author.username</c>) and whether a <c>system</c> flag distinguishes generated notes.
    /// </summary>
    protected static BackedUpComment? MapComment(
        JsonElement item,
        string authorObject,
        string authorProperty,
        bool readSystemFlag)
    {
        var body = GetStringOrNull(item, "body");
        var author = GetNestedStringOrNull(item, authorObject, authorProperty);
        if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(author))
        {
            return null;
        }

        return new BackedUpComment
        {
            Id = GetInt64OrNull(item, "id"),
            Author = author,
            Body = body,
            CreatedAt = GetDateTimeOffsetOrNull(item, "created_at"),
            UpdatedAt = GetDateTimeOffsetOrNull(item, "updated_at"),
            System = readSystemFlag && GetBoolean(item, "system")
        };
    }

    /// <summary>Maps a Gitea-lineage comment (GitHub, Forgejo): author at <c>user.login</c>.</summary>
    protected static BackedUpComment? MapGiteaComment(JsonElement item)
    {
        return MapComment(item, "user", "login", readSystemFlag: false);
    }

    /// <summary>
    /// Maps the shared issue fields. Attachments are populated by the caller — GitHub scans the body,
    /// Forgejo reads the assets array — so this leaves them empty.
    /// </summary>
    protected static BackedUpIssue? MapGiteaIssue(JsonElement item)
    {
        var number = GetInt64OrNull(item, "number");
        var title = GetStringOrNull(item, "title");
        if (number is null || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return new BackedUpIssue
        {
            Number = number.Value,
            Title = title,
            State = GetStringOrNull(item, "state"),
            Author = GetNestedStringOrNull(item, "user", "login"),
            Body = GetStringOrNull(item, "body"),
            CreatedAt = GetDateTimeOffsetOrNull(item, "created_at"),
            UpdatedAt = GetDateTimeOffsetOrNull(item, "updated_at"),
            ClosedAt = GetDateTimeOffsetOrNull(item, "closed_at"),
            Labels = GetLabelNames(item, "labels"),
            WebUrl = GetStringOrNull(item, "html_url")
        };
    }

    /// <summary>Maps the shared pull-request fields; attachments are populated by the caller.</summary>
    protected static BackedUpMergeRequest? MapGiteaPullRequest(JsonElement item)
    {
        var number = GetInt64OrNull(item, "number");
        var title = GetStringOrNull(item, "title");
        if (number is null || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return new BackedUpMergeRequest
        {
            Number = number.Value,
            Title = title,
            State = GetStringOrNull(item, "state"),
            Author = GetNestedStringOrNull(item, "user", "login"),
            Body = GetStringOrNull(item, "body"),
            SourceBranch = GetNestedStringOrNull(item, "head", "ref"),
            TargetBranch = GetNestedStringOrNull(item, "base", "ref"),
            CreatedAt = GetDateTimeOffsetOrNull(item, "created_at"),
            UpdatedAt = GetDateTimeOffsetOrNull(item, "updated_at"),
            MergedAt = GetDateTimeOffsetOrNull(item, "merged_at"),
            ClosedAt = GetDateTimeOffsetOrNull(item, "closed_at"),
            Labels = GetLabelNames(item, "labels"),
            WebUrl = GetStringOrNull(item, "html_url")
        };
    }

    /// <summary>
    /// Maps a release, including its downloadable assets from the <c>assets</c> array.
    /// </summary>
    protected static BackedUpRelease? MapGiteaRelease(JsonElement item)
    {
        var tag = GetStringOrNull(item, "tag_name");
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        return new BackedUpRelease
        {
            Tag = tag,
            Name = GetStringOrNull(item, "name"),
            Body = GetStringOrNull(item, "body"),
            Author = GetNestedStringOrNull(item, "author", "login"),
            Draft = GetBoolean(item, "draft"),
            Prerelease = GetBoolean(item, "prerelease"),
            CreatedAt = GetDateTimeOffsetOrNull(item, "created_at"),
            PublishedAt = GetDateTimeOffsetOrNull(item, "published_at"),
            WebUrl = GetStringOrNull(item, "html_url"),
            Attachments = ExtractAssetArray(item)
        };
    }

    /// <summary>
    /// Extracts downloadable assets from a Gitea-lineage <c>assets</c> array, deduping by download URL
    /// so a release (or issue/MR) never yields the same file twice.
    /// </summary>
    protected static IReadOnlyList<BackedUpAttachment> ExtractAssetArray(JsonElement item)
    {
        var attachments = new List<BackedUpAttachment>();
        if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return attachments;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in assets.EnumerateArray())
        {
            var url = GetStringOrNull(asset, "browser_download_url");
            var name = GetStringOrNull(asset, "name");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(name) || !seen.Add(url))
            {
                continue;
            }

            attachments.Add(new BackedUpAttachment
            {
                FileName = AttachmentDownloader.BuildStorageFileName(url, name),
                OriginalPath = url,
                DownloadUrl = url,
                SizeBytes = GetInt64OrNull(asset, "size")
            });
        }

        return attachments;
    }
}
