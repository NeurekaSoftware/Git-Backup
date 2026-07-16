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
        AppLogger.Info("Repository run started. enabledJobs={EnabledJobCount}.", enabledRepositories.Length);

        using var objectStorageService = _objectStorageServiceFactory(settings.Storage);

        AppLogger.Debug(
            "Repository storage target configured. endpoint={Endpoint}, bucket={Bucket}, region={Region}.",
            settings.Storage.Endpoint,
            settings.Storage.Bucket,
            settings.Storage.Region);

        var syncedRepositories = 0;

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
                    syncedRepositories += await RunProviderModeAsync(settings, repository, objectStorageService, cancellationToken);
                }
                else if (string.Equals(repository.Mode, RepositoryJobModes.Url, StringComparison.OrdinalIgnoreCase))
                {
                    syncedRepositories += await RunUrlModeAsync(settings, repository, objectStorageService, cancellationToken);
                }
                else
                {
                    AppLogger.Warn("Skipping repository job because mode is invalid. mode={Mode}.", repository.Mode);
                }
            }
            catch (Exception exception)
            {
                AppLogger.Error(
                    exception,
                    "Repository job failed. mode={Mode}, error={ErrorMessage}.",
                    repository.Mode,
                    exception.Message);
            }
        }

        AppLogger.Info("Repository run completed. syncedRepositories={SyncedRepositoryCount}.", syncedRepositories);
    }

    private async Task<int> RunProviderModeAsync(
        Settings settings,
        RepositoryJobConfig repository,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repository.Provider) || string.IsNullOrWhiteSpace(repository.Credential))
        {
            AppLogger.Warn("Skipping provider repository job because provider or credential is missing.");
            return 0;
        }

        if (!settings.Credentials.TryGetValue(repository.Credential, out var credentialConfig))
        {
            AppLogger.Warn(
                "Skipping provider repository job because credential is missing. provider={Provider}, credential={Credential}.",
                repository.Provider,
                repository.Credential);
            return 0;
        }

        AppLogger.Info("Provider repository discovery started. provider={Provider}.", repository.Provider);
        var providerClient = _providerFactory.Resolve(repository.Provider);
        var discoveredRepositories = await providerClient.ListOwnedRepositoriesAsync(repository, credentialConfig, cancellationToken);
        AppLogger.Info(
            "Provider repository discovery completed. provider={Provider}, repositories={RepositoryCount}.",
            repository.Provider,
            discoveredRepositories.Count);

        var gitCredential = CredentialResolver.ResolveGitCredential(credentialConfig);
        var syncedRepositories = 0;

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
                var localPath = Path.Combine(
                    _workingRoot,
                    "repositories",
                    RepositoryJobModes.Provider,
                    ComputeDeterministicFolderName($"{repository.Provider}:{discoveredRepository.CloneUrl}"));

                await SyncRepositorySnapshotAsync(
                    mode: RepositoryJobModes.Provider,
                    repositoryUrl: discoveredRepository.CloneUrl,
                    repositoryPrefix,
                    localPath,
                    gitCredential,
                    force: true,
                    includeLfs: repository.Lfs == true,
                    objectStorageService,
                    cancellationToken);

                syncedRepositories++;
            }
            catch (Exception exception)
            {
                AppLogger.Error(
                    exception,
                    "Provider repository sync failed. provider={Provider}, repository={RepositoryUrl}, error={ErrorMessage}.",
                    repository.Provider,
                    discoveredRepository.CloneUrl,
                    exception.Message);
            }
        }

        return syncedRepositories;
    }

    private async Task<int> RunUrlModeAsync(
        Settings settings,
        RepositoryJobConfig repository,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repository.Url))
        {
            AppLogger.Warn("Skipping URL repository job because url is missing.");
            return 0;
        }

        GitCredential? gitCredential = null;
        if (!string.IsNullOrWhiteSpace(repository.Credential))
        {
            if (!settings.Credentials.TryGetValue(repository.Credential, out var credentialConfig))
            {
                AppLogger.Warn(
                    "Skipping URL repository job because credential is missing. repository={RepositoryUrl}, credential={Credential}.",
                    repository.Url,
                    repository.Credential);
                return 0;
            }

            gitCredential = CredentialResolver.ResolveGitCredential(credentialConfig);
        }

        var pathInfo = RepositoryPathParser.Parse(repository.Url);
        var repositoryPrefix = StorageKeyBuilder.BuildUrlRepositoryPrefix(pathInfo);
        var localPath = BuildLocalPathFromPrefix(repositoryPrefix);

        await SyncRepositorySnapshotAsync(
            mode: RepositoryJobModes.Url,
            repositoryUrl: repository.Url,
            repositoryPrefix,
            localPath,
            gitCredential,
            force: false,
            includeLfs: repository.Lfs == true,
            objectStorageService,
            cancellationToken);

        return 1;
    }

    private async Task SyncRepositorySnapshotAsync(
        string mode,
        string repositoryUrl,
        string repositoryPrefix,
        string localPath,
        GitCredential? credential,
        bool force,
        bool includeLfs,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
    {
        AppLogger.Info("Repository sync started. mode={Mode}, repository={RepositoryUrl}.", mode, repositoryUrl);
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
        var archiveObjectKey = StorageKeyBuilder.BuildArchiveObjectKey(repositoryPrefix, timestamp.ToUnixTimeSeconds());

        await objectStorageService.UploadDirectoryAsTarGzAsync(localPath, archiveObjectKey, cancellationToken);

        var metadataDocument = new RepositoryMetadataDocument
        {
            Mode = mode,
            RepositoryUrl = repositoryUrl,
            UpdatedAtUnixSeconds = timestamp.ToUnixTimeSeconds()
        };
        await objectStorageService.UploadTextAsync(
            StorageKeyBuilder.BuildRepositoryMetadataObjectKey(repositoryPrefix),
            StorageMetadataDocuments.Serialize(metadataDocument),
            cancellationToken);

        AppLogger.Info(
            "Repository sync completed. mode={Mode}, repository={RepositoryUrl}, destination={RepositoryPrefix}.",
            mode,
            repositoryUrl,
            repositoryPrefix);
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
}
