using System.Text.Json;
using System.Text.RegularExpressions;
using GitBackup.Configuration.Models;
using GitBackup.Runtime;
using GitBackup.Services.Paths;

namespace GitBackup.Services.Providers;

public sealed class GitLabRepositoryProviderClient
    : ProviderHttpClientBase, IRepositoryProviderClient, IProjectMetadataProviderClient
{
    private const string DefaultBaseUrl = "https://gitlab.com";

    // GitLab renders upload references as /uploads/{32-hex-sha}/{filename} inside issue, merge
    // request, and note bodies. The filename runs until whitespace or a markdown/HTML delimiter.
    private static readonly Regex UploadReference = new(
        @"/uploads/([0-9a-fA-F]{32})/([^\s)\]""'<>]+)",
        RegexOptions.Compiled);

    public string Provider => "gitlab";

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
        using var client = CreateAuthenticatedClient(credential);

        var results = new List<DiscoveredRepository>();

        results.AddRange(await CollectAsync(
            client,
            page => $"{baseUrl}/projects?owned=true&simple=true&per_page=100&page={page}",
            item => MapProject(item, isStarred: false),
            HasNextPage,
            cancellationToken));

        if (repository.IncludeStarred == true)
        {
            AppLogger.Debug("Including starred repositories. provider={Provider}.", Provider);
            results.AddRange(await CollectAsync(
                client,
                page => $"{baseUrl}/projects?starred=true&simple=true&per_page=100&page={page}",
                item => MapProject(item, isStarred: true),
                HasNextPage,
                cancellationToken));
        }

        if (repository.IncludeSnippets == true)
        {
            // GitLab has no "starred snippets" endpoint, so includeStarred adds nothing here.
            AppLogger.Debug("Including snippets. provider={Provider}.", Provider);
            results.AddRange(await CollectAsync(
                client,
                page => $"{baseUrl}/snippets?per_page=100&page={page}",
                MapSnippet,
                HasNextPage,
                cancellationToken));
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

        var baseUrl = ResolveApiBaseUrl(context.BaseUrl);
        var projectId = ResolveProjectIdentifier(context);
        using var client = CreateAuthenticatedClient(credential);

        return await CollectWithCommentsAsync(
            client,
            context.Concurrency,
            page => $"{baseUrl}/projects/{projectId}/issues?per_page=100&page={page}",
            MapIssue,
            HasNextPage,
            async (issue, token) =>
            {
                issue.Comments = await CollectAsync(
                    client,
                    page => $"{baseUrl}/projects/{projectId}/issues/{issue.Number}/notes?per_page=100&sort=asc&page={page}",
                    MapNote,
                    HasNextPage,
                    token);

                issue.Attachments = ExtractAttachments(context, issue.Body, issue.Comments);
            },
            cancellationToken);
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
        var projectId = ResolveProjectIdentifier(context);
        using var client = CreateAuthenticatedClient(credential);

        return await CollectWithCommentsAsync(
            client,
            context.Concurrency,
            page => $"{baseUrl}/projects/{projectId}/merge_requests?per_page=100&page={page}",
            MapMergeRequest,
            HasNextPage,
            async (mergeRequest, token) =>
            {
                mergeRequest.Comments = await CollectAsync(
                    client,
                    page => $"{baseUrl}/projects/{projectId}/merge_requests/{mergeRequest.Number}/notes?per_page=100&sort=asc&page={page}",
                    MapNote,
                    HasNextPage,
                    token);

                mergeRequest.Attachments = ExtractAttachments(context, mergeRequest.Body, mergeRequest.Comments);
            },
            cancellationToken);
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
        var projectId = ResolveProjectIdentifier(context);
        var instanceHost = ResolveInstanceHost(context);
        using var client = CreateAuthenticatedClient(credential);

        return await CollectAsync(
            client,
            page => $"{baseUrl}/projects/{projectId}/releases?per_page=100&page={page}",
            item => MapRelease(item, instanceHost),
            HasNextPage,
            cancellationToken);
    }

    // GitLab paginates via the X-Next-Page response header rather than a full-page heuristic.
    private static bool HasNextPage(HttpResponseMessage response, int page, int itemCount)
    {
        if (!response.Headers.TryGetValues("X-Next-Page", out var values))
        {
            return false;
        }

        var raw = values.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var next) && next > page;
    }

    private static DiscoveredRepository? MapProject(JsonElement item, bool isStarred)
    {
        var cloneUrl = GetStringOrNull(item, "http_url_to_repo");
        if (string.IsNullOrWhiteSpace(cloneUrl))
        {
            return null;
        }

        return new DiscoveredRepository
        {
            CloneUrl = cloneUrl,
            WebUrl = GetStringOrNull(item, "web_url"),
            ProviderProjectId = GetInt64OrNull(item, "id")?.ToString(),
            IsStarred = isStarred
        };
    }

    private static DiscoveredRepository? MapSnippet(JsonElement item)
    {
        // The list endpoint (GET /snippets) omits http_url_to_repo, so clone via the web URL + ".git".
        var id = GetSnippetId(item);
        var webUrl = GetStringOrNull(item, "web_url");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(webUrl))
        {
            return null;
        }

        // A non-null project_id marks a project snippet, which nests under its owning project.
        var isProjectSnippet = item.TryGetProperty("project_id", out var projectId)
            && projectId.ValueKind == JsonValueKind.Number;

        return new DiscoveredRepository
        {
            CloneUrl = $"{webUrl}.git",
            WebUrl = webUrl,
            Kind = DiscoveredRepositoryKind.Snippet,
            Identifier = id,
            ParentUrl = isProjectSnippet ? TrimSnippetSuffix(webUrl) : null
        };
    }

    private static BackedUpIssue? MapIssue(JsonElement item)
    {
        var number = GetInt64OrNull(item, "iid") ?? GetInt64OrNull(item, "id");
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
            Author = GetNestedStringOrNull(item, "author", "username"),
            Body = GetStringOrNull(item, "description"),
            CreatedAt = GetDateTimeOffsetOrNull(item, "created_at"),
            UpdatedAt = GetDateTimeOffsetOrNull(item, "updated_at"),
            ClosedAt = GetDateTimeOffsetOrNull(item, "closed_at"),
            Labels = GetLabelNames(item, "labels"),
            WebUrl = GetStringOrNull(item, "web_url")
        };
    }

    private static BackedUpMergeRequest? MapMergeRequest(JsonElement item)
    {
        var number = GetInt64OrNull(item, "iid") ?? GetInt64OrNull(item, "id");
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
            Author = GetNestedStringOrNull(item, "author", "username"),
            Body = GetStringOrNull(item, "description"),
            SourceBranch = GetStringOrNull(item, "source_branch"),
            TargetBranch = GetStringOrNull(item, "target_branch"),
            CreatedAt = GetDateTimeOffsetOrNull(item, "created_at"),
            UpdatedAt = GetDateTimeOffsetOrNull(item, "updated_at"),
            MergedAt = GetDateTimeOffsetOrNull(item, "merged_at"),
            ClosedAt = GetDateTimeOffsetOrNull(item, "closed_at"),
            Labels = GetLabelNames(item, "labels"),
            WebUrl = GetStringOrNull(item, "web_url")
        };
    }

    private static BackedUpComment? MapNote(JsonElement item)
    {
        return MapComment(item, "author", "username", readSystemFlag: true);
    }

    private static BackedUpRelease? MapRelease(JsonElement item, string instanceHost)
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
            Body = GetStringOrNull(item, "description"),
            Author = GetNestedStringOrNull(item, "author", "username"),
            CreatedAt = GetDateTimeOffsetOrNull(item, "created_at"),
            PublishedAt = GetDateTimeOffsetOrNull(item, "released_at"),
            Commit = GetNestedStringOrNull(item, "commit", "id"),
            WebUrl = GetNestedStringOrNull(item, "_links", "self"),
            Attachments = ExtractReleaseLinks(item, instanceHost)
        };
    }

    /// <summary>
    /// Extracts GitLab release asset links. Only links whose target is on the GitLab instance are
    /// marked downloadable — external links are recorded as references so the private token is never
    /// sent to a third-party host. Auto-generated source archives (assets.sources) are skipped, since
    /// the repository mirror already captures every tag's source.
    /// </summary>
    private static IReadOnlyList<BackedUpAttachment> ExtractReleaseLinks(JsonElement item, string instanceHost)
    {
        var attachments = new List<BackedUpAttachment>();
        if (!item.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Object ||
            !assets.TryGetProperty("links", out var links) ||
            links.ValueKind != JsonValueKind.Array)
        {
            return attachments;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in links.EnumerateArray())
        {
            var name = GetStringOrNull(link, "name");
            var url = GetStringOrNull(link, "url");
            var directUrl = GetStringOrNull(link, "direct_asset_url");
            var reference = url ?? directUrl;
            var fetchUrl = directUrl ?? url;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(fetchUrl))
            {
                continue;
            }

            if (!seen.Add(reference))
            {
                continue;
            }

            attachments.Add(new BackedUpAttachment
            {
                FileName = $"{AttachmentDownloader.ShortHash(reference)}-{AttachmentDownloader.SanitizeFileName(name)}",
                OriginalPath = reference,
                DownloadUrl = fetchUrl,
                Downloadable = IsInstanceHost(url ?? directUrl!, instanceHost)
            });
        }

        return attachments;
    }

    private static string ResolveInstanceHost(ProjectMetadataContext context)
    {
        var reference = context.WebUrl ?? context.CloneUrl;
        return Uri.TryCreate(reference, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }

    private static bool IsInstanceHost(string url, string instanceHost)
    {
        return !string.IsNullOrEmpty(instanceHost)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, instanceHost, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<BackedUpAttachment> ExtractAttachments(
        ProjectMetadataContext context,
        string? body,
        IEnumerable<BackedUpComment> comments)
    {
        var projectUrl = (context.WebUrl ?? GitRepositoryUrl.TrimGitSuffix(context.CloneUrl)).TrimEnd('/');

        return ScanBodyAndComments(body, comments, UploadReference, match =>
        {
            var sha = match.Groups[1].Value;
            var rawName = match.Groups[2].Value;
            var originalPath = $"/uploads/{sha}/{rawName}";
            return new BackedUpAttachment
            {
                FileName = $"{sha[..8]}-{AttachmentDownloader.SanitizeFileName(rawName)}",
                OriginalPath = originalPath,
                DownloadUrl = $"{projectUrl}{originalPath}"
            };
        });
    }

    protected override HttpClient CreateAuthenticatedClient(CredentialConfig credential)
    {
        // Authenticate with an OAuth-style Bearer header (GitLab accepts a personal/group access token
        // as a Bearer token) instead of the custom PRIVATE-TOKEN header. The .NET handler strips the
        // standard Authorization header on a cross-host redirect, so the token is not forwarded when an
        // asset download 302-redirects to object storage on a different host.
        return CreateClient(credential.ApiKey);
    }

    private static string ResolveApiBaseUrl(string? configuredBaseUrl)
    {
        return ComposeApiBaseUrl(configuredBaseUrl, DefaultBaseUrl, "/api/v4");
    }

    private static string ResolveProjectIdentifier(ProjectMetadataContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ProviderProjectId))
        {
            return context.ProviderProjectId.Trim();
        }

        // Fall back to the URL-encoded project path (namespace/project), which GitLab accepts in
        // place of the numeric id. This keeps issue backup working even if discovery did not capture
        // the id for some reason.
        if (!Uri.TryCreate(context.CloneUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Cannot resolve a GitLab project id from '{context.CloneUrl}'.");
        }

        var path = GitRepositoryUrl.TrimGitSuffix(uri.AbsolutePath.Trim('/'));
        return Uri.EscapeDataString(path);
    }

    private static string? GetSnippetId(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var idElement))
        {
            return null;
        }

        return idElement.ValueKind switch
        {
            JsonValueKind.Number => idElement.GetRawText(),
            JsonValueKind.String => idElement.GetString(),
            _ => null
        };
    }

    private static string? TrimSnippetSuffix(string webUrl)
    {
        // Project snippet web URLs look like https://host/<namespace>/<project>/-/snippets/<id>
        // (or legacy https://host/<namespace>/<project>/snippets/<id>). Strip the marker to get the
        // owning project's URL.
        var marker = webUrl.IndexOf("/-/snippets/", StringComparison.Ordinal);
        if (marker < 0)
        {
            marker = webUrl.LastIndexOf("/snippets/", StringComparison.Ordinal);
        }

        return marker > 0 ? webUrl[..marker] : null;
    }

}
