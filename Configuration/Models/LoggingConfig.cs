using YamlDotNet.Serialization;

namespace GitBackup.Configuration.Models;

public sealed class LoggingConfig
{
    [YamlMember(Alias = "logLevel")]
    public string? LogLevel { get; set; }
}
