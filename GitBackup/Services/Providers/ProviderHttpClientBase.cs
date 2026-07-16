using System.Net.Http.Headers;
using System.Text.Json;

namespace GitBackup.Services.Providers;

public abstract class ProviderHttpClientBase
{
    protected static HttpClient CreateClient(string token)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitBackup", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        return client;
    }

    protected static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    protected static string ResolveBaseUrl(string? configuredBaseUrl, string defaultBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return defaultBaseUrl.TrimEnd('/');
        }

        return configuredBaseUrl.Trim().TrimEnd('/');
    }

    protected static string EnsureApiSuffix(string baseUrl, string apiSuffix)
    {
        if (baseUrl.EndsWith(apiSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return baseUrl;
        }

        return $"{baseUrl}{apiSuffix}";
    }

    protected static string? GetStringOrNull(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
