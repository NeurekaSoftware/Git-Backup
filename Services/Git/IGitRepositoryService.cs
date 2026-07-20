namespace GitBackup.Services.Git;

public interface IGitRepositoryService
{
    Task SyncBareRepositoryAsync(
        string remoteUrl,
        string localPath,
        GitCredential? credential,
        bool cache,
        bool includeLfs,
        CancellationToken cancellationToken);
}
