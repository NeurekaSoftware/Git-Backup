using System.Security.Cryptography;
using System.Text;
using GitBackup.Runtime;

namespace GitBackup.Services.Repositories;

/// <summary>
/// Manages the on-disk git mirrors under <c>{workingRoot}/repositories</c>. Each repository maps to
/// a single flat directory named by a deterministic hash of its storage prefix, so cleaning up
/// mirrors for repositories that are no longer backed up is a simple set difference.
/// </summary>
internal sealed class LocalMirrorStore
{
    private readonly string _mirrorsRoot;

    public LocalMirrorStore(string workingRoot)
    {
        _mirrorsRoot = Path.Combine(workingRoot, "repositories");
    }

    public string GetMirrorPath(string repositoryPrefix)
    {
        return Path.Combine(_mirrorsRoot, GetMirrorDirectoryName(repositoryPrefix));
    }

    public static string GetMirrorDirectoryName(string repositoryPrefix)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(repositoryPrefix));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public void TryDeleteMirror(string repositoryPrefix)
    {
        TryDeleteDirectory(GetMirrorPath(repositoryPrefix));
    }

    /// <summary>
    /// Deletes any mirror directory that is not in <paramref name="expectedDirectoryNames"/> — i.e.
    /// belongs to a repository that is no longer being backed up. Callers must only pass a complete
    /// expected set (see <see cref="RepositorySyncService"/>), otherwise a transient discovery error
    /// could remove a valid mirror.
    /// </summary>
    public void RemoveOrphans(IReadOnlySet<string> expectedDirectoryNames)
    {
        if (!Directory.Exists(_mirrorsRoot))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(_mirrorsRoot))
        {
            if (expectedDirectoryNames.Contains(Path.GetFileName(directory)))
            {
                continue;
            }

            if (TryDeleteDirectory(directory))
            {
                AppLogger.Info("Removed local mirror for a repository that is no longer backed up. path={Path}.", directory);
            }
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warn("Failed to remove local mirror directory. path={Path}, error={ErrorMessage}.", path, exception.Message);
        }

        return false;
    }
}
