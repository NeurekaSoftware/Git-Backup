namespace GitBackup.Services.Paths;

public static class StorageKeyBuilder
{
    private const string DefaultIndexPrefix = "indexes";

    public static string BuildProviderRepositoryPrefix(string provider, RepositoryPathInfo repository)
    {
        var segments = new List<string>
        {
            "repositories",
            "provider",
            provider.Trim().ToLowerInvariant()
        };

        segments.AddRange(repository.Hierarchy);
        return string.Join('/', segments);
    }

    public static string BuildUrlRepositoryPrefix(RepositoryPathInfo repository)
    {
        var segments = new List<string>
        {
            "repositories",
            "url",
            repository.FullDomain
        };

        segments.AddRange(repository.Hierarchy);
        return string.Join('/', segments);
    }

    public static string BuildProviderRepositoryIdentity(string provider, RepositoryPathInfo repository)
    {
        var segments = new List<string>
        {
            "provider",
            provider.Trim().ToLowerInvariant(),
            repository.BaseDomain
        };

        segments.AddRange(repository.Hierarchy);
        return string.Join('/', segments);
    }

    public static string BuildUrlRepositoryIdentity(RepositoryPathInfo repository)
    {
        var segments = new List<string>
        {
            "url",
            repository.FullDomain
        };

        segments.AddRange(repository.Hierarchy);
        return string.Join('/', segments);
    }

    public static string BuildRepositoryRegistryObjectKey()
    {
        var normalizedIndexPrefix = EnsurePrefix(DefaultIndexPrefix);
        return $"{normalizedIndexPrefix}repositories/registry.json".Trim('/');
    }

    public static string BuildProviderRepositoryIndexObjectKey(
        string provider,
        RepositoryPathInfo repository)
    {
        var normalizedIndexPrefix = EnsurePrefix(DefaultIndexPrefix);
        var repositoryIdentity = BuildProviderRepositoryIdentity(provider, repository);
        return $"{normalizedIndexPrefix}repositories/{repositoryIdentity}/index.json".Trim('/');
    }

    public static string BuildUrlRepositoryIndexObjectKey(RepositoryPathInfo repository)
    {
        var normalizedIndexPrefix = EnsurePrefix(DefaultIndexPrefix);
        var repositoryIdentity = BuildUrlRepositoryIdentity(repository);
        return $"{normalizedIndexPrefix}repositories/{repositoryIdentity}/index.json".Trim('/');
    }

    public static string EnsurePrefix(string keyOrPrefix)
    {
        var value = keyOrPrefix.Trim('/');
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.EndsWith('/') ? value : $"{value}/";
    }
}
