using YamlDotNet.Serialization;

namespace GitBackup.Configuration.Models;

public sealed class ScheduleConfig
{
    [YamlMember(Alias = "repositories")]
    public JobScheduleConfig Repositories { get; set; } = new();
}
