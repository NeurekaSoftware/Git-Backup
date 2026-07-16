using System.Net.Http.Headers;
using GitBackup.Configuration.Models;

namespace GitBackup.Services.Providers;

public sealed class ForgejoRepositoryProviderClient : ProviderHttpClientBase, IRepositoryProviderClient
{
    private const string DefaultBaseUrl = "https://codeberg.org";

    public string Provider => "forgejo";

    public async Task<IReadOnlyList<DiscoveredRepository>> ListOwnedRepositoriesAsync(
        RepositoryJobConfig repository,
        CredentialConfig credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            return [];
        }

        var baseUrl = EnsureApiSuffix(ResolveBaseUrl(repository.BaseUrl, DefaultBaseUrl), "/api/v1");
        var repositories = new List<DiscoveredRepository>();

        using var client = CreateClient(token: string.Empty);
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", credential.ApiKey.Trim());

        for (var page = 1; ; page++)
        {
            var requestUri = $"{baseUrl}/user/repos?affiliation=owner&limit=50&page={page}";
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
