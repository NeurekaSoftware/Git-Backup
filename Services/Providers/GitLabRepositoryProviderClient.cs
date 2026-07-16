using GitBackup.Configuration.Models;
using GitBackup.Runtime;

namespace GitBackup.Services.Providers;

public sealed class GitLabRepositoryProviderClient : ProviderHttpClientBase, IRepositoryProviderClient
{
    private const string DefaultBaseUrl = "https://gitlab.com";

    public string Provider => "gitlab";

    public async Task<IReadOnlyList<DiscoveredRepository>> ListRepositoriesAsync(
        RepositoryJobConfig repository,
        CredentialConfig credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            return [];
        }

        var baseUrl = EnsureApiSuffix(ResolveBaseUrl(repository.BaseUrl, DefaultBaseUrl), "/api/v4");

        using var client = CreateClient(token: string.Empty);
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", credential.ApiKey.Trim());

        var owned = await CollectAsync(
            client,
            page => $"{baseUrl}/projects?owned=true&simple=true&per_page=100&page={page}",
            cancellationToken);

        if (repository.IncludeStarred != true)
        {
            return owned;
        }

        AppLogger.Debug("Including starred repositories. provider={Provider}.", Provider);
        var starred = await CollectAsync(
            client,
            page => $"{baseUrl}/projects?starred=true&simple=true&per_page=100&page={page}",
            cancellationToken);

        return DistinctByCloneUrl(owned.Concat(starred));
    }

    private static async Task<List<DiscoveredRepository>> CollectAsync(
        HttpClient client,
        Func<int, string> buildRequestUri,
        CancellationToken cancellationToken)
    {
        var repositories = new List<DiscoveredRepository>();

        var page = 1;
        while (true)
        {
            var requestUri = buildRequestUri(page);
            using var response = await client.GetAsync(requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = await ReadJsonDocumentAsync(response, cancellationToken);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                break;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var cloneUrl = GetStringOrNull(item, "http_url_to_repo");
                if (string.IsNullOrWhiteSpace(cloneUrl))
                {
                    continue;
                }

                repositories.Add(new DiscoveredRepository
                {
                    CloneUrl = cloneUrl,
                    WebUrl = GetStringOrNull(item, "web_url")
                });
            }

            if (!response.Headers.TryGetValues("X-Next-Page", out var values))
            {
                break;
            }

            var nextPageRaw = values.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(nextPageRaw) || !int.TryParse(nextPageRaw, out var nextPage) || nextPage <= page)
            {
                break;
            }

            page = nextPage;
        }

        return repositories;
    }
}
