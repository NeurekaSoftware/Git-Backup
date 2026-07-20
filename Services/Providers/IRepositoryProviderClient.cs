using GitBackup.Configuration.Models;

namespace GitBackup.Services.Providers;

public interface IRepositoryProviderClient
{
    string Provider { get; }

    /// <summary>
    /// Whether the provider exposes gists or snippets. False means an <c>includeSnippets</c> job is
    /// reported as unsupported rather than silently discovering nothing, matching how the metadata
    /// client reports its own capability gaps.
    /// </summary>
    bool SupportsSnippets { get; }

    Task<IReadOnlyList<DiscoveredRepository>> ListRepositoriesAsync(
        RepositoryJobConfig repository,
        CredentialConfig credential,
        CancellationToken cancellationToken);
}
