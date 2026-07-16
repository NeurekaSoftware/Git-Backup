using GitBackup.Configuration.Models;
using GitBackup.Services.Git;

namespace GitBackup.Services;

public static class CredentialResolver
{
    public static GitCredential? ResolveGitCredential(CredentialConfig? credential)
    {
        if (credential is null || string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            return null;
        }

        var username = string.IsNullOrWhiteSpace(credential.Username)
            ? "git"
            : credential.Username.Trim();

        return new GitCredential(username, credential.ApiKey.Trim());
    }
}
