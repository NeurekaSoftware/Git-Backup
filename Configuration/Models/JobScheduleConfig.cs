using YamlDotNet.Serialization;

namespace GitBackup.Configuration.Models;

public sealed class JobScheduleConfig
{
    [YamlMember(Alias = "cron")]
    public string? Cron { get; set; }
}
