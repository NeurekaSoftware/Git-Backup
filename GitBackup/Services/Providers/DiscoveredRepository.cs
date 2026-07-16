namespace GitBackup.Services.Providers;

public sealed class DiscoveredRepository
{
    public required string CloneUrl { get; init; }

    public string? WebUrl { get; init; }
}
