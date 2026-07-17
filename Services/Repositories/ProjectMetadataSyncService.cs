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
        int concurrency,
        SemaphoreSlim downloadThrottle,
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
            BaseUrl = repository.BaseUrl,
            Concurrency = Math.Max(1, concurrency),
            DownloadThrottle = downloadThrottle
        };

        if (repository.IncludeIssues == true)
        {
            await BackUpCollectionAsync(
                label: "Issue",
                supportLabel: "issues",
                countLabel: "issues",
                supported: metadataClient.SupportsIssues,
                listAsync: () => metadataClient.ListIssuesAsync(context, credential, cancellationToken),
                collectionPrefix: StorageKeyBuilder.BuildIssuesCollectionPrefix(repositoryPrefix),
                manifestObjectKey: StorageKeyBuilder.BuildIssuesManifestObjectKey(repositoryPrefix),
                getSlug: issue => issue.Number.ToString(),
                buildObjectKey: slug => StorageKeyBuilder.BuildIssueObjectKey(repositoryPrefix, slug),
                buildAttachmentObjectKey: (slug, fileName) => StorageKeyBuilder.BuildIssueAttachmentObjectKey(repositoryPrefix, slug, fileName),
                serializeManifest: items => StorageMetadataDocuments.Serialize(items
                    .Select(issue => new CollectionManifestEntry { Number = issue.Number, Title = issue.Title, State = issue.State, UpdatedAt = issue.UpdatedAt })
                    .ToList()),
                includeArtifacts: repository.IncludeIssueArtifacts == true,
                repository, context, metadataClient, credential, repositoryDisplay, objectStorageService, cancellationToken);
        }

        if (repository.IncludeMergeRequests == true)
        {
            await BackUpCollectionAsync(
                label: "Merge request",
                supportLabel: "merge requests",
                countLabel: "mergeRequests",
                supported: metadataClient.SupportsMergeRequests,
                listAsync: () => metadataClient.ListMergeRequestsAsync(context, credential, cancellationToken),
                collectionPrefix: StorageKeyBuilder.BuildMergeRequestsCollectionPrefix(repositoryPrefix),
                manifestObjectKey: StorageKeyBuilder.BuildMergeRequestsManifestObjectKey(repositoryPrefix),
                getSlug: mergeRequest => mergeRequest.Number.ToString(),
                buildObjectKey: slug => StorageKeyBuilder.BuildMergeRequestObjectKey(repositoryPrefix, slug),
                buildAttachmentObjectKey: (slug, fileName) => StorageKeyBuilder.BuildMergeRequestAttachmentObjectKey(repositoryPrefix, slug, fileName),
                serializeManifest: items => StorageMetadataDocuments.Serialize(items
                    .Select(mergeRequest => new CollectionManifestEntry { Number = mergeRequest.Number, Title = mergeRequest.Title, State = mergeRequest.State, UpdatedAt = mergeRequest.UpdatedAt })
                    .ToList()),
                includeArtifacts: repository.IncludeMergeRequestsArtifacts == true,
                repository, context, metadataClient, credential, repositoryDisplay, objectStorageService, cancellationToken);
        }

        if (repository.IncludeReleases == true)
        {
            await BackUpCollectionAsync(
                label: "Release",
                supportLabel: "releases",
                countLabel: "releases",
                supported: metadataClient.SupportsReleases,
                listAsync: () => metadataClient.ListReleasesAsync(context, credential, cancellationToken),
                collectionPrefix: StorageKeyBuilder.BuildReleasesCollectionPrefix(repositoryPrefix),
                manifestObjectKey: StorageKeyBuilder.BuildReleasesManifestObjectKey(repositoryPrefix),
                getSlug: release => ResolveReleaseSlug(release.Tag),
                buildObjectKey: slug => StorageKeyBuilder.BuildReleaseObjectKey(repositoryPrefix, slug),
                buildAttachmentObjectKey: (slug, fileName) => StorageKeyBuilder.BuildReleaseAttachmentObjectKey(repositoryPrefix, slug, fileName),
                serializeManifest: items => StorageMetadataDocuments.Serialize(items
                    .Select(release => new ReleaseManifestEntry { Tag = release.Tag, Name = release.Name, PublishedAt = release.PublishedAt })
                    .ToList()),
                includeArtifacts: repository.IncludeReleaseArtifacts == true,
                repository, context, metadataClient, credential, repositoryDisplay, objectStorageService, cancellationToken);
        }
    }

    /// <summary>
    /// Backs up one metadata collection (issues, merge requests, or releases). A failed list call is
    /// logged and skipped so a partial fetch never drives a reconciliation delete. <paramref
    /// name="label"/> names the collection in log lines; <paramref name="supportLabel"/> and <paramref
    /// name="countLabel"/> keep the "not supported" and "completed" messages reading naturally.
    /// </summary>
    private async Task BackUpCollectionAsync<T>(
        string label,
        string supportLabel,
        string countLabel,
        bool supported,
        Func<Task<IReadOnlyList<T>>> listAsync,
        string collectionPrefix,
        string manifestObjectKey,
        Func<T, string> getSlug,
        Func<string, string> buildObjectKey,
        Func<string, string, string> buildAttachmentObjectKey,
        Func<IReadOnlyList<T>, string> serializeManifest,
        bool includeArtifacts,
        RepositoryJobConfig repository,
        ProjectMetadataContext context,
        IProjectMetadataProviderClient metadataClient,
        CredentialConfig credential,
        string repositoryDisplay,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
        where T : class, IBackedUpArtifactItem
    {
        if (!supported)
        {
            AppLogger.Debug("Provider does not support {Collection}. provider={Provider}.", supportLabel, repository.Provider);
            return;
        }

        AppLogger.Info("{Collection} backup started. repository={Repository}.", label, repositoryDisplay);

        IReadOnlyList<T> items;
        try
        {
            items = await listAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A partial fetch must never drive reconciliation deletes, so skip the whole collection.
            AppLogger.Error(exception, "{Collection} backup failed. repository={Repository}, error={ErrorMessage}.", label, repositoryDisplay, exception.Message);
            return;
        }

        await SyncCollectionAsync(
            items,
            collectionPrefix,
            manifestObjectKey,
            getSlug,
            buildObjectKey,
            buildAttachmentObjectKey,
            serializeManifest,
            includeArtifacts,
            context,
            metadataClient,
            credential,
            objectStorageService,
            cancellationToken);

        AppLogger.Info("{Collection} backup completed. repository={Repository}, {CountName}={Count}.", label, repositoryDisplay, countLabel, items.Count);
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

        // Per-item work (attachment download + document upload) is independent, so it can overlap up
        // to the configured degree. The manifest and reconciliation below run only after every item
        // has been stored, preserving the "reconcile a complete set" guarantee.
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Concurrency), CancellationToken = cancellationToken },
            async (item, token) =>
            {
                var slug = getSlug(item);

                if (downloadArtifacts)
                {
                    await DownloadAttachmentsAsync(item, slug, buildAttachmentObjectKey, context, metadataClient, credential, objectStorageService, token);
                }
                else
                {
                    // Artifacts are gated off, so record no attachment references at all.
                    item.Attachments = [];
                }

                await objectStorageService.UploadTextAsync(
                    buildObjectKey(slug),
                    StorageMetadataDocuments.Serialize(item),
                    token);
            });

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

            // Bound how many attachments are downloaded (and buffered fully in memory) at once across
            // the whole run. A cancellation while waiting propagates before the try, so no release runs.
            var throttle = context.DownloadThrottle;
            if (throttle is not null)
            {
                await throttle.WaitAsync(cancellationToken);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Keep the reference (with no StorageKey) so the document still records that the file
                // existed; one bad attachment must not fail the whole item.
                AppLogger.Error(exception, "Attachment backup failed. originalPath={OriginalPath}, error={ErrorMessage}.", AttachmentDownloader.RedactUrl(attachment.OriginalPath), exception.Message);
            }
            finally
            {
                throttle?.Release();
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
