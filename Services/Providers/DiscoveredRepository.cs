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

    // Provider-native project identifier (e.g. GitLab's numeric project id), used to fetch project
    // metadata such as issues and merge requests. Null when the provider addresses projects by
    // owner/repo path instead.
    public string? ProviderProjectId { get; init; }

    // True when this repository was discovered only because it is starred, not owned. Project
    // metadata (issues, merge requests) is never backed up for starred repositories.
    public bool IsStarred { get; init; }
}
