using System.Text.Json.Serialization;

namespace GitBackup.Services.Providers;

/// <summary>
/// Everything a metadata provider client needs to address a single project's issues and merge
/// requests. Built per repository during a sync run from the discovered repository plus the job's
/// configured base URL.
/// </summary>
public sealed class ProjectMetadataContext
{
    public required string CloneUrl { get; init; }

    public string? WebUrl { get; init; }

    // Provider-native project id (GitLab). Null when the provider addresses projects by owner/repo.
    public string? ProviderProjectId { get; init; }

    // Self-hosted forge base URL from the job config, or null for the provider default.
    public string? BaseUrl { get; init; }
}

/// <summary>
/// Shared shape of a backed-up issue or merge request, letting the sync orchestrator serialize,
/// download attachments for, index, and reconcile both collections with one code path.
/// </summary>
public interface IBackedUpProjectItem
{
    long Number { get; }

    string Title { get; }

    string? State { get; }

    DateTimeOffset? UpdatedAt { get; }

    IReadOnlyList<BackedUpAttachment> Attachments { get; set; }
}

/// <summary>
/// One row in a collection's <c>index.json</c> manifest, giving a later browser UI a listing
/// without needing to read every document or list the bucket.
/// </summary>
public sealed class CollectionManifestEntry
{
    public required long Number { get; init; }

    public string? Title { get; init; }

    public string? State { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// A backed-up issue with its full comment thread embedded. Serialized as <c>{number}.json</c>.
/// Fields are normalized across providers so a browser can render them uniformly.
/// </summary>
public sealed class BackedUpIssue : IBackedUpProjectItem
{
    public required long Number { get; init; }

    public required string Title { get; init; }

    public string? State { get; init; }

    public string? Author { get; init; }

    public string? Body { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    public string? WebUrl { get; init; }

    // Set by the provider after the per-issue comment/attachment fetches complete.
    public IReadOnlyList<BackedUpComment> Comments { get; set; } = [];

    public IReadOnlyList<BackedUpAttachment> Attachments { get; set; } = [];
}

/// <summary>
/// A backed-up merge/pull request with its comment thread embedded. Serialized as
/// <c>{number}.json</c> under <c>merge-requests/</c>.
/// </summary>
public sealed class BackedUpMergeRequest : IBackedUpProjectItem
{
    public required long Number { get; init; }

    public required string Title { get; init; }

    public string? State { get; init; }

    public string? Author { get; init; }

    public string? Body { get; init; }

    public string? SourceBranch { get; init; }

    public string? TargetBranch { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public DateTimeOffset? MergedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    public string? WebUrl { get; init; }

    // Set by the provider after the per-MR comment/attachment fetches complete.
    public IReadOnlyList<BackedUpComment> Comments { get; set; } = [];

    public IReadOnlyList<BackedUpAttachment> Attachments { get; set; } = [];
}

/// <summary>
/// A single comment (GitLab note / GitHub-Forgejo comment) on an issue or merge request.
/// </summary>
public sealed class BackedUpComment
{
    public long? Id { get; init; }

    public string? Author { get; init; }

    public string? Body { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    // True for provider-generated notes (state changes, label edits) rather than human comments.
    public bool System { get; init; }
}

/// <summary>
/// A file attached to an issue/MR body or comment. The provider resolves <see cref="DownloadUrl"/>
/// (not serialized); the orchestrator downloads it and records <see cref="StorageKey"/> — the
/// stored object's key relative to the bucket — so a browser can link the original reference to the
/// backed-up file.
/// </summary>
public sealed class BackedUpAttachment
{
    public required string FileName { get; init; }

    // The reference as it appears in the source text, e.g. /uploads/{sha}/{filename}.
    public required string OriginalPath { get; init; }

    public string? StorageKey { get; set; }

    public long? SizeBytes { get; set; }

    public string? ContentType { get; set; }

    [JsonIgnore]
    public required string DownloadUrl { get; init; }
}
