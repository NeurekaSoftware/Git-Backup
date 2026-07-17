using System.Net.Http.Headers;
using System.Text.Json;
using GitBackup.Configuration.Models;
using GitBackup.Runtime;

namespace GitBackup.Services.Providers;

public sealed class ForgejoRepositoryProviderClient
    : ProviderHttpClientBase, IRepositoryProviderClient, IProjectMetadataProviderClient
{
    private const string DefaultBaseUrl = "https://codeberg.org";
    private const int PageSize = 50;

    public string Provider => "forgejo";

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

        var baseUrl = ResolveApiBaseUrl(repository.BaseUrl);
        using var client = CreateForgejoClient(credential);

        var owned = await CollectAsync(
            client,
            page => $"{baseUrl}/user/repos?affiliation=owner&limit=50&page={page}",
            item => MapRepository(item, isStarred: false),
            cancellationToken);

        if (repository.IncludeStarred != true)
        {
            return owned;
        }

        AppLogger.Debug("Including starred repositories. provider={Provider}.", Provider);
        var starred = await CollectAsync(
            client,
            page => $"{baseUrl}/user/starred?limit=50&page={page}",
            item => MapRepository(item, isStarred: true),
            cancellationToken);

        return DistinctByCloneUrl(owned.Concat(starred));
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

        var baseUrl = ResolveApiBaseUrl(context.BaseUrl);
        var repositoryPath = ResolveRepositoryPath(context);
        using var client = CreateForgejoClient(credential);

        var issues = await CollectAsync(
            client,
            page => $"{baseUrl}/repos/{repositoryPath}/issues?type=issues&state=all&limit=50&page={page}",
            MapIssue,
            cancellationToken);

        foreach (var issue in issues)
        {
            var (comments, commentAttachments) = await FetchCommentsAsync(
                client,
                page => $"{baseUrl}/repos/{repositoryPath}/issues/{issue.Number}/comments?limit=50&page={page}",
                cancellationToken);

            issue.Comments = comments;
            issue.Attachments = MergeAttachments(issue.Attachments, commentAttachments);
        }

        return issues;
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

        var baseUrl = ResolveApiBaseUrl(context.BaseUrl);
        var repositoryPath = ResolveRepositoryPath(context);
        using var client = CreateForgejoClient(credential);

        var pulls = await CollectAsync(
            client,
            page => $"{baseUrl}/repos/{repositoryPath}/pulls?state=all&limit=50&page={page}",
            MapPullRequest,
            cancellationToken);

        foreach (var pull in pulls)
        {
            // Pull requests share the issue comment thread in the Gitea/Forgejo API.
            var (comments, commentAttachments) = await FetchCommentsAsync(
                client,
                page => $"{baseUrl}/repos/{repositoryPath}/issues/{pull.Number}/comments?limit=50&page={page}",
                cancellationToken);

            pull.Comments = comments;
            pull.Attachments = MergeAttachments(pull.Attachments, commentAttachments);
        }

        return pulls;
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

        var baseUrl = ResolveApiBaseUrl(context.BaseUrl);
        var repositoryPath = ResolveRepositoryPath(context);
        using var client = CreateForgejoClient(credential);

        return await CollectAsync(
            client,
            page => $"{baseUrl}/repos/{repositoryPath}/releases?limit=50&page={page}",
            MapRelease,
            cancellationToken);
    }

    public async Task<Stream> OpenAttachmentAsync(
        ProjectMetadataContext context,
        CredentialConfig credential,
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        using var client = CreateForgejoClient(credential);
        return await AttachmentDownloader.DownloadToMemoryAsync(client, downloadUrl, cancellationToken);
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

    private static async Task<(List<BackedUpComment> Comments, List<BackedUpAttachment> Attachments)> FetchCommentsAsync(
        HttpClient client,
        Func<int, string> buildRequestUri,
        CancellationToken cancellationToken)
    {
        var comments = new List<BackedUpComment>();
        var attachments = new List<BackedUpAttachment>();

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
                var comment = MapComment(item);
                if (comment is not null)
                {
                    comments.Add(comment);
                }

                attachments.AddRange(ExtractAssets(item));
            }

            if (itemCount < PageSize)
            {
                break;
            }
        }

        return (comments, attachments);
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

    private static BackedUpIssue? MapIssue(JsonElement item)
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
            WebUrl = GetStringOrNull(item, "html_url"),
            Attachments = ExtractAssets(item).ToList()
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
            WebUrl = GetStringOrNull(item, "html_url"),
            Attachments = ExtractAssets(item).ToList()
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
            Attachments = ExtractAssets(item).ToList()
        };
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

    private static IEnumerable<BackedUpAttachment> ExtractAssets(JsonElement item)
    {
        if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var downloadUrl = GetStringOrNull(asset, "browser_download_url");
            var name = GetStringOrNull(asset, "name");
            if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            yield return new BackedUpAttachment
            {
                FileName = $"{AttachmentDownloader.ShortHash(downloadUrl)}-{AttachmentDownloader.SanitizeFileName(name)}",
                OriginalPath = downloadUrl,
                DownloadUrl = downloadUrl,
                SizeBytes = GetInt64OrNull(asset, "size")
            };
        }
    }

    private static IReadOnlyList<BackedUpAttachment> MergeAttachments(
        IReadOnlyList<BackedUpAttachment> first,
        IReadOnlyList<BackedUpAttachment> second)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<BackedUpAttachment>();
        foreach (var attachment in first.Concat(second))
        {
            if (seen.Add(attachment.OriginalPath))
            {
                merged.Add(attachment);
            }
        }

        return merged;
    }

    private HttpClient CreateForgejoClient(CredentialConfig credential)
    {
        var client = CreateClient(token: string.Empty);
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", credential.ApiKey!.Trim());
        return client;
    }

    private static string ResolveApiBaseUrl(string? configuredBaseUrl)
    {
        return EnsureApiSuffix(ResolveBaseUrl(configuredBaseUrl, DefaultBaseUrl), "/api/v1");
    }

    private static string ResolveRepositoryPath(ProjectMetadataContext context)
    {
        var (owner, repository) = ResolveOwnerAndRepository(context.CloneUrl);
        return $"{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}";
    }
}
