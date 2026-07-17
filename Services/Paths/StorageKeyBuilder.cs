namespace GitBackup.Services.Paths;

public static class StorageKeyBuilder
{
    public const string ArchiveObjectNameSuffix = "_repo.tar.gz";
    public const string RepositoryMetadataObjectName = "metadata.json";
    public const string RepositoriesPrefix = "repositories/";
    public const string SnippetsPrefix = "snippets/";

    public const string IssuesCollectionSegment = "issues";
    public const string MergeRequestsCollectionSegment = "merge-requests";
    public const string ReleasesCollectionSegment = "releases";
    public const string AttachmentsCollectionSegment = "attachments";
    public const string CollectionManifestObjectName = "index.json";

    public static string BuildProviderRepositoryPrefix(string provider, RepositoryPathInfo repository)
    {
        var segments = new List<string>
        {
            "repositories",
            "provider",
            provider.Trim().ToLowerInvariant()
        };

        segments.AddRange(repository.Hierarchy);
        return string.Join('/', segments);
    }

    /// <summary>
    /// Builds the prefix for a standalone gist or personal snippet, which have no owner/repo
    /// hierarchy: <c>snippets/provider/{provider}/{identifier}</c>.
    /// </summary>
    public static string BuildSnippetResourcePrefix(string provider, string identifier)
    {
        var segments = new List<string>
        {
            "snippets",
            "provider",
            provider.Trim().ToLowerInvariant(),
            SanitizeIdentifier(identifier)
        };

        return string.Join('/', segments);
    }

    /// <summary>
    /// Builds the prefix for a project snippet nested under its owning repository:
    /// <c>{repositoryPrefix}/snippets/{identifier}</c>.
    /// </summary>
    public static string BuildNestedSnippetPrefix(string repositoryPrefix, string identifier)
    {
        return $"{repositoryPrefix.Trim('/')}/snippets/{SanitizeIdentifier(identifier)}";
    }

    // A snippet/gist id comes straight from the provider's JSON; a hostile self-hosted forge could
    // return one containing '/' (or '..') and steer this resource's objects under a different key
    // prefix. Strip anything outside the safe segment charset so a provider-supplied id can never
    // inject extra key segments, matching the normalization applied to repository path segments.
    private static string SanitizeIdentifier(string identifier)
    {
        var sanitized = GitRepositoryUrl.InvalidStorageSegmentCharacters.Replace(identifier.Trim(), "-").Trim('-', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    public static string BuildUrlRepositoryPrefix(RepositoryPathInfo repository)
    {
        var segments = new List<string>
        {
            "repositories",
            "url",
            repository.FullDomain
        };

        segments.AddRange(repository.Hierarchy);
        return string.Join('/', segments);
    }

    public static string BuildArchiveObjectKey(string repositoryPrefix, long timestampUnixSeconds)
    {
        return $"{repositoryPrefix.Trim('/')}/{timestampUnixSeconds}{ArchiveObjectNameSuffix}";
    }

    public static string BuildRepositoryMetadataObjectKey(string repositoryPrefix)
    {
        return $"{repositoryPrefix.Trim('/')}/{RepositoryMetadataObjectName}";
    }

    // Issues, merge requests, and releases are stored as latest-state JSON documents nested under
    // their repository prefix: {repositoryPrefix}/{collection}/{identifier}.json, each collection
    // with an index.json manifest and an attachments/{identifier}/ folder for downloaded files.
    // The identifier is the issue/MR number for those collections and the sanitized tag for releases.

    public static string BuildIssuesCollectionPrefix(string repositoryPrefix)
    {
        return BuildCollectionPrefix(repositoryPrefix, IssuesCollectionSegment);
    }

    public static string BuildIssueObjectKey(string repositoryPrefix, string identifier)
    {
        return BuildDocumentObjectKey(BuildIssuesCollectionPrefix(repositoryPrefix), identifier);
    }

    public static string BuildIssuesManifestObjectKey(string repositoryPrefix)
    {
        return BuildManifestObjectKey(BuildIssuesCollectionPrefix(repositoryPrefix));
    }

    public static string BuildIssueAttachmentObjectKey(string repositoryPrefix, string identifier, string fileName)
    {
        return BuildAttachmentObjectKey(BuildIssuesCollectionPrefix(repositoryPrefix), identifier, fileName);
    }

    public static string BuildMergeRequestsCollectionPrefix(string repositoryPrefix)
    {
        return BuildCollectionPrefix(repositoryPrefix, MergeRequestsCollectionSegment);
    }

    public static string BuildMergeRequestObjectKey(string repositoryPrefix, string identifier)
    {
        return BuildDocumentObjectKey(BuildMergeRequestsCollectionPrefix(repositoryPrefix), identifier);
    }

    public static string BuildMergeRequestsManifestObjectKey(string repositoryPrefix)
    {
        return BuildManifestObjectKey(BuildMergeRequestsCollectionPrefix(repositoryPrefix));
    }

    public static string BuildMergeRequestAttachmentObjectKey(string repositoryPrefix, string identifier, string fileName)
    {
        return BuildAttachmentObjectKey(BuildMergeRequestsCollectionPrefix(repositoryPrefix), identifier, fileName);
    }

    public static string BuildReleasesCollectionPrefix(string repositoryPrefix)
    {
        return BuildCollectionPrefix(repositoryPrefix, ReleasesCollectionSegment);
    }

    public static string BuildReleaseObjectKey(string repositoryPrefix, string identifier)
    {
        return BuildDocumentObjectKey(BuildReleasesCollectionPrefix(repositoryPrefix), identifier);
    }

    public static string BuildReleasesManifestObjectKey(string repositoryPrefix)
    {
        return BuildManifestObjectKey(BuildReleasesCollectionPrefix(repositoryPrefix));
    }

    public static string BuildReleaseAttachmentObjectKey(string repositoryPrefix, string identifier, string fileName)
    {
        return BuildAttachmentObjectKey(BuildReleasesCollectionPrefix(repositoryPrefix), identifier, fileName);
    }

    private static string BuildCollectionPrefix(string repositoryPrefix, string collectionSegment)
    {
        return $"{repositoryPrefix.Trim('/')}/{collectionSegment}";
    }

    private static string BuildDocumentObjectKey(string collectionPrefix, string identifier)
    {
        return $"{collectionPrefix}/{identifier}.json";
    }

    private static string BuildManifestObjectKey(string collectionPrefix)
    {
        return $"{collectionPrefix}/{CollectionManifestObjectName}";
    }

    private static string BuildAttachmentObjectKey(string collectionPrefix, string identifier, string fileName)
    {
        return $"{collectionPrefix}/{AttachmentsCollectionSegment}/{identifier}/{fileName}";
    }

    /// <summary>
    /// Parses the snapshot timestamp encoded in an archive object key
    /// (<c>{prefix}/{unixSeconds}_repo.tar.gz</c>). Returns false for non-archive keys.
    /// </summary>
    public static bool TryGetArchiveTimestamp(string objectKey, out long timestampUnixSeconds)
    {
        timestampUnixSeconds = 0;
        if (!objectKey.EndsWith(ArchiveObjectNameSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var leaf = objectKey[(objectKey.LastIndexOf('/') + 1)..];
        var timestampText = leaf[..^ArchiveObjectNameSuffix.Length];
        return long.TryParse(timestampText, out timestampUnixSeconds) && timestampUnixSeconds > 0;
    }

    /// <summary>
    /// Returns the parent prefix of an object key (everything before the last '/'),
    /// which for an archive or metadata object is its repository prefix.
    /// </summary>
    public static string GetParentPrefix(string objectKey)
    {
        var lastSlash = objectKey.LastIndexOf('/');
        return lastSlash <= 0 ? string.Empty : objectKey[..lastSlash];
    }

    public static string EnsurePrefix(string keyOrPrefix)
    {
        var value = keyOrPrefix.Trim('/');
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Trim('/') already stripped any trailing slash, so a separator is always appended.
        return $"{value}/";
    }
}
