using YamlDotNet.Serialization;

namespace GitBackup.Configuration.Models;

public sealed class CredentialConfig
{
    [YamlMember(Alias = "username")]
    public string? Username { get; set; }

    [YamlMember(Alias = "apiKey")]
    public string? ApiKey { get; set; }
}
