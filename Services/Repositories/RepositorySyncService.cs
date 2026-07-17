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
    private readonly ProjectMetadataSyncService _projectMetadataSyncService;

    public RepositorySyncService(
        RepositoryProviderClientFactory providerFactory,
        IGitRepositoryService gitRepositoryService,
        Func<StorageConfig, IObjectStorageService> objectStorageServiceFactory,
        LocalMirrorStore mirrorStore,
        ProjectMetadataSyncService projectMetadataSyncService)
    {
        _providerFactory = providerFactory;
        _gitRepositoryService = gitRepositoryService;
        _objectStorageServiceFactory = objectStorageServiceFactory;
        _mirrorStore = mirrorStore;
        _projectMetadataSyncService = projectMetadataSyncService;
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

        var repositoryConcurrency = Math.Max(1, settings.Concurrency.Repositories ?? 1);
        var metadataConcurrency = Math.Max(1, settings.Concurrency.Metadata ?? 1);

        // Shared across every repository this run, so the peak number of attachments buffered in
        // memory is capped at metadataConcurrency rather than repositoryConcurrency x metadataConcurrency.
        using var metadataDownloadThrottle = new SemaphoreSlim(metadataConcurrency);

        foreach (var repository in enabledRepositories)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                    var (synced, complete) = await RunProviderModeAsync(settings, repository, objectStorageService, expectedMirrorDirectories, repositoryConcurrency, metadataConcurrency, metadataDownloadThrottle, cancellationToken);
                    syncedRepositories += synced;
                    pictureComplete &= complete;
                }
                else if (string.Equals(repository.Mode, RepositoryJobModes.Url, StringComparison.OrdinalIgnoreCase))
                {
                    var (synced, complete) = await RunUrlModeAsync(settings, repository, objectStorageService, expectedMirrorDirectories, repositoryConcurrency, cancellationToken);
                    syncedRepositories += synced;
                    pictureComplete &= complete;
                }
                else
                {
                    AppLogger.Warn("Skipping repository job because mode is invalid. mode={Mode}.", repository.Mode);
                    pictureComplete = false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
        int repositoryConcurrency,
        int metadataConcurrency,
        SemaphoreSlim metadataDownloadThrottle,
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

        await Parallel.ForEachAsync(
            discoveredRepositories,
            new ParallelOptions { MaxDegreeOfParallelism = repositoryConcurrency, CancellationToken = cancellationToken },
            async (discoveredRepository, token) =>
            {
                if (string.IsNullOrWhiteSpace(discoveredRepository.CloneUrl))
                {
                    return;
                }

                try
                {
                    var repositoryPrefix = ResolveProviderPrefix(repository.Provider, discoveredRepository);
                    lock (expectedMirrorDirectories)
                    {
                        expectedMirrorDirectories.Add(LocalMirrorStore.GetMirrorDirectoryName(repositoryPrefix));
                    }

                    await SyncRepositorySnapshotAsync(
                        mode: RepositoryJobModes.Provider,
                        repositoryUrl: discoveredRepository.CloneUrl,
                        repositoryPrefix,
                        cache,
                        includeLfs,
                        gitCredential,
                        objectStorageService,
                        token);

                    Interlocked.Increment(ref syncedRepositories);

                    if (ShouldBackUpProjectMetadata(repository, discoveredRepository))
                    {
                        await _projectMetadataSyncService.SyncAsync(
                            repository,
                            discoveredRepository,
                            repositoryPrefix,
                            credentialConfig,
                            objectStorageService,
                            metadataConcurrency,
                            metadataDownloadThrottle,
                            token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
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
            });

        return (syncedRepositories, true);
    }

    // Issues and merge requests are backed up only for owned repositories: never for starred ones
    // (even when includeStarred is set) and never for gists or snippets, which have no such data.
    private static bool ShouldBackUpProjectMetadata(RepositoryJobConfig repository, DiscoveredRepository discoveredRepository)
    {
        return (repository.IncludeIssues == true
                || repository.IncludeMergeRequests == true
                || repository.IncludeReleases == true)
            && discoveredRepository.Kind == DiscoveredRepositoryKind.Repository
            && !discoveredRepository.IsStarred;
    }

    // Resolves the storage prefix for a discovered resource. Repositories use their clone URL's
    // owner/repo hierarchy; gists and personal snippets have no such hierarchy and are keyed by id
    // under the snippets/ root; project snippets nest under their owning project.
    private static string ResolveProviderPrefix(string provider, DiscoveredRepository discoveredRepository)
    {
        switch (discoveredRepository.Kind)
        {
            case DiscoveredRepositoryKind.Gist:
                return StorageKeyBuilder.BuildSnippetResourcePrefix(provider, discoveredRepository.Identifier!);

            case DiscoveredRepositoryKind.Snippet when string.IsNullOrWhiteSpace(discoveredRepository.ParentUrl):
                return StorageKeyBuilder.BuildSnippetResourcePrefix(provider, discoveredRepository.Identifier!);

            case DiscoveredRepositoryKind.Snippet:
                var projectInfo = RepositoryPathParser.Parse(discoveredRepository.ParentUrl!);
                var projectPrefix = StorageKeyBuilder.BuildProviderRepositoryPrefix(provider, projectInfo);
                return StorageKeyBuilder.BuildNestedSnippetPrefix(projectPrefix, discoveredRepository.Identifier!);

            default:
                var pathInfo = RepositoryPathParser.Parse(discoveredRepository.CloneUrl);
                return StorageKeyBuilder.BuildProviderRepositoryPrefix(provider, pathInfo);
        }
    }

    private async Task<(int Synced, bool Complete)> RunUrlModeAsync(
        Settings settings,
        RepositoryJobConfig repository,
        IObjectStorageService objectStorageService,
        HashSet<string> expectedMirrorDirectories,
        int repositoryConcurrency,
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

        await Parallel.ForEachAsync(
            repository.Urls,
            new ParallelOptions { MaxDegreeOfParallelism = repositoryConcurrency, CancellationToken = cancellationToken },
            async (url, token) =>
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return;
                }

                try
                {
                    var pathInfo = RepositoryPathParser.Parse(url);
                    var repositoryPrefix = StorageKeyBuilder.BuildUrlRepositoryPrefix(pathInfo);
                    lock (expectedMirrorDirectories)
                    {
                        expectedMirrorDirectories.Add(LocalMirrorStore.GetMirrorDirectoryName(repositoryPrefix));
                    }

                    await SyncRepositorySnapshotAsync(
                        mode: RepositoryJobModes.Url,
                        repositoryUrl: url,
                        repositoryPrefix,
                        cache,
                        includeLfs,
                        gitCredential,
                        objectStorageService,
                        token);

                    Interlocked.Increment(ref syncedRepositories);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    AppLogger.Error(
                        exception,
                        "URL repository sync failed. repository={RepositoryUrl}, error={ErrorMessage}.",
                        url,
                        exception.Message);
                }
            });

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
