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
        using var client = CreateAuthenticatedClient(credential);

        var ownedWalk = CollectAsync(
            client,
            page => $"{baseUrl}/user/repos?affiliation=owner&limit={PageSize}&page={page}",
            item => MapGiteaRepository(item, isStarred: false),
            PageIsFull(PageSize),
            cancellationToken);

        if (repository.IncludeStarred != true)
        {
            return await ownedWalk;
        }

        // The owned and starred walks hit independent endpoints, so let them run concurrently.
        AppLogger.Debug("Including starred repositories. provider={Provider}.", Provider);
        var starredWalk = CollectAsync(
            client,
            page => $"{baseUrl}/user/starred?limit={PageSize}&page={page}",
            item => MapGiteaRepository(item, isStarred: true),
            PageIsFull(PageSize),
            cancellationToken);

        var owned = await ownedWalk;
        var starred = await starredWalk;
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
        using var client = CreateAuthenticatedClient(credential);

        return await CollectWithCommentsAsync(
            client,
            context.Concurrency,
            page => $"{baseUrl}/repos/{repositoryPath}/issues?type=issues&state=all&limit={PageSize}&page={page}",
            MapIssue,
            PageIsFull(PageSize),
            async (issue, token) =>
            {
                var (comments, commentAttachments) = await FetchCommentsAsync(
                    client,
                    page => $"{baseUrl}/repos/{repositoryPath}/issues/{issue.Number}/comments?limit={PageSize}&page={page}",
                    token);

                issue.Comments = comments;
                issue.Attachments = MergeAttachments(issue.Attachments, commentAttachments);
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
        var repositoryPath = ResolveRepositoryPath(context);
        using var client = CreateAuthenticatedClient(credential);

        return await CollectWithCommentsAsync(
            client,
            context.Concurrency,
            page => $"{baseUrl}/repos/{repositoryPath}/pulls?state=all&limit={PageSize}&page={page}",
            MapPullRequest,
            PageIsFull(PageSize),
            async (pull, token) =>
            {
                // Pull requests share the issue comment thread in the Gitea/Forgejo API.
                var (comments, commentAttachments) = await FetchCommentsAsync(
                    client,
                    page => $"{baseUrl}/repos/{repositoryPath}/issues/{pull.Number}/comments?limit={PageSize}&page={page}",
                    token);

                pull.Comments = comments;
                pull.Attachments = MergeAttachments(pull.Attachments, commentAttachments);
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
        var repositoryPath = ResolveRepositoryPath(context);
        using var client = CreateAuthenticatedClient(credential);

        return await CollectAsync(
            client,
            page => $"{baseUrl}/repos/{repositoryPath}/releases?limit={PageSize}&page={page}",
            MapGiteaRelease,
            PageIsFull(PageSize),
            cancellationToken);
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
        return DistinctByKey(first.Concat(second), attachment => attachment.OriginalPath);
    }

    protected override void ApplyAuthentication(HttpClient client, CredentialConfig credential)
    {
        if (!string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", credential.ApiKey.Trim());
        }
    }

    private static string ResolveApiBaseUrl(string? configuredBaseUrl)
    {
        return ComposeApiBaseUrl(configuredBaseUrl, DefaultBaseUrl, "/api/v1");
    }

    private static string ResolveRepositoryPath(ProjectMetadataContext context)
    {
        return BuildOwnerRepoPath(context.CloneUrl);
    }
}
