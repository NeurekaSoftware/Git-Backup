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
            page => $"{baseUrl}/user/repos?affiliation=owner&visibility=all&per_page={PageSize}&page={page}",
            item => MapGiteaRepository(item, isStarred: false),
            PageIsFull(PageSize),
            cancellationToken));

        if (repository.IncludeStarred == true)
        {
            AppLogger.Debug("Including starred repositories. provider={Provider}.", Provider);
            results.AddRange(await CollectAsync(
                client,
                page => $"{baseUrl}/user/starred?per_page={PageSize}&page={page}",
                item => MapGiteaRepository(item, isStarred: true),
                PageIsFull(PageSize),
                cancellationToken));
        }

        if (repository.IncludeSnippets == true)
        {
            AppLogger.Debug("Including gists. provider={Provider}.", Provider);
            results.AddRange(await CollectAsync(
                client,
                page => $"{baseUrl}/gists?per_page={PageSize}&page={page}",
                MapGist,
                PageIsFull(PageSize),
                cancellationToken));

            if (repository.IncludeStarred == true)
            {
                AppLogger.Debug("Including starred gists. provider={Provider}.", Provider);
                results.AddRange(await CollectAsync(
                    client,
                    page => $"{baseUrl}/gists/starred?per_page={PageSize}&page={page}",
                    MapGist,
                    PageIsFull(PageSize),
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
                page => $"{baseUrl}/repos/{repositoryPath}/issues?state=all&per_page={PageSize}&page={page}",
                MapIssue,
                PageIsFull(PageSize),
                cancellationToken);

            await Parallel.ForEachAsync(
                issues,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Concurrency), CancellationToken = cancellationToken },
                async (issue, token) =>
                {
                    issue.Comments = await CollectAsync(
                        client,
                        page => $"{baseUrl}/repos/{repositoryPath}/issues/{issue.Number}/comments?per_page={PageSize}&page={page}",
                        MapGiteaComment,
                        PageIsFull(PageSize),
                        token);

                    issue.Attachments = ExtractAttachments(issue.Body, issue.Comments);
                });

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
                page => $"{baseUrl}/repos/{repositoryPath}/pulls?state=all&per_page={PageSize}&page={page}",
                MapGiteaPullRequest,
                PageIsFull(PageSize),
                cancellationToken);

            await Parallel.ForEachAsync(
                pulls,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Concurrency), CancellationToken = cancellationToken },
                async (pull, token) =>
                {
                    // Pull requests are issues on GitHub, so their discussion comments live on the
                    // issue comments endpoint.
                    pull.Comments = await CollectAsync(
                        client,
                        page => $"{baseUrl}/repos/{repositoryPath}/issues/{pull.Number}/comments?per_page={PageSize}&page={page}",
                        MapGiteaComment,
                        PageIsFull(PageSize),
                        token);

                    pull.Attachments = ExtractAttachments(pull.Body, pull.Comments);
                });

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
                page => $"{baseUrl}/repos/{repositoryPath}/releases?per_page={PageSize}&page={page}",
                MapGiteaRelease,
                PageIsFull(PageSize),
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

    // The GitHub issues endpoint also returns pull requests; those carry a pull_request object and are
    // skipped here (they are backed up via the pulls endpoint instead).
    private static BackedUpIssue? MapIssue(JsonElement item)
    {
        return item.TryGetProperty("pull_request", out _) ? null : MapGiteaIssue(item);
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

                // Persist and key off the query-free URL so the short-lived ?jwt= signing token is never
                // stored (and the key stays stable across runs); the full URL is kept only for download.
                var reference = AttachmentDownloader.RedactUrl(url);
                if (!seen.Add(reference))
                {
                    continue;
                }

                attachments.Add(new BackedUpAttachment
                {
                    FileName = $"{AttachmentDownloader.ShortHash(reference)}-{AttachmentDownloader.SanitizeFileName(LastPathSegment(reference))}",
                    OriginalPath = reference,
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
