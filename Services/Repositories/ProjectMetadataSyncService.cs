using GitBackup.Configuration.Models;
using GitBackup.Runtime;
using GitBackup.Services.Paths;
using GitBackup.Services.Providers;
using GitBackup.Services.Storage;

namespace GitBackup.Services.Repositories;

/// <summary>
/// Backs up a single owned repository's issues and merge requests (with embedded comments and, when
/// enabled, downloaded attachments) as latest-state JSON documents under the repository's storage
/// prefix, then reconciles the stored set to match what the provider returned. Issues and merge
/// requests are handled by one generic code path via <see cref="IBackedUpProjectItem"/>.
/// </summary>
public sealed class ProjectMetadataSyncService
{
    private readonly RepositoryProviderClientFactory _providerFactory;

    public ProjectMetadataSyncService(RepositoryProviderClientFactory providerFactory)
    {
        _providerFactory = providerFactory;
    }

    public async Task SyncAsync(
        RepositoryJobConfig repository,
        DiscoveredRepository discoveredRepository,
        string repositoryPrefix,
        CredentialConfig credential,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
    {
        var metadataClient = _providerFactory.TryResolveMetadata(repository.Provider!);
        if (metadataClient is null)
        {
            AppLogger.Debug("Provider does not support project metadata backup. provider={Provider}.", repository.Provider);
            return;
        }

        var repositoryDisplay = discoveredRepository.WebUrl ?? discoveredRepository.CloneUrl;
        var context = new ProjectMetadataContext
        {
            CloneUrl = discoveredRepository.CloneUrl,
            WebUrl = discoveredRepository.WebUrl,
            ProviderProjectId = discoveredRepository.ProviderProjectId,
            BaseUrl = repository.BaseUrl
        };

        if (repository.IncludeIssues == true)
        {
            await BackUpIssuesAsync(repository, context, metadataClient, credential, repositoryPrefix, repositoryDisplay, objectStorageService, cancellationToken);
        }

        if (repository.IncludeMergeRequests == true)
        {
            await BackUpMergeRequestsAsync(repository, context, metadataClient, credential, repositoryPrefix, repositoryDisplay, objectStorageService, cancellationToken);
        }
    }

    private async Task BackUpIssuesAsync(
        RepositoryJobConfig repository,
        ProjectMetadataContext context,
        IProjectMetadataProviderClient metadataClient,
        CredentialConfig credential,
        string repositoryPrefix,
        string repositoryDisplay,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
    {
        if (!metadataClient.SupportsIssues)
        {
            AppLogger.Debug("Provider does not support issues. provider={Provider}.", repository.Provider);
            return;
        }

        AppLogger.Info("Issue backup started. repository={Repository}.", repositoryDisplay);

        IReadOnlyList<BackedUpIssue> issues;
        try
        {
            issues = await metadataClient.ListIssuesAsync(context, credential, cancellationToken);
        }
        catch (Exception exception)
        {
            // A partial fetch must never drive reconciliation deletes, so skip the whole collection.
            AppLogger.Error(
                exception,
                "Issue backup failed. repository={Repository}, error={ErrorMessage}.",
                repositoryDisplay,
                exception.Message);
            return;
        }

        await SyncCollectionAsync(
            issues,
            StorageKeyBuilder.BuildIssuesCollectionPrefix(repositoryPrefix),
            StorageKeyBuilder.BuildIssuesManifestObjectKey(repositoryPrefix),
            number => StorageKeyBuilder.BuildIssueObjectKey(repositoryPrefix, number),
            (number, fileName) => StorageKeyBuilder.BuildIssueAttachmentObjectKey(repositoryPrefix, number, fileName),
            includeArtifacts: repository.IncludeIssueArtifacts == true,
            context,
            metadataClient,
            credential,
            objectStorageService,
            cancellationToken);

        AppLogger.Info("Issue backup completed. repository={Repository}, issues={IssueCount}.", repositoryDisplay, issues.Count);
    }

    private async Task BackUpMergeRequestsAsync(
        RepositoryJobConfig repository,
        ProjectMetadataContext context,
        IProjectMetadataProviderClient metadataClient,
        CredentialConfig credential,
        string repositoryPrefix,
        string repositoryDisplay,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
    {
        if (!metadataClient.SupportsMergeRequests)
        {
            AppLogger.Debug("Provider does not support merge requests. provider={Provider}.", repository.Provider);
            return;
        }

        AppLogger.Info("Merge request backup started. repository={Repository}.", repositoryDisplay);

        IReadOnlyList<BackedUpMergeRequest> mergeRequests;
        try
        {
            mergeRequests = await metadataClient.ListMergeRequestsAsync(context, credential, cancellationToken);
        }
        catch (Exception exception)
        {
            AppLogger.Error(
                exception,
                "Merge request backup failed. repository={Repository}, error={ErrorMessage}.",
                repositoryDisplay,
                exception.Message);
            return;
        }

        await SyncCollectionAsync(
            mergeRequests,
            StorageKeyBuilder.BuildMergeRequestsCollectionPrefix(repositoryPrefix),
            StorageKeyBuilder.BuildMergeRequestsManifestObjectKey(repositoryPrefix),
            number => StorageKeyBuilder.BuildMergeRequestObjectKey(repositoryPrefix, number),
            (number, fileName) => StorageKeyBuilder.BuildMergeRequestAttachmentObjectKey(repositoryPrefix, number, fileName),
            includeArtifacts: repository.IncludeMergeRequestsArtifacts == true,
            context,
            metadataClient,
            credential,
            objectStorageService,
            cancellationToken);

        AppLogger.Info(
            "Merge request backup completed. repository={Repository}, mergeRequests={MergeRequestCount}.",
            repositoryDisplay,
            mergeRequests.Count);
    }

    private static async Task SyncCollectionAsync<T>(
        IReadOnlyList<T> items,
        string collectionPrefix,
        string manifestObjectKey,
        Func<long, string> buildObjectKey,
        Func<long, string, string> buildAttachmentObjectKey,
        bool includeArtifacts,
        ProjectMetadataContext context,
        IProjectMetadataProviderClient metadataClient,
        CredentialConfig credential,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
        where T : class, IBackedUpProjectItem
    {
        var downloadArtifacts = includeArtifacts && metadataClient.SupportsArtifacts;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (downloadArtifacts)
            {
                await DownloadAttachmentsAsync(item, buildAttachmentObjectKey, context, metadataClient, credential, objectStorageService, cancellationToken);
            }
            else
            {
                // Artifacts are gated off, so record no attachment references at all.
                item.Attachments = [];
            }

            await objectStorageService.UploadTextAsync(
                buildObjectKey(item.Number),
                StorageMetadataDocuments.Serialize(item),
                cancellationToken);
        }

        var manifest = items
            .Select(item => new CollectionManifestEntry
            {
                Number = item.Number,
                Title = item.Title,
                State = item.State,
                UpdatedAt = item.UpdatedAt
            })
            .ToList();
        await objectStorageService.UploadTextAsync(manifestObjectKey, StorageMetadataDocuments.Serialize(manifest), cancellationToken);

        await ReconcileAsync(items, collectionPrefix, manifestObjectKey, objectStorageService, cancellationToken);
    }

    private static async Task DownloadAttachmentsAsync(
        IBackedUpProjectItem item,
        Func<long, string, string> buildAttachmentObjectKey,
        ProjectMetadataContext context,
        IProjectMetadataProviderClient metadataClient,
        CredentialConfig credential,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
    {
        foreach (var attachment in item.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var stream = await metadataClient.OpenAttachmentAsync(context, credential, attachment.DownloadUrl, cancellationToken);
                var objectKey = buildAttachmentObjectKey(item.Number, attachment.FileName);
                var contentType = MimeTypeResolver.ResolveFromFileName(attachment.FileName);

                await objectStorageService.UploadStreamAsync(objectKey, stream, contentType, cancellationToken);

                attachment.StorageKey = objectKey;
                attachment.ContentType = contentType;
                attachment.SizeBytes = stream.CanSeek ? stream.Length : null;
            }
            catch (Exception exception)
            {
                // Keep the reference (with no StorageKey) so the document still records that the file
                // existed; one bad attachment must not fail the whole issue/MR.
                AppLogger.Error(
                    exception,
                    "Attachment backup failed. originalPath={OriginalPath}, error={ErrorMessage}.",
                    attachment.OriginalPath,
                    exception.Message);
            }
        }
    }

    /// <summary>
    /// Deletes stored documents (and attachment subtrees) for items the provider no longer returns,
    /// so the backup mirrors upstream. Runs only after a complete, successful fetch — the manifest we
    /// just wrote is left in place.
    /// </summary>
    private static async Task ReconcileAsync<T>(
        IReadOnlyList<T> items,
        string collectionPrefix,
        string manifestObjectKey,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
        where T : IBackedUpProjectItem
    {
        var fetchedNumbers = items.Select(item => item.Number).ToHashSet();
        var normalizedPrefix = StorageKeyBuilder.EnsurePrefix(collectionPrefix);
        var existingKeys = await objectStorageService.ListObjectKeysAsync(collectionPrefix, cancellationToken);

        var keysToDelete = new List<string>();
        foreach (var objectKey in existingKeys)
        {
            if (string.Equals(objectKey, manifestObjectKey, StringComparison.Ordinal) ||
                !objectKey.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relativeKey = objectKey[normalizedPrefix.Length..];
            if (TryGetReconcilableNumber(relativeKey, out var number) && !fetchedNumbers.Contains(number))
            {
                keysToDelete.Add(objectKey);
            }
        }

        if (keysToDelete.Count > 0)
        {
            AppLogger.Debug("Reconciling stored collection. removedObjects={RemovedObjectCount}.", keysToDelete.Count);
            await objectStorageService.DeleteObjectsAsync(keysToDelete, cancellationToken);
        }
    }

    /// <summary>
    /// Extracts the owning item number from a collection-relative key: <c>{number}.json</c> or
    /// <c>attachments/{number}/...</c>. Returns false for the manifest and anything else, so those
    /// are never reconciled away.
    /// </summary>
    private static bool TryGetReconcilableNumber(string relativeKey, out long number)
    {
        number = 0;

        var firstSlash = relativeKey.IndexOf('/');
        if (firstSlash < 0)
        {
            return relativeKey.EndsWith(".json", StringComparison.Ordinal)
                && long.TryParse(relativeKey[..^".json".Length], out number);
        }

        if (!string.Equals(relativeKey[..firstSlash], StorageKeyBuilder.AttachmentsCollectionSegment, StringComparison.Ordinal))
        {
            return false;
        }

        var afterAttachments = relativeKey[(firstSlash + 1)..];
        var nextSlash = afterAttachments.IndexOf('/');
        var numberSegment = nextSlash < 0 ? afterAttachments : afterAttachments[..nextSlash];
        return long.TryParse(numberSegment, out number);
    }
}
