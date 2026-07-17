namespace GitBackup.Services.Providers;

public sealed class RepositoryProviderClientFactory
{
    private readonly Dictionary<string, IRepositoryProviderClient> _clients;

    public RepositoryProviderClientFactory(IEnumerable<IRepositoryProviderClient> clients)
    {
        _clients = clients.ToDictionary(client => client.Provider, client => client, StringComparer.OrdinalIgnoreCase);
    }

    public IRepositoryProviderClient Resolve(string provider)
    {
        if (_clients.TryGetValue(provider, out var client))
        {
            return client;
        }

        throw new InvalidOperationException($"No provider client registered for '{provider}'.");
    }

    /// <summary>
    /// Returns the provider's project-metadata client (issues and merge requests), or null when the
    /// provider is unregistered or does not support project metadata.
    /// </summary>
    public IProjectMetadataProviderClient? TryResolveMetadata(string provider)
    {
        return _clients.TryGetValue(provider, out var client)
            ? client as IProjectMetadataProviderClient
            : null;
    }
}
