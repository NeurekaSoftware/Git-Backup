using GitBackup.Configuration.Models;
using YamlDotNet.Serialization;

namespace GitBackup.Configuration;

public sealed class Settings
{
    [YamlMember(Alias = "logging")]
    public LoggingConfig Logging { get; set; } = new();

    [YamlMember(Alias = "storage")]
    public StorageConfig Storage { get; set; } = new();

    [YamlMember(Alias = "credentials")]
    public Dictionary<string, CredentialConfig> Credentials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [YamlMember(Alias = "repositories")]
    public List<RepositoryJobConfig?> Repositories { get; set; } = [];

    [YamlMember(Alias = "schedule")]
    public ScheduleConfig Schedule { get; set; } = new();
}
