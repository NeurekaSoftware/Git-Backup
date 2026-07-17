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

    /// <summary>The per-collection descriptors (labels, keys, and the map/serialize delegates).</summary>
    private sealed record CollectionSpec<T>(
        string Label,
        string SupportLabel,
        string CountLabel,
        bool Supported,
        Func<IAsyncEnumerable<T>> ListAsync,
        string CollectionPrefix,
        string ManifestObjectKey,
        Func<T, string> GetSlug,
        Func<string, string> BuildObjectKey,
        Func<string, string, string> BuildAttachmentObjectKey,
        Func<T, object> ToManifestEntry,
        bool IncludeArtifacts)
        where T : class, IBackedUpArtifactItem;

    /// <summary>The ambient dependencies shared by all three collections of one repository's run.</summary>
    private sealed record CollectionBackupContext(
        RepositoryJobConfig Repository,
        ProjectMetadataContext Context,
        IProjectMetadataProviderClient MetadataClient,
        CredentialConfig Credential,
        string RepositoryDisplay,
        IObjectStorageService ObjectStorageService);

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

        var backup = new CollectionBackupContext(repository, context, metadataClient, credential, repositoryDisplay, objectStorageService);

        if (repository.IncludeIssues == true)
        {
            await BackUpCollectionAsync(
                new CollectionSpec<BackedUpIssue>(
                    Label: "Issue",
                    SupportLabel: "issues",
                    CountLabel: "issues",
                    Supported: metadataClient.SupportsIssues,
                    ListAsync: () => metadataClient.ListIssuesAsync(context, credential, cancellationToken),
                    CollectionPrefix: StorageKeyBuilder.BuildIssuesCollectionPrefix(repositoryPrefix),
                    ManifestObjectKey: StorageKeyBuilder.BuildIssuesManifestObjectKey(repositoryPrefix),
                    GetSlug: issue => issue.Number.ToString(),
                    BuildObjectKey: slug => StorageKeyBuilder.BuildIssueObjectKey(repositoryPrefix, slug),
                    BuildAttachmentObjectKey: (slug, fileName) => StorageKeyBuilder.BuildIssueAttachmentObjectKey(repositoryPrefix, slug, fileName),
                    ToManifestEntry: issue => new CollectionManifestEntry { Number = issue.Number, Title = issue.Title, State = issue.State, UpdatedAt = issue.UpdatedAt },
                    IncludeArtifacts: repository.IncludeIssueArtifacts == true),
                backup,
                cancellationToken);
        }

        if (repository.IncludeMergeRequests == true)
        {
            await BackUpCollectionAsync(
                new CollectionSpec<BackedUpMergeRequest>(
                    Label: "Merge request",
                    SupportLabel: "merge requests",
                    CountLabel: "mergeRequests",
                    Supported: metadataClient.SupportsMergeRequests,
                    ListAsync: () => metadataClient.ListMergeRequestsAsync(context, credential, cancellationToken),
                    CollectionPrefix: StorageKeyBuilder.BuildMergeRequestsCollectionPrefix(repositoryPrefix),
                    ManifestObjectKey: StorageKeyBuilder.BuildMergeRequestsManifestObjectKey(repositoryPrefix),
                    GetSlug: mergeRequest => mergeRequest.Number.ToString(),
                    BuildObjectKey: slug => StorageKeyBuilder.BuildMergeRequestObjectKey(repositoryPrefix, slug),
                    BuildAttachmentObjectKey: (slug, fileName) => StorageKeyBuilder.BuildMergeRequestAttachmentObjectKey(repositoryPrefix, slug, fileName),
                    ToManifestEntry: mergeRequest => new CollectionManifestEntry { Number = mergeRequest.Number, Title = mergeRequest.Title, State = mergeRequest.State, UpdatedAt = mergeRequest.UpdatedAt },
                    IncludeArtifacts: repository.IncludeMergeRequestsArtifacts == true),
                backup,
                cancellationToken);
        }

        if (repository.IncludeReleases == true)
        {
            await BackUpCollectionAsync(
                new CollectionSpec<BackedUpRelease>(
                    Label: "Release",
                    SupportLabel: "releases",
                    CountLabel: "releases",
                    Supported: metadataClient.SupportsReleases,
                    ListAsync: () => metadataClient.ListReleasesAsync(context, credential, cancellationToken),
                    CollectionPrefix: StorageKeyBuilder.BuildReleasesCollectionPrefix(repositoryPrefix),
                    ManifestObjectKey: StorageKeyBuilder.BuildReleasesManifestObjectKey(repositoryPrefix),
                    GetSlug: release => ResolveReleaseSlug(release.Tag),
                    BuildObjectKey: slug => StorageKeyBuilder.BuildReleaseObjectKey(repositoryPrefix, slug),
                    BuildAttachmentObjectKey: (slug, fileName) => StorageKeyBuilder.BuildReleaseAttachmentObjectKey(repositoryPrefix, slug, fileName),
                    ToManifestEntry: release => new ReleaseManifestEntry { Tag = release.Tag, Name = release.Name, PublishedAt = release.PublishedAt },
                    IncludeArtifacts: repository.IncludeReleaseArtifacts == true),
                backup,
                cancellationToken);
        }
    }

    /// <summary>
    /// Backs up one metadata collection (issues, merge requests, or releases). A partial fetch never
    /// drives a reconciliation delete: a listing failure leaves the previous manifest untouched.
    /// </summary>
    private async Task BackUpCollectionAsync<T>(
        CollectionSpec<T> spec,
        CollectionBackupContext backup,
        CancellationToken cancellationToken)
        where T : class, IBackedUpArtifactItem
    {
        if (!spec.Supported)
        {
            AppLogger.Debug("Provider does not support {Collection}. provider={Provider}.", spec.SupportLabel, backup.Repository.Provider);
            return;
        }

        AppLogger.Info("{Collection} backup started. repository={Repository}.", spec.Label, backup.RepositoryDisplay);

        var backedUp = await SyncCollectionAsync(spec, backup, cancellationToken);

        AppLogger.Info("{Collection} backup completed. repository={Repository}, {CountName}={Count}.", spec.Label, backup.RepositoryDisplay, spec.CountLabel, backedUp);
    }

    private static async Task<int> SyncCollectionAsync<T>(
        CollectionSpec<T> spec,
        CollectionBackupContext backup,
        CancellationToken cancellationToken)
        where T : class, IBackedUpArtifactItem
    {
        var downloadArtifacts = spec.IncludeArtifacts && backup.MetadataClient.SupportsArtifacts;
        var anyItemFailed = 0;
        var backedUp = 0;
        var manifestEntries = new List<object>();
        var fetchedSlugs = new HashSet<string>(StringComparer.Ordinal);
        var accumulateLock = new object();
        var listingFailed = false;

        // Stream the collection so it is never held in memory all at once. Per-item work (attachment
        // download + document upload) overlaps up to the configured degree, and Parallel.ForEachAsync
        // applies backpressure to the source, so peak memory is one provider page plus the in-flight
        // items rather than every issue/MR with its full comment thread. A single item's failure is
        // logged and flagged (not thrown) so it never discards the rest of the run.
        try
        {
            await Parallel.ForEachAsync(
                spec.ListAsync(),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, backup.Context.Concurrency), CancellationToken = cancellationToken },
                async (item, token) =>
                {
                    var slug = spec.GetSlug(item);

                    try
                    {
                        if (downloadArtifacts)
                        {
                            await DownloadAttachmentsAsync(item, slug, spec.BuildAttachmentObjectKey, backup, token);
                        }
                        else
                        {
                            // Artifacts are gated off, so record no attachment references at all.
                            item.Attachments = [];
                        }

                        await backup.ObjectStorageService.UploadTextAsync(
                            spec.BuildObjectKey(slug),
                            StorageMetadataDocuments.Serialize(item),
                            token);

                        var entry = spec.ToManifestEntry(item);
                        lock (accumulateLock)
                        {
                            manifestEntries.Add(entry);
                            fetchedSlugs.Add(slug);
                            backedUp++;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        Interlocked.Exchange(ref anyItemFailed, 1);
                        lock (accumulateLock)
                        {
                            // Record the slug even on failure so a reconcile can never treat this
                            // still-present item as removed (reconcile is skipped this run regardless).
                            fetchedSlugs.Add(slug);
                        }

                        AppLogger.Error(
                            exception,
                            "{Collection} item backup failed. repository={Repository}, slug={Slug}, error={ErrorMessage}.",
                            spec.Label,
                            backup.RepositoryDisplay,
                            slug,
                            exception.Message);
                    }
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Listing/paging (or a per-item comment fetch) failed partway through the stream, so the set
            // is incomplete. Leave the previous manifest in place and delete nothing this run.
            listingFailed = true;
            AppLogger.Error(
                exception,
                "{Collection} backup failed while listing. repository={Repository}, error={ErrorMessage}.",
                spec.Label,
                backup.RepositoryDisplay,
                exception.Message);
        }

        if (listingFailed)
        {
            return backedUp;
        }

        await backup.ObjectStorageService.UploadTextAsync(
            spec.ManifestObjectKey,
            StorageMetadataDocuments.Serialize(manifestEntries),
            cancellationToken);

        // Reconciliation deletes stored documents the provider no longer returns. Skip it when any item
        // failed this run so an item whose document did not upload is never mistaken for one removed
        // upstream and deleted — a partial set must not drive deletes.
        if (anyItemFailed == 0)
        {
            await ReconcileAsync(fetchedSlugs, spec.CollectionPrefix, spec.ManifestObjectKey, backup.ObjectStorageService, cancellationToken);
        }
        else
        {
            AppLogger.Warn(
                "{Collection} reconciliation skipped because one or more items failed to back up. repository={Repository}.",
                spec.Label,
                backup.RepositoryDisplay);
        }

        return backedUp;
    }

    private static async Task DownloadAttachmentsAsync(
        IBackedUpArtifactItem item,
        string slug,
        Func<string, string, string> buildAttachmentObjectKey,
        CollectionBackupContext backup,
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

            // Bound how many attachments are downloaded at once across the whole run. A cancellation
            // while waiting propagates before the try, so no release runs.
            var throttle = backup.Context.DownloadThrottle;
            if (throttle is not null)
            {
                await throttle.WaitAsync(cancellationToken);
            }

            try
            {
                await using var stream = await backup.MetadataClient.OpenAttachmentAsync(backup.Context, backup.Credential, attachment.DownloadUrl, cancellationToken);
                var objectKey = buildAttachmentObjectKey(slug, attachment.FileName);
                var contentType = MimeTypeResolver.ResolveFromFileName(attachment.FileName);

                var knownLength = AttachmentDownloader.TryGetKnownLength(stream);

                await backup.ObjectStorageService.UploadStreamAsync(objectKey, stream, contentType, knownLength, cancellationToken);

                attachment.StorageKey = objectKey;
                attachment.ContentType = contentType;
                // Prefer the length the server declared; fall back to the provider-reported size (set for
                // release assets). The stream itself cannot answer — it is not seekable.
                attachment.SizeBytes = knownLength ?? attachment.SizeBytes;
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
    /// just wrote is left in place. <paramref name="fetchedSlugs"/> is the set of slugs seen this run.
    /// </summary>
    private static async Task ReconcileAsync(
        HashSet<string> fetchedSlugs,
        string collectionPrefix,
        string manifestObjectKey,
        IObjectStorageService objectStorageService,
        CancellationToken cancellationToken)
    {
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
