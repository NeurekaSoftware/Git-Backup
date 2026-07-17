namespace GitBackup.Services.Paths;

/// <summary>
/// Small shared helpers for working with git repository URLs and names.
/// </summary>
public static class GitRepositoryUrl
{
    /// <summary>
    /// Removes a trailing <c>.git</c> suffix (case-insensitive) from a repository URL or name.
    /// Returns the value unchanged when no such suffix is present.
    /// </summary>
    public static string TrimGitSuffix(string value)
    {
        return value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }
}
