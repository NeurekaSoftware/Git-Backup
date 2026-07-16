using GitBackup.Configuration.Models;

namespace GitBackup.Services.Providers;

public interface IRepositoryProviderClient
{
    string Provider { get; }

    Task<IReadOnlyList<DiscoveredRepository>> ListOwnedRepositoriesAsync(
        RepositoryJobConfig repository,
        CredentialConfig credential,
        CancellationToken cancellationToken);
}
