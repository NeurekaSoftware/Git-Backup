namespace GitBackup.Services.Git;

public interface IGitRepositoryService
{
    Task SyncBareRepositoryAsync(
        string remoteUrl,
        string localPath,
        GitCredential? credential,
        bool force,
        bool includeLfs,
        CancellationToken cancellationToken);
}
