using System.Net.Http.Headers;
using GitBackup.Configuration.Models;
using GitBackup.Runtime;

namespace GitBackup.Services.Providers;

public sealed class ForgejoRepositoryProviderClient : ProviderHttpClientBase, IRepositoryProviderClient
{
    private const string DefaultBaseUrl = "https://codeberg.org";

    public string Provider => "forgejo";

    public async Task<IReadOnlyList<DiscoveredRepository>> ListRepositoriesAsync(
        RepositoryJobConfig repository,
        CredentialConfig credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            return [];
        }

        var baseUrl = EnsureApiSuffix(ResolveBaseUrl(repository.BaseUrl, DefaultBaseUrl), "/api/v1");

        using var client = CreateClient(token: string.Empty);
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", credential.ApiKey.Trim());

        var owned = await CollectAsync(
            client,
            page => $"{baseUrl}/user/repos?affiliation=owner&limit=50&page={page}",
            cancellationToken);

        if (repository.IncludeStarred != true)
        {
            return owned;
        }

        AppLogger.Debug("Including starred repositories. provider={Provider}.", Provider);
        var starred = await CollectAsync(
            client,
            page => $"{baseUrl}/user/starred?limit=50&page={page}",
            cancellationToken);

        return DistinctByCloneUrl(owned.Concat(starred));
    }

    private static async Task<List<DiscoveredRepository>> CollectAsync(
        HttpClient client,
        Func<int, string> buildRequestUri,
        CancellationToken cancellationToken)
    {
        var repositories = new List<DiscoveredRepository>();

        for (var page = 1; ; page++)
        {
            var requestUri = buildRequestUri(page);
            using var response = await client.GetAsync(requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = await ReadJsonDocumentAsync(response, cancellationToken);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var cloneUrl = GetStringOrNull(item, "clone_url");
                if (string.IsNullOrWhiteSpace(cloneUrl))
                {
                    continue;
                }

                repositories.Add(new DiscoveredRepository
                {
                    CloneUrl = cloneUrl,
                    WebUrl = GetStringOrNull(item, "html_url")
                });

                count++;
            }

            if (count < 50)
            {
                break;
            }
        }

        return repositories;
    }
}
