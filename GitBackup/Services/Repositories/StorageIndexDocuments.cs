using System.Text.Json;

namespace GitBackup.Services.Repositories;

internal static class StorageIndexDocuments
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string Serialize<TDocument>(TDocument document)
    {
        return JsonSerializer.Serialize(document, SerializerOptions);
    }

    public static bool TryDeserialize<TDocument>(string json, out TDocument? document)
    {
        try
        {
            document = JsonSerializer.Deserialize<TDocument>(json, SerializerOptions);
            return document is not null;
        }
        catch
        {
            document = default;
            return false;
        }
    }
}

internal sealed class RepositoryRegistryDocument
{
    public List<string> IndexKeys { get; set; } = [];
}

internal sealed class RepositoryIndexDocument
{
    public string Mode { get; set; } = string.Empty;

    public string RepositoryIdentity { get; set; } = string.Empty;

    public List<RepositorySnapshotDocument> Snapshots { get; set; } = [];
}

internal sealed class RepositorySnapshotDocument
{
    public string RootPrefix { get; set; } = string.Empty;

    public long TimestampUnixSeconds { get; set; }
}
