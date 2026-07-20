using GitBackup.Configuration;
using GitBackup.Configuration.Models;
using GitBackup.Runtime;
using GitBackup.Services.Paths;
using GitBackup.Services.Storage;

namespace GitBackup.Services.Repositories;

public sealed class RepositoryRetentionService
{
    private readonly Func<StorageConfig, IObjectStorageService> _objectStorageServiceFactory;
    private bool _retentionMinimumZeroWarningShown;

    public RepositoryRetentionService(Func<StorageConfig, IObjectStorageService> objectStorageServiceFactory)
    {
        _objectStorageServiceFactory = objectStorageServiceFactory;
    }

    public async Task RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var retentionDays = settings.Storage.Retention;
        var retentionMinimum = Math.Max(0, settings.Storage.RetentionMinimum ?? 1);
        if (retentionDays is null || retentionDays <= 0)
        {
            AppLogger.Info("Retention is disabled. Repository snapshots will be kept indefinitely.");
            return;
        }

        if (retentionMinimum == 0)
        {
            if (!_retentionMinimumZeroWarningShown)
            {
                AppLogger.Warn(
                    "Retention minimum is set to 0. Repository snapshots can be deleted after the retention window, including repositories removed from configuration or whose URL changed.");
                _retentionMinimumZeroWarningShown = true;
            }
        }
        else
        {
            _retentionMinimumZeroWarningShown = false;
        }

        AppLogger.Info(
            "Retention run started. retentionDays={RetentionDays}, retentionMinimum={RetentionMinimum}.",
            retentionDays,
            retentionMinimum);

        using var objectStorageService = _objectStorageServiceFactory(settings.Storage);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays.Value);
        AppLogger.Info("Retention cutoff resolved. cutoff={CutoffTimestamp}.", AppLogger.FormatTimestamp(cutoff));

        var result = await ApplyRepositoryRetentionAsync(objectStorageService, cutoff, retentionMinimum, cancellationToken);

        AppLogger.Info(
            "Retention run completed. deletedSnapshots={DeletedSnapshots}, deletedOrphanObjects={DeletedOrphanObjects}, emptiedRepositories={EmptiedRepositories}.",
            result.DeletedSnapshots,
            result.DeletedOrphanObjects,
            result.EmptiedRepositories);
    }

    /// <summary>
    /// Prunes expired snapshots using the object listing as the source of truth: every archive
    /// object encodes its own timestamp in its key, so retention needs no side index. Snapshots
    /// are grouped by repository prefix, the newest <paramref name="retentionMinimum"/> are always
    /// protected, and anything older than <paramref name="cutoff"/> beyond that is deleted in a
    /// single batched call. When a repository loses all of its snapshots, its remaining non-archive
    /// objects (the advisory metadata.json and any issues/, merge-requests/, and releases/ documents
    /// and attachments) are removed too so no orphaned prefix is left behind.
    /// </summary>
    private static async Task<(int DeletedSnapshots, int DeletedOrphanObjects, int EmptiedRepositories)> ApplyRepositoryRetentionAsync(
        IObjectStorageService objectStorageService,
        DateTimeOffset cutoff,
        int retentionMinimum,
        CancellationToken cancellationToken)
    {
        // Snapshots live under two roots: repositories/ (repos and owned project snippets) and
        // snippets/ (gists and personal snippets). Retention treats both the same way. The two roots
        // are independent subtrees, so list them concurrently (matching the concurrent-walk pattern
        // used for provider discovery) instead of one full paginated walk after the other.
        var repositoryKeysTask = objectStorageService.ListObjectKeysAsync(StorageKeyBuilder.RepositoriesPrefix, cancellationToken);
        var snippetKeysTask = objectStorageService.ListObjectKeysAsync(StorageKeyBuilder.SnippetsPrefix, cancellationToken);
        await Task.WhenAll(repositoryKeysTask, snippetKeysTask);
        var allKeys = (await repositoryKeysTask).Concat(await snippetKeysTask).ToList();

        // Classify every key exactly once here: archive keys feed the retention grouping below, and
        // everything else is a candidate orphan collected in nonArchiveKeys — so the orphan pass can
        // filter that list rather than re-parsing every key with TryGetArchiveTimestamp a second time.
        var snapshotsByRepository = new Dictionary<string, List<(string ObjectKey, long TimestampUnixSeconds)>>(StringComparer.Ordinal);
        var nonArchiveKeys = new List<string>();
        foreach (var objectKey in allKeys)
        {
            if (!StorageKeyBuilder.TryGetArchiveTimestamp(objectKey, out var timestampUnixSeconds))
            {
                nonArchiveKeys.Add(objectKey);
                continue;
            }

            var repositoryPrefix = StorageKeyBuilder.GetParentPrefix(objectKey);
            if (!snapshotsByRepository.TryGetValue(repositoryPrefix, out var snapshots))
            {
                snapshots = [];
                snapshotsByRepository[repositoryPrefix] = snapshots;
            }

            snapshots.Add((objectKey, timestampUnixSeconds));
        }

        var expiredKeys = new List<string>();
        var emptiedRepositories = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (repositoryPrefix, snapshots) in snapshotsByRepository)
        {
            var ordered = snapshots
                .OrderByDescending(snapshot => snapshot.TimestampUnixSeconds)
                .ToList();

            var protectedCount = Math.Min(retentionMinimum, ordered.Count);
            var expired = ordered
                .Skip(protectedCount)
                .Where(snapshot => DateTimeOffset.FromUnixTimeSeconds(snapshot.TimestampUnixSeconds) < cutoff)
                .ToList();

            expiredKeys.AddRange(expired.Select(snapshot => snapshot.ObjectKey));

            if (expired.Count == ordered.Count)
            {
                emptiedRepositories.Add(repositoryPrefix);
            }
        }

        // Non-archive objects belonging to an emptied repository: the advisory metadata.json plus the
        // issues/ and merge-requests/ subtrees (documents and attachments). Project snippets nest
        // under {prefix}/snippets/{id} and are independent repositories with their own snapshots and
        // retention, so they are intentionally left untouched here.
        //
        // Precompute the deep collection prefixes for each emptied repository once, rather than
        // rebuilding three interpolated strings per object key scanned. Held as a set so a key can probe
        // it directly instead of every key scanning every prefix.
        var reclaimableCollectionPrefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repositoryPrefix in emptiedRepositories)
        {
            reclaimableCollectionPrefixes.Add($"{repositoryPrefix}/{StorageKeyBuilder.IssuesCollectionSegment}");
            reclaimableCollectionPrefixes.Add($"{repositoryPrefix}/{StorageKeyBuilder.MergeRequestsCollectionSegment}");
            reclaimableCollectionPrefixes.Add($"{repositoryPrefix}/{StorageKeyBuilder.ReleasesCollectionSegment}");
        }

        var orphanKeys = nonArchiveKeys
            .Where(objectKey => IsReclaimableOrphan(objectKey, emptiedRepositories, reclaimableCollectionPrefixes))
            .ToList();

        var keysToDelete = expiredKeys.Concat(orphanKeys).ToList();
        if (keysToDelete.Count > 0)
        {
            await objectStorageService.DeleteObjectsAsync(keysToDelete, cancellationToken);
        }

        return (expiredKeys.Count, orphanKeys.Count, emptiedRepositories.Count);
    }

    private static bool IsReclaimableOrphan(
        string objectKey,
        HashSet<string> emptiedRepositories,
        HashSet<string> reclaimableCollectionPrefixes)
    {
        // The advisory metadata.json is a direct child of the repository prefix.
        if (emptiedRepositories.Contains(StorageKeyBuilder.GetParentPrefix(objectKey)))
        {
            return true;
        }

        // Issue/merge-request/release documents and their attachments nest deeper under the prefix. Walk
        // this key's own ancestor prefixes — bounded by its segment count — and probe each one, rather
        // than testing every emptied repository's prefixes against every key. Both of those grow with
        // the bucket, so scanning one against the other degrades when a batch of repositories expires
        // together; this does not.
        for (var i = objectKey.IndexOf('/'); i > 0; i = objectKey.IndexOf('/', i + 1))
        {
            if (reclaimableCollectionPrefixes.Contains(objectKey[..i]))
            {
                return true;
            }
        }

        return false;
    }
}
