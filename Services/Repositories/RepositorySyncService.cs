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
    private readonly LocalMirrorStore _mirrorStore;

    public RepositorySyncService(
        RepositoryProviderClientFactory providerFactory,
        IGitRepositoryService gitRepositoryService,
        Func<StorageConfig, IObjectStorageService> objectStorageServiceFactory,
        string workingRoot)
    {
        _providerFactory = providerFactory;
        _gitRepositoryService = gitRepositoryService;
        _objectStorageServiceFactory = objectStorageServiceFactory;
        _mirrorStore = new LocalMirrorStore(workingRoot);
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

        // Track every repository's mirror directory this run, plus whether the picture is complete.
        // Local-mirror cleanup only runs when complete, so a discovery error never deletes a valid
        // mirror.
        var expectedMirrorDirectories = new HashSet<string>(StringComparer.Ordinal);
        var pictureComplete = true;
        var syncedRepositories = 0;

        foreach (var repository in enabledRepositories)
        {
            if (repository is null)
            {
                AppLogger.Warn("Skipping repository job because the entry is missing.");
                pictureComplete = false;
                continue;
            }

            try
            {
                if (string.Equals(repository.Mode, RepositoryJobModes.Provider, StringComparison.OrdinalIgnoreCase))
                {
                    var (synced, complete) = await RunProviderModeAsync(settings, repository, objectStorageService, expectedMirrorDirectories, cancellationToken);
                    syncedRepositories += synced;
                    pictureComplete &= complete;
                }
                else if (string.Equals(repository.Mode, RepositoryJobModes.Url, StringComparison.OrdinalIgnoreCase))
                {
                    var (synced, complete) = await RunUrlModeAsync(settings, repository, objectStorageService, expectedMirrorDirectories, cancellationToken);
                    syncedRepositories += synced;
                    pictureComplete &= complete;
                }
                else
                {
                    AppLogger.Warn("Skipping repository job because mode is invalid. mode={Mode}.", repository.Mode);
                    pictureComplete = false;
                }
            }
            catch (Exception exception)
            {
                AppLogger.Error(
                    exception,
                    "Repository job failed. mode={Mode}, error={ErrorMessage}.",
                    repository.Mode,
                    exception.Message);
                pictureComplete = false;
            }
        }

        if (pictureComplete)
        {
            _mirrorStore.RemoveOrphans(expectedMirrorDirectories);
        }
        else
        {
            AppLogger.Info("Skipping local mirror cleanup because the repository set for this run is incomplete.");
        }

        AppLogger.Info("Repository run completed. syncedRepositories={SyncedRepositoryCount}.", syncedRepositories);
    }

    private async Task<(int Synced, bool Complete)> RunProviderModeAsync(
        Settings settings,
        RepositoryJobConfig repository,
        IObjectStorageService objectStorageService,
        HashSet<string> expectedMirrorDirectories,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repository.Provider) || string.IsNullOrWhiteSpace(repository.Credential))
        {
            AppLogger.Warn("Skipping provider repository job because provider or credential is missing.");
            return (0, false);
        }

        if (!settings.Credentials.TryGetValue(repository.Credential, out var credentialConfig))
        {
            AppLogger.Warn(
                "Skipping provider repository job because credential is missing. provider={Provider}, credential={Credential}.",
                repository.Provider,
                repository.Credential);
            return (0, false);
        }

        AppLogger.Info("Provider repository discovery started. provider={Provider}.", repository.Provider);
        var providerClient = _providerFactory.Resolve(repository.Provider);

        IReadOnlyList<DiscoveredRepository> discoveredRepositories;
        try
        {
            discoveredRepositories = await providerClient.ListRepositoriesAsync(repository, credentialConfig, cancellationToken);
        }
        catch (Exception exception)
        {
            // Discovery failed, so we cannot know the full set of repositories — signal an incomplete
            // picture so local-mirror cleanup is skipped this run.
            AppLogger.Error(
                exception,
                "Provider repository discovery failed. provider={Provider}, error={ErrorMessage}.",
                repository.Provider,
                exception.Message);
            return (0, false);
        }

        AppLogger.Info(
            "Provider repository discovery completed. provider={Provider}, repositories={RepositoryCount}.",
            repository.Provider,
            discoveredRepositories.Count);

        var gitCredential = CredentialResolver.ResolveGitCredential(credentialConfig);
        var cache = repository.Cache != false;
        var includeLfs = repository.Lfs != false;
        var syncedRepositories = 0;

        foreach (var discoveredRepository in discoveredRepositories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(discoveredRepository.CloneUrl))
            {
                continue;
            }

            var pathInfo = RepositoryPathParser.Parse(discoveredRepository.CloneUrl);
            var repositoryPrefix = StorageKeyBuilder.BuildProviderRepositoryPrefix(repository.Provider, pathInfo);
            expectedMirrorDirectories.Add(LocalMirrorStore.GetMirrorDirectoryName(repositoryPrefix));

            try
            {
                await SyncRepositorySnapshotAsync(
                    mode: RepositoryJobModes.Provider,
                    repositoryUrl: discoveredRepository.CloneUrl,
                    repositoryPrefix,
                    cache,
                    includeLfs,
                    gitCredential,
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

        return (syncedRepositories, true);
    }

    private async Task<(int Synced, bool Complete)> RunUrlModeAsync(
        Settings settings,
        RepositoryJobConfig repository,
        IObjectStorageService objectStorageService,
        HashSet<string> expectedMirrorDirectories,
        CancellationToken cancellationToken)
    {
        if (repository.Urls is not { Count: > 0 })
        {
            AppLogger.Warn("Skipping URL repository job because url is missing.");
            return (0, false);
        }

        GitCredential? gitCredential = null;
        if (!string.IsNullOrWhiteSpace(repository.Credential))
        {
            if (!settings.Credentials.TryGetValue(repository.Credential, out var credentialConfig))
            {
                AppLogger.Warn(
                    "Skipping URL repository job because credential is missing. credential={Credential}.",
                    repository.Credential);
                return (0, false);
            }

            gitCredential = CredentialResolver.ResolveGitCredential(credentialConfig);
        }

        var cache = repository.Cache != false;
        var includeLfs = repository.Lfs != false;
        var syncedRepositories = 0;

        foreach (var url in repository.Urls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            try
            {
                var pathInfo = RepositoryPathParser.Parse(url);
                var repositoryPrefix = StorageKeyBuilder.BuildUrlRepositoryPrefix(pathInfo);
                expectedMirrorDirectories.Add(LocalMirrorStore.GetMirrorDirectoryName(repositoryPrefix));

                await SyncRepositorySnapshotAsync(
                    mode: RepositoryJobModes.Url,
                    repositoryUrl: url,
                    repositoryPrefix,
                    cache,
                    includeLfs,
                    gitCredential,
                    objectStorageService,
                    cancellationToken);

                syncedRepositories++;
            }
            catch (Exception exception)
            {
                AppLogger.Error(
                    exception,
                    "URL repository sync failed. repository={RepositoryUrl}, error={ErrorMessage}.",
                    url,
                    exception.Message);
            }
        }

        // The URL set is fully known from config (no discovery step), so the mirror-cleanup picture
        // stays complete even when an individual URL fails, matching provider-mode semantics.
        return (syncedRepositories, true);
    }

    private async Task SyncRepositorySnapshotAsync(
        string mode,
        string repositoryUrl,
        string repositoryPrefix,
        bool cache,
        bool includeLfs,
        GitCredential? credential,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
    {
        var localPath = _mirrorStore.GetMirrorPath(repositoryPrefix);

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
            cache,
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

        // A non-cached mirror exists only to build this snapshot; now that the upload has succeeded,
        // delete it so only one repository's worth of disk is used at a time.
        if (!cache)
        {
            AppLogger.Debug("Removing local mirror after upload (cache disabled). repository={RepositoryUrl}.", repositoryUrl);
            _mirrorStore.TryDeleteMirror(repositoryPrefix);
        }

        AppLogger.Info(
            "Repository sync completed. mode={Mode}, repository={RepositoryUrl}, destination={RepositoryPrefix}.",
            mode,
            repositoryUrl,
            repositoryPrefix);
    }
}
