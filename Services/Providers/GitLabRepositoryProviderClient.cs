using System.Text.Json;
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

        var results = new List<DiscoveredRepository>();

        results.AddRange(await CollectAsync(
            client,
            page => $"{baseUrl}/projects?owned=true&simple=true&per_page=100&page={page}",
            MapProject,
            cancellationToken));

        if (repository.IncludeStarred == true)
        {
            AppLogger.Debug("Including starred repositories. provider={Provider}.", Provider);
            results.AddRange(await CollectAsync(
                client,
                page => $"{baseUrl}/projects?starred=true&simple=true&per_page=100&page={page}",
                MapProject,
                cancellationToken));
        }

        if (repository.IncludeSnippets == true)
        {
            // GitLab has no "starred snippets" endpoint, so includeStarred adds nothing here.
            AppLogger.Debug("Including snippets. provider={Provider}.", Provider);
            results.AddRange(await CollectAsync(
                client,
                page => $"{baseUrl}/snippets?per_page=100&page={page}",
                MapSnippet,
                cancellationToken));
        }

        return DistinctByCloneUrl(results);
    }

    private static async Task<List<DiscoveredRepository>> CollectAsync(
        HttpClient client,
        Func<int, string> buildRequestUri,
        Func<JsonElement, DiscoveredRepository?> mapItem,
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
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var mapped = mapItem(item);
                if (mapped is not null)
                {
                    repositories.Add(mapped);
                }
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

    private static DiscoveredRepository? MapProject(JsonElement item)
    {
        var cloneUrl = GetStringOrNull(item, "http_url_to_repo");
        if (string.IsNullOrWhiteSpace(cloneUrl))
        {
            return null;
        }

        return new DiscoveredRepository
        {
            CloneUrl = cloneUrl,
            WebUrl = GetStringOrNull(item, "web_url")
        };
    }

    private static DiscoveredRepository? MapSnippet(JsonElement item)
    {
        // The list endpoint (GET /snippets) omits http_url_to_repo, so clone via the web URL + ".git".
        var id = GetSnippetId(item);
        var webUrl = GetStringOrNull(item, "web_url");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(webUrl))
        {
            return null;
        }

        // A non-null project_id marks a project snippet, which nests under its owning project.
        var isProjectSnippet = item.TryGetProperty("project_id", out var projectId)
            && projectId.ValueKind == JsonValueKind.Number;

        return new DiscoveredRepository
        {
            CloneUrl = $"{webUrl}.git",
            WebUrl = webUrl,
            Kind = DiscoveredRepositoryKind.Snippet,
            Identifier = id,
            ParentUrl = isProjectSnippet ? TrimSnippetSuffix(webUrl) : null
        };
    }

    private static string? GetSnippetId(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var idElement))
        {
            return null;
        }

        return idElement.ValueKind switch
        {
            JsonValueKind.Number => idElement.GetRawText(),
            JsonValueKind.String => idElement.GetString(),
            _ => null
        };
    }

    private static string? TrimSnippetSuffix(string webUrl)
    {
        // Project snippet web URLs look like https://host/<namespace>/<project>/-/snippets/<id>
        // (or legacy https://host/<namespace>/<project>/snippets/<id>). Strip the marker to get the
        // owning project's URL.
        var marker = webUrl.IndexOf("/-/snippets/", StringComparison.Ordinal);
        if (marker < 0)
        {
            marker = webUrl.LastIndexOf("/snippets/", StringComparison.Ordinal);
        }

        return marker > 0 ? webUrl[..marker] : null;
    }
}
