using System.Text.Json;
using GitBackup.Configuration.Models;
using GitBackup.Runtime;

namespace GitBackup.Services.Providers;

public sealed class GitHubRepositoryProviderClient : ProviderHttpClientBase, IRepositoryProviderClient
{
    private const string DefaultApiBaseUrl = "https://api.github.com";

    public string Provider => "github";

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
            MapRepository,
            cancellationToken));

        if (repository.IncludeStarred == true)
        {
            AppLogger.Debug("Including starred repositories. provider={Provider}.", Provider);
            results.AddRange(await CollectAsync(
                client,
                page => $"{baseUrl}/user/starred?per_page=100&page={page}",
                MapRepository,
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

    private static async Task<List<DiscoveredRepository>> CollectAsync(
        HttpClient client,
        Func<int, string> buildRequestUri,
        Func<JsonElement, DiscoveredRepository?> mapItem,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var repositories = new List<DiscoveredRepository>();

        for (var page = 1; ; page++)
        {
            var requestUri = buildRequestUri(page);
            using var response = await client.GetAsync(requestUri, cancellationToken);
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
                    repositories.Add(mapped);
                }
            }

            if (itemCount < pageSize)
            {
                break;
            }
        }

        return repositories;
    }

    private static DiscoveredRepository? MapRepository(JsonElement item)
    {
        var cloneUrl = GetStringOrNull(item, "clone_url");
        if (string.IsNullOrWhiteSpace(cloneUrl))
        {
            return null;
        }

        return new DiscoveredRepository
        {
            CloneUrl = cloneUrl,
            WebUrl = GetStringOrNull(item, "html_url")
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
