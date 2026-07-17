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
            page => $"{baseUrl}/user/repos?affiliation=owner&limit={PageSize}&page={page}",
            item => MapGiteaRepository(item, isStarred: false),
            PageIsFull(PageSize),
            cancellationToken);

        if (repository.IncludeStarred != true)
        {
            return owned;
        }

        AppLogger.Debug("Including starred repositories. provider={Provider}.", Provider);
        var starred = await CollectAsync(
            client,
            page => $"{baseUrl}/user/starred?limit={PageSize}&page={page}",
            item => MapGiteaRepository(item, isStarred: true),
            PageIsFull(PageSize),
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
            page => $"{baseUrl}/repos/{repositoryPath}/issues?type=issues&state=all&limit={PageSize}&page={page}",
            MapIssue,
            PageIsFull(PageSize),
            cancellationToken);

        await Parallel.ForEachAsync(
            issues,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Concurrency), CancellationToken = cancellationToken },
            async (issue, token) =>
            {
                var (comments, commentAttachments) = await FetchCommentsAsync(
                    client,
                    page => $"{baseUrl}/repos/{repositoryPath}/issues/{issue.Number}/comments?limit={PageSize}&page={page}",
                    token);

                issue.Comments = comments;
                issue.Attachments = MergeAttachments(issue.Attachments, commentAttachments);
            });

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
            page => $"{baseUrl}/repos/{repositoryPath}/pulls?state=all&limit={PageSize}&page={page}",
            MapPullRequest,
            PageIsFull(PageSize),
            cancellationToken);

        await Parallel.ForEachAsync(
            pulls,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Concurrency), CancellationToken = cancellationToken },
            async (pull, token) =>
            {
                // Pull requests share the issue comment thread in the Gitea/Forgejo API.
                var (comments, commentAttachments) = await FetchCommentsAsync(
                    client,
                    page => $"{baseUrl}/repos/{repositoryPath}/issues/{pull.Number}/comments?limit={PageSize}&page={page}",
                    token);

                pull.Comments = comments;
                pull.Attachments = MergeAttachments(pull.Attachments, commentAttachments);
            });

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
            page => $"{baseUrl}/repos/{repositoryPath}/releases?limit={PageSize}&page={page}",
            MapGiteaRelease,
            PageIsFull(PageSize),
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

    private static async Task<(List<BackedUpComment> Comments, List<BackedUpAttachment> Attachments)> FetchCommentsAsync(
        HttpClient client,
        Func<int, string> buildRequestUri,
        CancellationToken cancellationToken)
    {
        var attachments = new List<BackedUpAttachment>();
        var comments = await CollectAsync(
            client,
            buildRequestUri,
            item =>
            {
                attachments.AddRange(ExtractAssetArray(item));
                return MapGiteaComment(item);
            },
            PageIsFull(PageSize),
            cancellationToken);

        return (comments, attachments);
    }

    private static BackedUpIssue? MapIssue(JsonElement item)
    {
        var issue = MapGiteaIssue(item);
        if (issue is not null)
        {
            issue.Attachments = ExtractAssetArray(item);
        }

        return issue;
    }

    private static BackedUpMergeRequest? MapPullRequest(JsonElement item)
    {
        var pull = MapGiteaPullRequest(item);
        if (pull is not null)
        {
            pull.Attachments = ExtractAssetArray(item);
        }

        return pull;
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
