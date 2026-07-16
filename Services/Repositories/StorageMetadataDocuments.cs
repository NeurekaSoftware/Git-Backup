using System.Text.Json;

namespace GitBackup.Services.Repositories;

internal static class StorageMetadataDocuments
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string Serialize<TDocument>(TDocument document)
    {
        return JsonSerializer.Serialize(document, SerializerOptions);
    }
}

/// <summary>
/// Advisory, human-readable metadata written alongside a repository's snapshots. It records only
/// the facts that cannot be derived from the object keys (the source URL and mode). It is never
/// read back for retention decisions — the object listing is the source of truth — so it is safe
/// to lose or rebuild.
/// </summary>
internal sealed class RepositoryMetadataDocument
{
    public string Mode { get; set; } = string.Empty;

    public string RepositoryUrl { get; set; } = string.Empty;

    public long UpdatedAtUnixSeconds { get; set; }
}
