using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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

    // One repo-wide comments walk per repository, shared by the issue and pull-request passes: GitHub
    // returns both kinds of comment from the same endpoint. Keyed on the per-repository context instance
    // so the grouped comments are released as soon as that repository's metadata sync finishes.
    private static readonly ConditionalWeakTable<ProjectMetadataContext, Task<Dictionary<long, List<BackedUpComment>>>> CommentsByContext = new();

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
        if (!HasApiKey(credential))
        {
            return [];
        }

        var baseUrl = ResolveGitHubApiBaseUrl(repository.BaseUrl);
        using var client = CreateAuthenticatedClient(credential);

        // The walks hit independent endpoints, so run them concurrently over the pooled handler and
        // merge in a stable order (owned first) — DistinctByCloneUrl keeps the first occurrence.
        var walks = new List<Task<List<DiscoveredRepository>>>
        {
            CollectAsync(
                client,
                page => $"{baseUrl}/user/repos?affiliation=owner&visibility=all&per_page={PageSize}&page={page}",
                item => MapGiteaRepository(item, isStarred: false),
                PageIsFull(PageSize),
                cancellationToken)
        };

        if (repository.IncludeStarred == true)
        {
            AppLogger.Debug("Including starred repositories. provider={Provider}.", Provider);
            walks.Add(CollectAsync(
                client,
                page => $"{baseUrl}/user/starred?per_page={PageSize}&page={page}",
                item => MapGiteaRepository(item, isStarred: true),
                PageIsFull(PageSize),
                cancellationToken));
        }

        if (repository.IncludeSnippets == true)
        {
            AppLogger.Debug("Including gists. provider={Provider}.", Provider);
            walks.Add(CollectAsync(
                client,
                page => $"{baseUrl}/gists?per_page={PageSize}&page={page}",
                MapGist,
                PageIsFull(PageSize),
                cancellationToken));

            if (repository.IncludeStarred == true)
            {
                AppLogger.Debug("Including starred gists. provider={Provider}.", Provider);
                walks.Add(CollectAsync(
                    client,
                    page => $"{baseUrl}/gists/starred?per_page={PageSize}&page={page}",
                    MapGist,
                    PageIsFull(PageSize),
                    cancellationToken));
            }
        }

        var walkResults = await Task.WhenAll(walks);
        return DistinctByCloneUrl(walkResults.SelectMany(walk => walk));
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
            // One repo-wide comments fetch (O(total/100) requests) instead of one paginated fetch per
            // issue: GitHub's issue-comments endpoint returns every issue and PR comment, each carrying
            // the issue_url it belongs to, so they can be grouped by number in memory.
            var commentsByNumber = await GetIssueCommentsByNumberAsync(context, client, baseUrl, repositoryPath, cancellationToken);

            var issues = CollectStreamAsync(
                client,
                page => $"{baseUrl}/repos/{repositoryPath}/issues?state=all&per_page={PageSize}&page={page}",
                MapIssue,
                PageIsFull(PageSize),
                cancellationToken);

            await foreach (var issue in issues)
            {
                issue.Comments = commentsByNumber.TryGetValue(issue.Number, out var comments) ? comments : [];
                issue.Attachments = ExtractAttachments(issue.Body, issue.Comments);
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
            // Pull requests are issues on GitHub, so their discussion comments live on the same
            // repo-wide issue-comments endpoint that the issue pass already walked; reuse that result
            // rather than paging it a second time.
            var commentsByNumber = await GetIssueCommentsByNumberAsync(context, client, baseUrl, repositoryPath, cancellationToken);

            var pulls = CollectStreamAsync(
                client,
                page => $"{baseUrl}/repos/{repositoryPath}/pulls?state=all&per_page={PageSize}&page={page}",
                MapGiteaPullRequest,
                PageIsFull(PageSize),
                cancellationToken);

            await foreach (var pull in pulls)
            {
                pull.Comments = commentsByNumber.TryGetValue(pull.Number, out var comments) ? comments : [];
                pull.Attachments = ExtractAttachments(pull.Body, pull.Comments);
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
                page => $"{baseUrl}/repos/{repositoryPath}/releases?per_page={PageSize}&page={page}",
                MapGiteaRelease,
                PageIsFull(PageSize),
                cancellationToken);

            await foreach (var release in releases)
            {
                yield return release;
            }
        }
    }

    protected override void ApplyAuthentication(HttpClient client, CredentialConfig credential)
    {
        if (!string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.ApiKey.Trim());
        }
    }

    private (string BaseUrl, HttpClient Client, string RepositoryPath) CreateProjectClient(
        ProjectMetadataContext context,
        CredentialConfig credential)
    {
        var baseUrl = ResolveGitHubApiBaseUrl(context.BaseUrl);
        return (baseUrl, CreateAuthenticatedClient(credential), BuildOwnerRepoPath(context.CloneUrl));
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
        return ScanBodyAndComments(body, comments, AttachmentReference, match =>
        {
            var url = match.Value;

            // The trailing group permits '/' and '.', so untrusted body text can embed dot-segments that
            // Uri normalization resolves into a different path on the same allowlisted host before the
            // request goes out with the token attached. Match on whole segments so a name like
            // "chart..v2.png" is still accepted.
            if (url.Split('?', 2)[0].Split('/').Contains(".."))
            {
                return null;
            }

            // Persist and key off the query-free URL so the short-lived ?jwt= signing token is never
            // stored (and the key stays stable across runs); the full URL is kept only for download.
            var reference = AttachmentDownloader.RedactUrl(url);
            return new BackedUpAttachment
            {
                FileName = AttachmentDownloader.BuildStorageFileName(reference, LastPathSegment(reference)),
                OriginalPath = reference,
                DownloadUrl = url
            };
        });
    }

    private static string LastPathSegment(string url)
    {
        var withoutQuery = url.Split('?', 2)[0];
        var lastSlash = withoutQuery.LastIndexOf('/');
        return lastSlash >= 0 ? withoutQuery[(lastSlash + 1)..] : withoutQuery;
    }

    /// <summary>
    /// Returns the repository's comments grouped by issue/PR number, walking the endpoint at most once
    /// per repository. The issue and pull-request passes both need the same repo-wide data and run one
    /// after the other, so the result is cached against the per-repository context: the walk happens on
    /// whichever pass runs first, and the entry becomes collectable once that context goes out of scope.
    /// </summary>
    private static Task<Dictionary<long, List<BackedUpComment>>> GetIssueCommentsByNumberAsync(
        ProjectMetadataContext context,
        HttpClient client,
        string baseUrl,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        return CommentsByContext.GetValue(
            context,
            _ => FetchIssueCommentsByNumberAsync(client, baseUrl, repositoryPath, cancellationToken));
    }

    // Fetches every issue and PR comment for the repo in one paginated walk and groups them by the
    // issue/PR number parsed from each comment's issue_url. Requested oldest-first so each thread stays
    // in chronological order.
    //
    // GitHub has no per-page comment endpoint, so this materializes the repository's whole comment
    // corpus up front: peak memory here scales with the total comment count rather than with one page.
    // That is the deliberate trade for removing the per-item N+1 (one request per issue/PR), and it is
    // released once the repository's context goes out of scope.
    private static async Task<Dictionary<long, List<BackedUpComment>>> FetchIssueCommentsByNumberAsync(
        HttpClient client,
        string baseUrl,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var commentsByNumber = new Dictionary<long, List<BackedUpComment>>();

        // CollectAsync walks pages sequentially, so the dictionary is only ever mutated on one thread.
        await CollectAsync<BackedUpComment>(
            client,
            page => $"{baseUrl}/repos/{repositoryPath}/issues/comments?sort=created&direction=asc&per_page={PageSize}&page={page}",
            item =>
            {
                var comment = MapGiteaComment(item);
                var number = ParseIssueNumber(GetStringOrNull(item, "issue_url"));
                if (comment is not null && number is not null)
                {
                    if (!commentsByNumber.TryGetValue(number.Value, out var thread))
                    {
                        thread = [];
                        commentsByNumber[number.Value] = thread;
                    }

                    thread.Add(comment);
                }

                // The grouped dictionary is the only result, so skip collection: returning the comment
                // would accumulate a second list of every comment in the repository and discard it.
                // Paging keys off the raw element count, so skipping every item is safe.
                return null;
            },
            PageIsFull(PageSize),
            cancellationToken);

        return commentsByNumber;
    }

    // Extracts the trailing issue/PR number from a GitHub issue_url such as
    // https://api.github.com/repos/{owner}/{repo}/issues/{number}.
    private static long? ParseIssueNumber(string? issueUrl)
    {
        if (string.IsNullOrEmpty(issueUrl))
        {
            return null;
        }

        var lastSlash = issueUrl.LastIndexOf('/');
        var segment = lastSlash >= 0 ? issueUrl[(lastSlash + 1)..] : issueUrl;
        return long.TryParse(segment, out var number) ? number : null;
    }

    private static string ResolveGitHubApiBaseUrl(string? configuredBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return DefaultApiBaseUrl;
        }

        // Share the "clean the configured base URL" normalization with the other providers; the GitHub
        // /api/v3 vs api.github.com suffix handling below stays provider-specific.
        var trimmed = ResolveBaseUrl(configuredBaseUrl, DefaultApiBaseUrl);
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
