using GitBackup.Configuration.Models;
using GitBackup.Runtime;
using GitBackup.Services.Paths;
using GitBackup.Services.Providers;
using GitBackup.Services.Storage;

namespace GitBackup.Services.Repositories;

/// <summary>
/// Backs up a single owned repository's issues, merge requests, and releases (with embedded comments
/// where applicable and, when enabled, downloaded attachments/assets) as latest-state JSON documents
/// under the repository's storage prefix, then reconciles the stored set to match what the provider
/// returned. All three collections are handled by one generic code path keyed on a string slug: the
/// issue/MR number for those, the sanitized tag for releases.
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

        if (repository.IncludeReleases == true)
        {
            await BackUpReleasesAsync(repository, context, metadataClient, credential, repositoryPrefix, repositoryDisplay, objectStorageService, cancellationToken);
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
            AppLogger.Error(exception, "Issue backup failed. repository={Repository}, error={ErrorMessage}.", repositoryDisplay, exception.Message);
            return;
        }

        await SyncCollectionAsync(
            issues,
            StorageKeyBuilder.BuildIssuesCollectionPrefix(repositoryPrefix),
            StorageKeyBuilder.BuildIssuesManifestObjectKey(repositoryPrefix),
            issue => issue.Number.ToString(),
            slug => StorageKeyBuilder.BuildIssueObjectKey(repositoryPrefix, slug),
            (slug, fileName) => StorageKeyBuilder.BuildIssueAttachmentObjectKey(repositoryPrefix, slug, fileName),
            items => StorageMetadataDocuments.Serialize(items
                .Select(issue => new CollectionManifestEntry { Number = issue.Number, Title = issue.Title, State = issue.State, UpdatedAt = issue.UpdatedAt })
                .ToList()),
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
            AppLogger.Error(exception, "Merge request backup failed. repository={Repository}, error={ErrorMessage}.", repositoryDisplay, exception.Message);
            return;
        }

        await SyncCollectionAsync(
            mergeRequests,
            StorageKeyBuilder.BuildMergeRequestsCollectionPrefix(repositoryPrefix),
            StorageKeyBuilder.BuildMergeRequestsManifestObjectKey(repositoryPrefix),
            mergeRequest => mergeRequest.Number.ToString(),
            slug => StorageKeyBuilder.BuildMergeRequestObjectKey(repositoryPrefix, slug),
            (slug, fileName) => StorageKeyBuilder.BuildMergeRequestAttachmentObjectKey(repositoryPrefix, slug, fileName),
            items => StorageMetadataDocuments.Serialize(items
                .Select(mergeRequest => new CollectionManifestEntry { Number = mergeRequest.Number, Title = mergeRequest.Title, State = mergeRequest.State, UpdatedAt = mergeRequest.UpdatedAt })
                .ToList()),
            includeArtifacts: repository.IncludeMergeRequestsArtifacts == true,
            context,
            metadataClient,
            credential,
            objectStorageService,
            cancellationToken);

        AppLogger.Info("Merge request backup completed. repository={Repository}, mergeRequests={MergeRequestCount}.", repositoryDisplay, mergeRequests.Count);
    }

    private async Task BackUpReleasesAsync(
        RepositoryJobConfig repository,
        ProjectMetadataContext context,
        IProjectMetadataProviderClient metadataClient,
        CredentialConfig credential,
        string repositoryPrefix,
        string repositoryDisplay,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
    {
        if (!metadataClient.SupportsReleases)
        {
            AppLogger.Debug("Provider does not support releases. provider={Provider}.", repository.Provider);
            return;
        }

        AppLogger.Info("Release backup started. repository={Repository}.", repositoryDisplay);

        IReadOnlyList<BackedUpRelease> releases;
        try
        {
            releases = await metadataClient.ListReleasesAsync(context, credential, cancellationToken);
        }
        catch (Exception exception)
        {
            AppLogger.Error(exception, "Release backup failed. repository={Repository}, error={ErrorMessage}.", repositoryDisplay, exception.Message);
            return;
        }

        await SyncCollectionAsync(
            releases,
            StorageKeyBuilder.BuildReleasesCollectionPrefix(repositoryPrefix),
            StorageKeyBuilder.BuildReleasesManifestObjectKey(repositoryPrefix),
            release => ResolveReleaseSlug(release.Tag),
            slug => StorageKeyBuilder.BuildReleaseObjectKey(repositoryPrefix, slug),
            (slug, fileName) => StorageKeyBuilder.BuildReleaseAttachmentObjectKey(repositoryPrefix, slug, fileName),
            items => StorageMetadataDocuments.Serialize(items
                .Select(release => new ReleaseManifestEntry { Tag = release.Tag, Name = release.Name, PublishedAt = release.PublishedAt })
                .ToList()),
            includeArtifacts: repository.IncludeReleaseArtifacts == true,
            context,
            metadataClient,
            credential,
            objectStorageService,
            cancellationToken);

        AppLogger.Info("Release backup completed. repository={Repository}, releases={ReleaseCount}.", repositoryDisplay, releases.Count);
    }

    private static async Task SyncCollectionAsync<T>(
        IReadOnlyList<T> items,
        string collectionPrefix,
        string manifestObjectKey,
        Func<T, string> getSlug,
        Func<string, string> buildObjectKey,
        Func<string, string, string> buildAttachmentObjectKey,
        Func<IReadOnlyList<T>, string> serializeManifest,
        bool includeArtifacts,
        ProjectMetadataContext context,
        IProjectMetadataProviderClient metadataClient,
        CredentialConfig credential,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
        where T : class, IBackedUpArtifactItem
    {
        var downloadArtifacts = includeArtifacts && metadataClient.SupportsArtifacts;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slug = getSlug(item);

            if (downloadArtifacts)
            {
                await DownloadAttachmentsAsync(item, slug, buildAttachmentObjectKey, context, metadataClient, credential, objectStorageService, cancellationToken);
            }
            else
            {
                // Artifacts are gated off, so record no attachment references at all.
                item.Attachments = [];
            }

            await objectStorageService.UploadTextAsync(
                buildObjectKey(slug),
                StorageMetadataDocuments.Serialize(item),
                cancellationToken);
        }

        await objectStorageService.UploadTextAsync(manifestObjectKey, serializeManifest(items), cancellationToken);

        await ReconcileAsync(items, getSlug, collectionPrefix, manifestObjectKey, objectStorageService, cancellationToken);
    }

    private static async Task DownloadAttachmentsAsync(
        IBackedUpArtifactItem item,
        string slug,
        Func<string, string, string> buildAttachmentObjectKey,
        ProjectMetadataContext context,
        IProjectMetadataProviderClient metadataClient,
        CredentialConfig credential,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
    {
        foreach (var attachment in item.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Reference-only assets (e.g. a GitLab release link outside the instance) are recorded but
            // never fetched, so the credential is never sent to a third-party host.
            if (!attachment.Downloadable)
            {
                continue;
            }

            try
            {
                await using var stream = await metadataClient.OpenAttachmentAsync(context, credential, attachment.DownloadUrl, cancellationToken);
                var objectKey = buildAttachmentObjectKey(slug, attachment.FileName);
                var contentType = MimeTypeResolver.ResolveFromFileName(attachment.FileName);

                await objectStorageService.UploadStreamAsync(objectKey, stream, contentType, cancellationToken);

                attachment.StorageKey = objectKey;
                attachment.ContentType = contentType;
                attachment.SizeBytes = stream.CanSeek ? stream.Length : null;
            }
            catch (Exception exception)
            {
                // Keep the reference (with no StorageKey) so the document still records that the file
                // existed; one bad attachment must not fail the whole item.
                AppLogger.Error(exception, "Attachment backup failed. originalPath={OriginalPath}, error={ErrorMessage}.", attachment.OriginalPath, exception.Message);
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
        Func<T, string> getSlug,
        string collectionPrefix,
        string manifestObjectKey,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
        where T : IBackedUpArtifactItem
    {
        var fetchedSlugs = items.Select(getSlug).ToHashSet(StringComparer.Ordinal);
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
            if (TryGetReconcilableSlug(relativeKey, out var slug) && !fetchedSlugs.Contains(slug))
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
    /// Extracts the owning item slug from a collection-relative key: <c>{slug}.json</c> or
    /// <c>attachments/{slug}/...</c>. Returns false for the manifest and anything else, so those are
    /// never reconciled away.
    /// </summary>
    private static bool TryGetReconcilableSlug(string relativeKey, out string slug)
    {
        slug = string.Empty;

        var firstSlash = relativeKey.IndexOf('/');
        if (firstSlash < 0)
        {
            if (!relativeKey.EndsWith(".json", StringComparison.Ordinal))
            {
                return false;
            }

            slug = relativeKey[..^".json".Length];
            return slug.Length > 0;
        }

        if (!string.Equals(relativeKey[..firstSlash], StorageKeyBuilder.AttachmentsCollectionSegment, StringComparison.Ordinal))
        {
            return false;
        }

        var afterAttachments = relativeKey[(firstSlash + 1)..];
        var nextSlash = afterAttachments.IndexOf('/');
        slug = nextSlash < 0 ? afterAttachments : afterAttachments[..nextSlash];
        return slug.Length > 0;
    }

    /// <summary>
    /// Turns a release tag into a safe storage-key leaf. Guards the reserved manifest base name so a
    /// tag literally named "index" cannot collide with the collection's index.json.
    /// </summary>
    private static string ResolveReleaseSlug(string tag)
    {
        var slug = AttachmentDownloader.SanitizeFileName(tag);
        var reserved = Path.GetFileNameWithoutExtension(StorageKeyBuilder.CollectionManifestObjectName);
        if (string.Equals(slug, reserved, StringComparison.Ordinal))
        {
            slug = $"{slug}-{AttachmentDownloader.ShortHash(tag)}";
        }

        return slug;
    }
}
