using System.Text.Json;

namespace GitBackup.Services.Providers;

/// <summary>
/// JSON mappers for the Gitea-lineage API shape, shared by GitHub and Forgejo. Split from
/// <see cref="ProviderHttpClientBase"/> — which carries the HTTP, retry, pagination, and auth plumbing
/// every provider needs — because GitLab uses different field names entirely and would silently get
/// nothing back from these: a GitLab payload has no <c>number</c>, so the issue mapper would return null
/// and drop the item rather than fail. Keeping them off GitLab's base makes that unreachable.
/// </summary>
public abstract class GiteaProviderClientBase : ProviderHttpClientBase
{
    protected static DiscoveredRepository? MapGiteaRepository(JsonElement item, bool isStarred)
    {
        var cloneUrl = GetStringOrNull(item, "clone_url");
        if (string.IsNullOrWhiteSpace(cloneUrl))
        {
            return null;
        }

        return new DiscoveredRepository
        {
            CloneUrl = cloneUrl,
            WebUrl = GetStringOrNull(item, "html_url"),
            IsStarred = isStarred
        };
    }

    /// <summary>Maps a Gitea-lineage comment (GitHub, Forgejo): author at <c>user.login</c>.</summary>
    protected static BackedUpComment? MapGiteaComment(JsonElement item)
    {
        return MapComment(item, "user", "login", readSystemFlag: false);
    }

    /// <summary>
    /// Maps the shared issue fields. Attachments are populated by the caller — GitHub scans the body,
    /// Forgejo reads the assets array — so this leaves them empty.
    /// </summary>
    protected static BackedUpIssue? MapGiteaIssue(JsonElement item)
    {
        var number = GetInt64OrNull(item, "number");
        var title = GetStringOrNull(item, "title");
        if (number is null || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return new BackedUpIssue
        {
            Number = number.Value,
            Title = title,
            State = GetStringOrNull(item, "state"),
            Author = GetNestedStringOrNull(item, "user", "login"),
            Body = GetStringOrNull(item, "body"),
            CreatedAt = GetDateTimeOffsetOrNull(item, "created_at"),
            UpdatedAt = GetDateTimeOffsetOrNull(item, "updated_at"),
            ClosedAt = GetDateTimeOffsetOrNull(item, "closed_at"),
            Labels = GetLabelNames(item, "labels"),
            WebUrl = GetStringOrNull(item, "html_url")
        };
    }

    /// <summary>Maps the shared pull-request fields; attachments are populated by the caller.</summary>
    protected static BackedUpMergeRequest? MapGiteaPullRequest(JsonElement item)
    {
        var number = GetInt64OrNull(item, "number");
        var title = GetStringOrNull(item, "title");
        if (number is null || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return new BackedUpMergeRequest
        {
            Number = number.Value,
            Title = title,
            State = GetStringOrNull(item, "state"),
            Author = GetNestedStringOrNull(item, "user", "login"),
            Body = GetStringOrNull(item, "body"),
            SourceBranch = GetNestedStringOrNull(item, "head", "ref"),
            TargetBranch = GetNestedStringOrNull(item, "base", "ref"),
            CreatedAt = GetDateTimeOffsetOrNull(item, "created_at"),
            UpdatedAt = GetDateTimeOffsetOrNull(item, "updated_at"),
            MergedAt = GetDateTimeOffsetOrNull(item, "merged_at"),
            ClosedAt = GetDateTimeOffsetOrNull(item, "closed_at"),
            Labels = GetLabelNames(item, "labels"),
            WebUrl = GetStringOrNull(item, "html_url")
        };
    }

    /// <summary>
    /// Maps a release, including its downloadable assets from the <c>assets</c> array.
    /// </summary>
    protected static BackedUpRelease? MapGiteaRelease(JsonElement item)
    {
        var tag = GetStringOrNull(item, "tag_name");
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        return new BackedUpRelease
        {
            Tag = tag,
            Name = GetStringOrNull(item, "name"),
            Body = GetStringOrNull(item, "body"),
            Author = GetNestedStringOrNull(item, "author", "login"),
            Draft = GetBoolean(item, "draft"),
            Prerelease = GetBoolean(item, "prerelease"),
            CreatedAt = GetDateTimeOffsetOrNull(item, "created_at"),
            PublishedAt = GetDateTimeOffsetOrNull(item, "published_at"),
            WebUrl = GetStringOrNull(item, "html_url"),
            Attachments = ExtractAssetArray(item)
        };
    }

    /// <summary>
    /// Extracts downloadable assets from a Gitea-lineage <c>assets</c> array, deduping by download URL
    /// so a release (or issue/MR) never yields the same file twice.
    /// </summary>
    protected static IReadOnlyList<BackedUpAttachment> ExtractAssetArray(JsonElement item)
    {
        var attachments = new List<BackedUpAttachment>();
        if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return attachments;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in assets.EnumerateArray())
        {
            var url = GetStringOrNull(asset, "browser_download_url");
            var name = GetStringOrNull(asset, "name");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(name) || !seen.Add(url))
            {
                continue;
            }

            attachments.Add(new BackedUpAttachment
            {
                FileName = AttachmentDownloader.BuildStorageFileName(url, name),
                OriginalPath = url,
                DownloadUrl = url,
                SizeBytes = GetInt64OrNull(asset, "size")
            });
        }

        return attachments;
    }
}
