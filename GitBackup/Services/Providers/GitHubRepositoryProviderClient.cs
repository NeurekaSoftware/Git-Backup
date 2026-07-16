using GitBackup.Configuration.Models;

namespace GitBackup.Services.Providers;

public sealed class GitHubRepositoryProviderClient : ProviderHttpClientBase, IRepositoryProviderClient
{
    private const string DefaultApiBaseUrl = "https://api.github.com";

    public string Provider => "github";

    public async Task<IReadOnlyList<DiscoveredRepository>> ListOwnedRepositoriesAsync(
        RepositoryJobConfig repository,
        CredentialConfig credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            return [];
        }

        var baseUrl = ResolveGitHubApiBaseUrl(repository.BaseUrl);
        var repositories = new List<DiscoveredRepository>();

        using var client = CreateClient(credential.ApiKey);

        for (var page = 1; ; page++)
        {
            var requestUri = $"{baseUrl}/user/repos?affiliation=owner&visibility=all&per_page=100&page={page}";
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

            if (count < 100)
            {
                break;
            }
        }

        return repositories;
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
