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
    /// True when the URI's scheme is http or https. The single predicate behind every "is this a
    /// usable web URL" check — settings validation, the git transport allowlist, the storage-key
    /// parser, and the attachment SSRF guard — so the accepted scheme set can never drift between
    /// those security-relevant choke points.
    /// </summary>
    public static bool IsHttpOrHttps(Uri uri)
    {
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses <paramref name="value"/> as an absolute http/https URL. Returns false (and a null
    /// <paramref name="uri"/> the caller must not read) for anything else.
    /// </summary>
    public static bool TryCreateHttpUrl(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && IsHttpOrHttps(parsed))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
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
