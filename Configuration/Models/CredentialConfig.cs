using YamlDotNet.Serialization;

namespace GitBackup.Configuration.Models;

public sealed class CredentialConfig
{
    [YamlMember(Alias = "username")]
    public string? Username { get; set; }

    // Accepts a literal token or a ${ENV_VAR} placeholder; see SecretResolver.
    [YamlMember(Alias = "apiKey")]
    public string? ApiKey { get; set; }

    // Path to a file holding the token (e.g. a Docker secret mount), as an alternative to apiKey.
    [YamlMember(Alias = "apiKeyFile")]
    public string? ApiKeyFile { get; set; }
}
