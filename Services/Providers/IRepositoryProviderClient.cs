using GitBackup.Configuration.Models;

namespace GitBackup.Services.Providers;

public interface IRepositoryProviderClient
{
    string Provider { get; }

    Task<IReadOnlyList<DiscoveredRepository>> ListRepositoriesAsync(
        RepositoryJobConfig repository,
        CredentialConfig credential,
        CancellationToken cancellationToken);
}
