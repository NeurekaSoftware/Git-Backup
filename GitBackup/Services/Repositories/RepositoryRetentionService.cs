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
            "Retention run started. retentionDays={RetentionDays}, retentionMinimum={RetentionMinimum}",
            retentionDays,
            retentionMinimum);

        var objectStorageService = _objectStorageServiceFactory(settings.Storage);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays.Value);
        AppLogger.Info("Retention cutoff: {CutoffTimestamp}", AppLogger.FormatTimestamp(cutoff));

        var retentionResult = await ApplyRepositoryRetentionAsync(
            objectStorageService,
            cutoff,
            retentionMinimum,
            cancellationToken);

        AppLogger.Info(
            "Retention run completed. deletedSnapshots={DeletedSnapshots}, updatedIndexes={UpdatedIndexes}.",
            retentionResult.DeletedSnapshots,
            retentionResult.UpdatedIndexes);
    }

    private async Task<(int DeletedSnapshots, int UpdatedIndexes)> ApplyRepositoryRetentionAsync(
        IObjectStorageService objectStorageService,
        DateTimeOffset cutoff,
        int retentionMinimum,
        CancellationToken cancellationToken)
    {
        var deletedCount = 0;
        var updatedIndexCount = 0;

        var repositoryRegistryObjectKey = StorageKeyBuilder.BuildRepositoryRegistryObjectKey();
        var repositoryRegistryContent = await objectStorageService.GetTextIfExistsAsync(repositoryRegistryObjectKey, cancellationToken);
        var repositoryRegistry = ParseOrCreateRepositoryRegistry(repositoryRegistryContent);
        var knownIndexKeys = new HashSet<string>(repositoryRegistry.IndexKeys, StringComparer.Ordinal);
        var repositoryRegistryChanged = false;

        foreach (var repositoryIndexObjectKey in knownIndexKeys.ToArray())
        {
            var repositoryIndexContent = await objectStorageService.GetTextIfExistsAsync(repositoryIndexObjectKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(repositoryIndexContent))
            {
                repositoryRegistryChanged = true;
                knownIndexKeys.Remove(repositoryIndexObjectKey);
                AppLogger.Warn(
                    "Repository index is missing and will be removed from registry. indexObject={IndexObjectKey}",
                    repositoryIndexObjectKey);
                continue;
            }

            if (!StorageIndexDocuments.TryDeserialize<RepositoryIndexDocument>(repositoryIndexContent, out var repositoryIndex) ||
                repositoryIndex is null)
            {
                AppLogger.Warn(
                    "Repository index contains invalid JSON and will be skipped. indexObject={IndexObjectKey}",
                    repositoryIndexObjectKey);
                continue;
            }

            repositoryIndex.Snapshots ??= [];

            var normalizedSnapshots = repositoryIndex.Snapshots
                .Where(IsValidSnapshot)
                .GroupBy(snapshot => snapshot.RootPrefix, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(snapshot => snapshot.TimestampUnixSeconds).First())
                .OrderByDescending(snapshot => snapshot.TimestampUnixSeconds)
                .ToList();

            if (normalizedSnapshots.Count == 0)
            {
                repositoryRegistryChanged = true;
                knownIndexKeys.Remove(repositoryIndexObjectKey);
                AppLogger.Warn(
                    "Repository index has no valid snapshots and will be removed from registry. indexObject={IndexObjectKey}",
                    repositoryIndexObjectKey);
                continue;
            }

            var protectedSnapshotCount = Math.Min(retentionMinimum, normalizedSnapshots.Count);
            var expiredSnapshots = normalizedSnapshots
                .Skip(protectedSnapshotCount)
                .Where(snapshot => DateTimeOffset.FromUnixTimeSeconds(snapshot.TimestampUnixSeconds) < cutoff)
                .ToArray();

            foreach (var snapshot in expiredSnapshots)
            {
                await objectStorageService.DeleteObjectsAsync([snapshot.RootPrefix], cancellationToken);
                deletedCount++;
                AppLogger.Info("Deleted expired repository snapshot. target={SnapshotTarget}", snapshot.RootPrefix);
            }

            var retainedSnapshots = normalizedSnapshots
                .Where((snapshot, index) =>
                    index < protectedSnapshotCount ||
                    DateTimeOffset.FromUnixTimeSeconds(snapshot.TimestampUnixSeconds) >= cutoff)
                .OrderByDescending(snapshot => snapshot.TimestampUnixSeconds)
                .ToList();

            if (!RepositorySnapshotsAreEqual(repositoryIndex.Snapshots, retainedSnapshots))
            {
                repositoryIndex.Snapshots = retainedSnapshots;
                await objectStorageService.UploadTextAsync(
                    repositoryIndexObjectKey,
                    StorageIndexDocuments.Serialize(repositoryIndex),
                    cancellationToken);
                updatedIndexCount++;
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
            "Repository retention cleanup completed. deletedSnapshots={DeletedSnapshots}, updatedIndexes={UpdatedIndexes}.",
            deletedCount,
            updatedIndexCount);
        return (deletedCount, updatedIndexCount);
    }

    private static bool IsValidSnapshot(RepositorySnapshotDocument snapshot)
    {
        return !string.IsNullOrWhiteSpace(snapshot.RootPrefix) && snapshot.TimestampUnixSeconds > 0;
    }

    private static bool RepositorySnapshotsAreEqual(
        IReadOnlyList<RepositorySnapshotDocument> left,
        IReadOnlyList<RepositorySnapshotDocument> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            var leftSnapshot = left[i];
            var rightSnapshot = right[i];

            if (!string.Equals(leftSnapshot.RootPrefix, rightSnapshot.RootPrefix, StringComparison.Ordinal) ||
                leftSnapshot.TimestampUnixSeconds != rightSnapshot.TimestampUnixSeconds)
            {
                return false;
            }
        }

        return true;
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
}
