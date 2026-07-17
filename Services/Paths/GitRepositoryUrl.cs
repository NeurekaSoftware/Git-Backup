using System.Text.RegularExpressions;

namespace GitBackup.Services.Paths;

/// <summary>
/// Small shared helpers for working with git repository URLs and names.
/// </summary>
public static class GitRepositoryUrl
{
    /// <summary>
    /// Characters not allowed in a storage-key path segment or file name; everything else is replaced
    /// with '-'. Shared so segment and file-name normalization stay in sync.
    /// </summary>
    public static readonly Regex InvalidStorageSegmentCharacters = new("[^A-Za-z0-9._-]+", RegexOptions.Compiled);

    /// <summary>
    /// Removes a trailing <c>.git</c> suffix (case-insensitive) from a repository URL or name.
    /// Returns the value unchanged when no such suffix is present.
    /// </summary>
    public static string TrimGitSuffix(string value)
    {
        return value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }

    /// <summary>
    /// Splits a URL's absolute path into its non-empty, URL-unescaped segments.
    /// </summary>
    public static string[] SplitUnescapedSegments(Uri uri)
    {
        return uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
    }
}
