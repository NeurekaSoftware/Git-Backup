using System.Security.Cryptography;
using System.Text;
using GitBackup.Configuration;
using GitBackup.Configuration.Models;
using GitBackup.Runtime;
using GitBackup.Services.Git;
using GitBackup.Services.Paths;
using GitBackup.Services.Providers;
using GitBackup.Services.Storage;

namespace GitBackup.Services.Repositories;

public sealed class RepositorySyncService
{
    private const string RepositoryMarkerName = ".repository-root";
    private const string ArchiveObjectNameSuffix = "_repo.tar.gz";

    private readonly RepositoryProviderClientFactory _providerFactory;
    private readonly IGitRepositoryService _gitRepositoryService;
    private readonly Func<StorageConfig, IObjectStorageService> _objectStorageServiceFactory;
    private readonly string _workingRoot;

    public RepositorySyncService(
        RepositoryProviderClientFactory providerFactory,
        IGitRepositoryService gitRepositoryService,
        Func<StorageConfig, IObjectStorageService> objectStorageServiceFactory,
        string workingRoot)
    {
        _providerFactory = providerFactory;
        _gitRepositoryService = gitRepositoryService;
        _objectStorageServiceFactory = objectStorageServiceFactory;
        _workingRoot = workingRoot;
    }

    public async Task RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var enabledRepositories = settings.Repositories.Where(repository => repository?.Enabled != false).ToArray();
        AppLogger.Info("Repository run started. enabledJobs={EnabledJobCount}", enabledRepositories.Length);

        var objectStorageService = _objectStorageServiceFactory(settings.Storage);
        var repositoryRegistryObjectKey = StorageKeyBuilder.BuildRepositoryRegistryObjectKey();

        var repositoryRegistry = await LoadRepositoryRegistryAsync(objectStorageService, repositoryRegistryObjectKey, cancellationToken);
        var knownIndexKeys = new HashSet<string>(repositoryRegistry.IndexKeys, StringComparer.Ordinal);
        var repositoryRegistryChanged = false;

        AppLogger.Debug(
            "Repository storage target configured. endpoint={Endpoint}, bucket={Bucket}, region={Region}.",
            settings.Storage.Endpoint,
            settings.Storage.Bucket,
            settings.Storage.Region);

        foreach (var repository in enabledRepositories)
        {
            if (repository is null)
            {
                AppLogger.Warn("Skipping repository job because the entry is missing.");
                continue;
            }

            try
            {
                if (string.Equals(repository.Mode, RepositoryJobModes.Provider, StringComparison.OrdinalIgnoreCase))
                {
                    repositoryRegistryChanged |= await RunProviderModeAsync(
                        settings,
                        repository,
                        objectStorageService,
                        knownIndexKeys,
                        cancellationToken);
                }
                else if (string.Equals(repository.Mode, RepositoryJobModes.Url, StringComparison.OrdinalIgnoreCase))
                {
                    repositoryRegistryChanged |= await RunUrlModeAsync(
                        settings,
                        repository,
                        objectStorageService,
                        knownIndexKeys,
                        cancellationToken);
                }
                else
                {
                    AppLogger.Warn(
                        "Skipping repository job because mode is invalid. mode={Mode}",
                        repository.Mode);
                }
            }
            catch (Exception exception)
            {
                AppLogger.Error(
                    exception,
                    "Repository job failed. mode={Mode}, error={ErrorMessage}",
                    repository.Mode,
                    exception.Message);
            }
        }

        if (repositoryRegistryChanged)
        {
            repositoryRegistry.IndexKeys = knownIndexKeys.OrderBy(value => value, StringComparer.Ordinal).ToList();
            await objectStorageService.UploadTextAsync(
                repositoryRegistryObjectKey,
                StorageIndexDocuments.Serialize(repositoryRegistry),
                cancellationToken);
        }

        AppLogger.Info(
            "Repository run completed. trackedRepositoryIndexes={RepositoryIndexCount}.",
            knownIndexKeys.Count);
    }

    private async Task<bool> RunProviderModeAsync(
        Settings settings,
        RepositoryJobConfig repository,
        IObjectStorageService objectStorageService,
        HashSet<string> knownIndexKeys,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repository.Provider) || string.IsNullOrWhiteSpace(repository.Credential))
        {
            AppLogger.Warn("Skipping provider repository job because provider or credential is missing.");
            return false;
        }

        if (!settings.Credentials.TryGetValue(repository.Credential, out var credentialConfig))
        {
            AppLogger.Warn(
                "Skipping provider repository job because credential is missing. provider={Provider}, credential={Credential}",
                repository.Provider,
                repository.Credential);
            return false;
        }

        AppLogger.Info("Provider repository discovery started. provider={Provider}", repository.Provider);
        var providerClient = _providerFactory.Resolve(repository.Provider);
        var discoveredRepositories = await providerClient.ListOwnedRepositoriesAsync(repository, credentialConfig, cancellationToken);
        AppLogger.Info(
            "Provider repository discovery completed. provider={Provider}, repositories={RepositoryCount}.",
            repository.Provider,
            discoveredRepositories.Count);

        var gitCredential = CredentialResolver.ResolveGitCredential(credentialConfig);
        var registryChanged = false;

        foreach (var discoveredRepository in discoveredRepositories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(discoveredRepository.CloneUrl))
            {
                continue;
            }

            try
            {
                var pathInfo = RepositoryPathParser.Parse(discoveredRepository.CloneUrl);
                var repositoryPrefix = StorageKeyBuilder.BuildProviderRepositoryPrefix(repository.Provider, pathInfo);
                var repositoryIdentity = StorageKeyBuilder.BuildProviderRepositoryIdentity(repository.Provider, pathInfo);
                var repositoryIndexObjectKey = StorageKeyBuilder.BuildProviderRepositoryIndexObjectKey(repository.Provider, pathInfo);
                var localPath = Path.Combine(
                    _workingRoot,
                    "repositories",
                    RepositoryJobModes.Provider,
                    ComputeDeterministicFolderName($"{repository.Provider}:{discoveredRepository.CloneUrl}"));

                var indexAdded = await SyncRepositorySnapshotAsync(
                    mode: RepositoryJobModes.Provider,
                    repositoryUrl: discoveredRepository.CloneUrl,
                    repositoryIdentity,
                    repositoryPrefix,
                    repositoryIndexObjectKey,
                    localPath,
                    gitCredential,
                    force: true,
                    includeLfs: repository.Lfs == true,
                    objectStorageService,
                    knownIndexKeys,
                    cancellationToken);

                registryChanged |= indexAdded;
            }
            catch (Exception exception)
            {
                AppLogger.Error(
                    exception,
                    "Provider repository sync failed. provider={Provider}, repository={RepositoryUrl}, error={ErrorMessage}",
                    repository.Provider,
                    discoveredRepository.CloneUrl,
                    exception.Message);
            }
        }

        return registryChanged;
    }

    private async Task<bool> RunUrlModeAsync(
        Settings settings,
        RepositoryJobConfig repository,
        IObjectStorageService objectStorageService,
        HashSet<string> knownIndexKeys,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repository.Url))
        {
            AppLogger.Warn("Skipping URL repository job because url is missing.");
            return false;
        }

        GitCredential? gitCredential = null;
        if (!string.IsNullOrWhiteSpace(repository.Credential))
        {
            if (!settings.Credentials.TryGetValue(repository.Credential, out var credentialConfig))
            {
                AppLogger.Warn(
                    "Skipping URL repository job because credential is missing. repository={RepositoryUrl}, credential={Credential}",
                    repository.Url,
                    repository.Credential);
                return false;
            }

            gitCredential = CredentialResolver.ResolveGitCredential(credentialConfig);
        }

        var pathInfo = RepositoryPathParser.Parse(repository.Url);
        var repositoryPrefix = StorageKeyBuilder.BuildUrlRepositoryPrefix(pathInfo);
        var repositoryIdentity = StorageKeyBuilder.BuildUrlRepositoryIdentity(pathInfo);
        var repositoryIndexObjectKey = StorageKeyBuilder.BuildUrlRepositoryIndexObjectKey(pathInfo);
        var localPath = BuildLocalPathFromPrefix(repositoryPrefix);

        return await SyncRepositorySnapshotAsync(
            mode: RepositoryJobModes.Url,
            repositoryUrl: repository.Url,
            repositoryIdentity,
            repositoryPrefix,
            repositoryIndexObjectKey,
            localPath,
            gitCredential,
            force: false,
            includeLfs: repository.Lfs == true,
            objectStorageService,
            knownIndexKeys,
            cancellationToken);
    }

    private async Task<bool> SyncRepositorySnapshotAsync(
        string mode,
        string repositoryUrl,
        string repositoryIdentity,
        string repositoryPrefix,
        string repositoryIndexObjectKey,
        string localPath,
        GitCredential? credential,
        bool force,
        bool includeLfs,
        IObjectStorageService objectStorageService,
        HashSet<string> knownIndexKeys,
        CancellationToken cancellationToken)
    {
        var repositoryIndexContent = await objectStorageService.GetTextIfExistsAsync(repositoryIndexObjectKey, cancellationToken);
        var repositoryIndexDocument = ParseOrCreateRepositoryIndex(repositoryIndexContent, mode, repositoryIdentity);

        AppLogger.Info(
            "Repository sync started. mode={Mode}, repository={RepositoryUrl}",
            mode,
            repositoryUrl);
        AppLogger.Debug(
            "Repository working paths resolved. mode={Mode}, repository={RepositoryUrl}, localPath={LocalPath}, targetPrefix={TargetPrefix}.",
            mode,
            repositoryUrl,
            localPath,
            repositoryPrefix);

        await _gitRepositoryService.SyncBareRepositoryAsync(
            repositoryUrl,
            localPath,
            credential,
            force,
            includeLfs,
            cancellationToken);

        var timestamp = DateTimeOffset.UtcNow;
        var archiveObjectKey = $"{repositoryPrefix}/{BuildArchiveObjectName(timestamp)}";

        await objectStorageService.UploadDirectoryAsTarGzAsync(
            localPath,
            archiveObjectKey,
            cancellationToken);

        repositoryIndexDocument.Snapshots = repositoryIndexDocument.Snapshots
            .Where(IsValidSnapshot)
            .Where(snapshot => !string.Equals(snapshot.RootPrefix, archiveObjectKey, StringComparison.Ordinal))
            .ToList();
        repositoryIndexDocument.Snapshots.Add(new RepositorySnapshotDocument
        {
            RootPrefix = archiveObjectKey,
            TimestampUnixSeconds = timestamp.ToUnixTimeSeconds()
        });

        var updatedRepositoryIndexContent = StorageIndexDocuments.Serialize(repositoryIndexDocument);
        if (!string.Equals(repositoryIndexContent, updatedRepositoryIndexContent, StringComparison.Ordinal))
        {
            await objectStorageService.UploadTextAsync(
                repositoryIndexObjectKey,
                updatedRepositoryIndexContent,
                cancellationToken);
        }

        var indexAdded = knownIndexKeys.Add(repositoryIndexObjectKey);

        await objectStorageService.UploadTextAsync(
            $"{repositoryPrefix}/{RepositoryMarkerName}",
            $"mode={mode}\nrepository={repositoryUrl}\nupdatedAt={timestamp:O}",
            cancellationToken);

        AppLogger.Info(
            "Repository sync completed. mode={Mode}, repository={RepositoryUrl}, destination={RepositoryPrefix}.",
            mode,
            repositoryUrl,
            repositoryPrefix);

        return indexAdded;
    }

    private static RepositoryRegistryDocument ParseOrCreateRepositoryRegistry(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RepositoryRegistryDocument();
        }

        if (!StorageIndexDocuments.TryDeserialize<RepositoryRegistryDocument>(json, out var parsed) || parsed is null)
        {
            AppLogger.Warn("Repository index registry is invalid JSON. Rebuilding from discovered state.");
            return new RepositoryRegistryDocument();
        }

        parsed.IndexKeys = (parsed.IndexKeys ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim('/'))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return parsed;
    }

    private static RepositoryIndexDocument ParseOrCreateRepositoryIndex(string? json, string mode, string repositoryIdentity)
    {
        RepositoryIndexDocument document;

        if (string.IsNullOrWhiteSpace(json))
        {
            document = new RepositoryIndexDocument();
        }
        else if (!StorageIndexDocuments.TryDeserialize<RepositoryIndexDocument>(json, out var parsed) || parsed is null)
        {
            AppLogger.Warn(
                "Repository index is invalid JSON. Rebuilding index for repository={RepositoryIdentity}.",
                repositoryIdentity);
            document = new RepositoryIndexDocument();
        }
        else
        {
            document = parsed;
        }

        document.Mode = mode;
        document.RepositoryIdentity = repositoryIdentity;
        document.Snapshots ??= [];
        return document;
    }

    private static async Task<RepositoryRegistryDocument> LoadRepositoryRegistryAsync(
        IObjectStorageService objectStorageService,
        string repositoryRegistryObjectKey,
        CancellationToken cancellationToken)
    {
        var registryContent = await objectStorageService.GetTextIfExistsAsync(repositoryRegistryObjectKey, cancellationToken);
        return ParseOrCreateRepositoryRegistry(registryContent);
    }

    private string BuildLocalPathFromPrefix(string repositoryPrefix)
    {
        var localPath = _workingRoot;

        foreach (var segment in repositoryPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            localPath = Path.Combine(localPath, segment);
        }

        return localPath;
    }

    private static string ComputeDeterministicFolderName(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BuildArchiveObjectName(DateTimeOffset timestamp)
    {
        return $"{timestamp.ToUnixTimeSeconds()}{ArchiveObjectNameSuffix}";
    }

    private static bool IsValidSnapshot(RepositorySnapshotDocument snapshot)
    {
        return !string.IsNullOrWhiteSpace(snapshot.RootPrefix) && snapshot.TimestampUnixSeconds > 0;
    }
}
