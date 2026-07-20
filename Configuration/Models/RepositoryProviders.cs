namespace GitBackup.Configuration.Models;

/// <summary>
/// The supported repository providers and their canonical string values. Centralized so the settings
/// validator, the messages it produces, and the client registration cannot drift out of sync: adding a
/// provider to one but not another either rejects a provider the code supports, or accepts one that has
/// no registered client and fails partway through a run instead of at startup.
/// </summary>
public static class RepositoryProviders
{
    public const string GitHub = "github";
    public const string GitLab = "gitlab";
    public const string Forgejo = "forgejo";

    public static readonly IReadOnlySet<string> Supported =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GitHub, GitLab, Forgejo };
}
