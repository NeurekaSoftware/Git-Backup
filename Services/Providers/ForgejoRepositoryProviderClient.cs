using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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

    // Forgejo has no gists/snippets API, so includeSnippets cannot be honored here.
    public bool SupportsSnippets => false;

    public bool SupportsIssues => true;

    public bool SupportsMergeRequests => true;

    public bool SupportsReleases => true;

    public bool SupportsArtifacts => true;

    public async Task<IReadOnlyList<DiscoveredRepository>> ListRepositoriesAsync(
        RepositoryJobConfig repository,
        CredentialConfig credential,
        CancellationToken cancellationToken)
    {
        if (!HasApiKey(credential))
        {
            return [];
        }

        var baseUrl = ResolveApiBaseUrl(repository.BaseUrl);
        using var client = CreateAuthenticatedClient(credential);

        // The walks hit independent endpoints, so they run concurrently over the pooled handler and are
        // merged owned-first by MergeDiscoveryWalksAsync.
        var walks = new List<Task<List<DiscoveredRepository>>>
        {
            CollectAsync(
                client,
                page => $"{baseUrl}/user/repos?affiliation=owner&limit={PageSize}&page={page}",
                item => MapGiteaRepository(item, isStarred: false),
                PageIsFull(PageSize),
                cancellationToken)
        };

        if (repository.IncludeStarred == true)
        {
            AppLogger.Debug("Including starred repositories. provider={Provider}.", Provider);
            walks.Add(CollectAsync(
                client,
                page => $"{baseUrl}/user/starred?limit={PageSize}&page={page}",
                item => MapGiteaRepository(item, isStarred: true),
                PageIsFull(PageSize),
                cancellationToken));
        }

        return await MergeDiscoveryWalksAsync(walks);
    }

    public async IAsyncEnumerable<BackedUpIssue> ListIssuesAsync(
        ProjectMetadataContext context,
        CredentialConfig credential,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!HasApiKey(credential))
        {
            yield break;
        }

        var (baseUrl, client, repositoryPath) = CreateProjectClient(context, credential);
        using (client)
        {
            var issues = CollectWithCommentsAsync(
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

            await foreach (var issue in issues)
            {
                yield return issue;
            }
        }
    }

    public async IAsyncEnumerable<BackedUpMergeRequest> ListMergeRequestsAsync(
        ProjectMetadataContext context,
        CredentialConfig credential,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!HasApiKey(credential))
        {
            yield break;
        }

        var (baseUrl, client, repositoryPath) = CreateProjectClient(context, credential);
        using (client)
        {
            var pulls = CollectWithCommentsAsync(
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

            await foreach (var pull in pulls)
            {
                yield return pull;
            }
        }
    }

    public async IAsyncEnumerable<BackedUpRelease> ListReleasesAsync(
        ProjectMetadataContext context,
        CredentialConfig credential,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!HasApiKey(credential))
        {
            yield break;
        }

        var (baseUrl, client, repositoryPath) = CreateProjectClient(context, credential);
        using (client)
        {
            var releases = CollectStreamAsync(
                client,
                page => $"{baseUrl}/repos/{repositoryPath}/releases?limit={PageSize}&page={page}",
                MapGiteaRelease,
                PageIsFull(PageSize),
                cancellationToken);

            await foreach (var release in releases)
            {
                yield return release;
            }
        }
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

    // The shared preamble every metadata method opens with: resolve the API base URL, create the
    // authenticated client, and resolve the owner/repo path.
    private (string BaseUrl, HttpClient Client, string RepositoryPath) CreateProjectClient(
        ProjectMetadataContext context,
        CredentialConfig credential)
    {
        return (ResolveApiBaseUrl(context.BaseUrl), CreateAuthenticatedClient(credential), ResolveRepositoryPath(context));
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
