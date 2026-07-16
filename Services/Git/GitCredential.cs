namespace GitBackup.Services.Git;

public sealed class GitCredential
{
    public GitCredential(string username, string password)
    {
        Username = username;
        Password = password;
    }

    public string Username { get; }

    public string Password { get; }
}
