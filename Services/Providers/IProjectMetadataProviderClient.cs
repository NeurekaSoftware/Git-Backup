using GitBackup.Configuration.Models;

namespace GitBackup.Services.Providers;

/// <summary>
/// Fetches a project's issues and merge/pull requests (with embedded comments and attachment
/// references) from a forge API. Implemented alongside <see cref="IRepositoryProviderClient"/> on
/// the same provider classes. A provider that does not implement this interface simply has no
/// project-metadata support; a provider that implements it but lacks a specific capability reports
/// that through the <c>Supports*</c> properties.
/// </summary>
public interface IProjectMetadataProviderClient
{
    string Provider { get; }

    bool SupportsIssues { get; }

    bool SupportsMergeRequests { get; }

    bool SupportsReleases { get; }

    // Whether the provider can resolve and download attachments/assets referenced by the metadata.
    bool SupportsArtifacts { get; }

    Task<IReadOnlyList<BackedUpIssue>> ListIssuesAsync(
        ProjectMetadataContext context,
        CredentialConfig credential,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BackedUpMergeRequest>> ListMergeRequestsAsync(
        ProjectMetadataContext context,
        CredentialConfig credential,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BackedUpRelease>> ListReleasesAsync(
        ProjectMetadataContext context,
        CredentialConfig credential,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens the raw bytes of an attachment previously reported on a backed-up issue/MR. The
    /// provider owns authentication and any host/redirect quirks; the caller owns storage. The
    /// returned stream must be disposed by the caller.
    /// </summary>
    Task<Stream> OpenAttachmentAsync(
        ProjectMetadataContext context,
        CredentialConfig credential,
        string downloadUrl,
        CancellationToken cancellationToken);
}
