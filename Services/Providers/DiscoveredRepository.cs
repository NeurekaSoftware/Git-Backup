namespace GitBackup.Services.Providers;

public enum DiscoveredRepositoryKind
{
    Repository,
    Gist,
    Snippet
}

public sealed class DiscoveredRepository
{
    public required string CloneUrl { get; init; }

    public string? WebUrl { get; init; }

    public DiscoveredRepositoryKind Kind { get; init; } = DiscoveredRepositoryKind.Repository;

    // Gist/snippet identifier, used to build the storage key for those resources.
    public string? Identifier { get; init; }

    // For a project snippet: the owning project's web URL, used to nest it under that project's
    // storage prefix. Null for gists and personal snippets.
    public string? ParentUrl { get; init; }
}
