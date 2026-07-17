using System.Text.Json;
using System.Text.RegularExpressions;
using GitBackup.Configuration.Models;
using GitBackup.Runtime;

namespace GitBackup.Services.Providers;

public sealed class GitHubRepositoryProviderClient
    : ProviderHttpClientBase, IRepositoryProviderClient, IProjectMetadataProviderClient
{
    private const string DefaultApiBaseUrl = "https://api.github.com";
    private const int PageSize = 100;

    // GitHub stores issue/PR attachments as absolute URLs on its attachment hosts, embedded in the
    // markdown body. Match those and download them directly.
    private static readonly Regex AttachmentReference = new(
        @"https?://(?:user-images\.githubusercontent\.com|private-user-images\.githubusercontent\.com|github\.com/user-attachments/(?:assets|files)|github\.com/[^/\s)]+/[^/\s)]+/(?:assets|files))/[^\s)\]""'<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Provider => "github";

    public bool SupportsIssues => true;

    public bool SupportsMergeRequests => true;

    public bool SupportsReleases => true;

    public bool SupportsArtifacts => true;

    public async Task<IReadOnlyList<DiscoveredRepository>> ListRepositoriesAsync(
        RepositoryJobConfig repository,
        CredentialConfig credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            return [];
        }

        var baseUrl = ResolveGitHubApiBaseUrl(repository.BaseUrl);
        using var client = CreateClient(credential.ApiKey);

        var results = new List<DiscoveredRepository>();

        results.AddRange(await CollectAsync(
            client,
            page => $"{baseUrl}/user/repos?affiliation=owner&visibility=all&per_page=100&page={page}",
            item => MapRepository(item, isStarred: false),
            cancellationToken));

        if (repository.IncludeStarred == true)
        {
            AppLogger.Debug("Including starred repositories. provider={Provider}.", Provider);
            results.AddRange(await CollectAsync(
                client,
                page => $"{baseUrl}/user/starred?per_page=100&page={page}",
                item => MapRepository(item, isStarred: true),
                cancellationToken));
        }

        if (repository.IncludeSnippets == true)
        {
            AppLogger.Debug("Including gists. provider={Provider}.", Provider);
            results.AddRange(await CollectAsync(
                client,
                page => $"{baseUrl}/gists?per_page=100&page={page}",
                MapGist,
                cancellationToken));

            if (repository.IncludeStarred == true)
            {
                AppLogger.Debug("Including starred gists. provider={Provider}.", Provider);
                results.AddRange(await CollectAsync(
                    client,
                    page => $"{baseUrl}/gists/starred?per_page=100&page={page}",
                    MapGist,
                    cancellationToken));
            }
        }

        return DistinctByCloneUrl(results);
    }

    public async Task<IReadOnlyList<BackedUpIssue>> ListIssuesAsync(
        ProjectMetadataContext context,
        CredentialConfig credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            return [];
        }

        var (baseUrl, client, repositoryPath) = CreateProjectClient(context, credential);
        using (client)
        {
            var issues = await CollectAsync(
                client,
                page => $"{baseUrl}/repos/{repositoryPath}/issues?state=all&per_page=100&page={page}",
                MapIssue,
                cancellationToken);

            foreach (var issue in issues)
            {
                issue.Comments = await CollectAsync(
                    client,
                    page => $"{baseUrl}/repos/{repositoryPath}/issues/{issue.Number}/comments?per_page=100&page={page}",
                    MapComment,
                    cancellationToken);

                issue.Attachments = ExtractAttachments(issue.Body, issue.Comments);
            }

            return issues;
        }
    }

    public async Task<IReadOnlyList<BackedUpMergeRequest>> ListMergeRequestsAsync(
        ProjectMetadataContext context,
        CredentialConfig credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            return [];
        }

        var (baseUrl, client, repositoryPath) = CreateProjectClient(context, credential);
        using (client)
        {
            var pulls = await CollectAsync(
                client,
                page => $"{baseUrl}/repos/{repositoryPath}/pulls?state=all&per_page=100&page={page}",
                MapPullRequest,
                cancellationToken);

            foreach (var pull in pulls)
            {
                // Pull requests are issues on GitHub, so their discussion comments live on the issue
                // comments endpoint.
                pull.Comments = await CollectAsync(
                    client,
                    page => $"{baseUrl}/repos/{repositoryPath}/issues/{pull.Number}/comments?per_page=100&page={page}",
                    MapComment,
                    cancellationToken);

                pull.Attachments = ExtractAttachments(pull.Body, pull.Comments);
            }

            return pulls;
        }
    }

    public async Task<IReadOnlyList<BackedUpRelease>> ListReleasesAsync(
        ProjectMetadataContext context,
        CredentialConfig credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            return [];
        }

        var (baseUrl, client, repositoryPath) = CreateProjectClient(context, credential);
        using (client)
        {
            return await CollectAsync(
                client,
                page => $"{baseUrl}/repos/{repositoryPath}/releases?per_page=100&page={page}",
                MapRelease,
                cancellationToken);
        }
    }

    public async Task<Stream> OpenAttachmentAsync(
        ProjectMetadataContext context,
        CredentialConfig credential,
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credential.ApiKey);
        return await AttachmentDownloader.DownloadToMemoryAsync(client, downloadUrl, cancellationToken);
    }

    private (string BaseUrl, HttpClient Client, string RepositoryPath) CreateProjectClient(
        ProjectMetadataContext context,
        CredentialConfig credential)
    {
        var baseUrl = ResolveGitHubApiBaseUrl(context.BaseUrl);
        var (owner, repository) = ResolveOwnerAndRepository(context.CloneUrl);
        var repositoryPath = $"{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}";
        return (baseUrl, CreateClient(credential.ApiKey!), repositoryPath);
    }

    private static async Task<List<T>> CollectAsync<T>(
        HttpClient client,
        Func<int, string> buildRequestUri,
        Func<JsonElement, T?> mapItem,
        CancellationToken cancellationToken)
        where T : class
    {
        var items = new List<T>();

        for (var page = 1; ; page++)
        {
            var requestUri = buildRequestUri(page);
            using var response = await GetWithRetryAsync(client, requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = await ReadJsonDocumentAsync(response, cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
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

            if (itemCount < PageSize)
            {
                break;
            }
        }

        return items;
    }

    private static DiscoveredRepository? MapRepository(JsonElement item, bool isStarred)
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

    private static DiscoveredRepository? MapGist(JsonElement item)
    {
        var cloneUrl = GetStringOrNull(item, "git_pull_url");
        var id = GetStringOrNull(item, "id");
        if (string.IsNullOrWhiteSpace(cloneUrl) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new DiscoveredRepository
        {
            CloneUrl = cloneUrl,
            WebUrl = GetStringOrNull(item, "html_url"),
            Kind = DiscoveredRepositoryKind.Gist,
            Identifier = id
        };
    }

    private static BackedUpIssue? MapIssue(JsonElement item)
    {
        // The GitHub issues endpoint also returns pull requests; those carry a pull_request object.
        if (item.TryGetProperty("pull_request", out _))
        {
            return null;
        }

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

    private static BackedUpMergeRequest? MapPullRequest(JsonElement item)
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

    private static BackedUpRelease? MapRelease(JsonElement item)
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
            Attachments = ExtractReleaseAssets(item)
        };
    }

    private static IReadOnlyList<BackedUpAttachment> ExtractReleaseAssets(JsonElement item)
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
                FileName = $"{AttachmentDownloader.ShortHash(url)}-{AttachmentDownloader.SanitizeFileName(name)}",
                OriginalPath = url,
                DownloadUrl = url,
                SizeBytes = GetInt64OrNull(asset, "size")
            });
        }

        return attachments;
    }

    private static BackedUpComment? MapComment(JsonElement item)
    {
        var body = GetStringOrNull(item, "body");
        var author = GetNestedStringOrNull(item, "user", "login");
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
            System = false
        };
    }

    private static IReadOnlyList<BackedUpAttachment> ExtractAttachments(
        string? body,
        IEnumerable<BackedUpComment> comments)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var attachments = new List<BackedUpAttachment>();

        void Scan(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (Match match in AttachmentReference.Matches(text))
            {
                var url = match.Value;
                if (!seen.Add(url))
                {
                    continue;
                }

                attachments.Add(new BackedUpAttachment
                {
                    FileName = $"{AttachmentDownloader.ShortHash(url)}-{AttachmentDownloader.SanitizeFileName(LastPathSegment(url))}",
                    OriginalPath = url,
                    DownloadUrl = url
                });
            }
        }

        Scan(body);
        foreach (var comment in comments)
        {
            Scan(comment.Body);
        }

        return attachments;
    }

    private static string LastPathSegment(string url)
    {
        var withoutQuery = url.Split('?', 2)[0];
        var lastSlash = withoutQuery.LastIndexOf('/');
        return lastSlash >= 0 ? withoutQuery[(lastSlash + 1)..] : withoutQuery;
    }

    private static string ResolveGitHubApiBaseUrl(string? configuredBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return DefaultApiBaseUrl;
        }

        var trimmed = configuredBaseUrl.Trim().TrimEnd('/');
        if (trimmed.Contains("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.Equals("https://github.com", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultApiBaseUrl;
        }

        return $"{trimmed}/api/v3";
    }
}
