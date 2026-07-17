using YamlDotNet.Serialization;

namespace GitBackup.Configuration.Models;

public sealed class StorageConfig
{
    [YamlMember(Alias = "endpoint")]
    public string? Endpoint { get; set; }

    [YamlMember(Alias = "region")]
    public string? Region { get; set; }

    // The two key fields accept a literal value or a ${ENV_VAR} placeholder, or can be sourced from a
    // file via their *File companion (e.g. a Docker secret mount); see SecretResolver.
    [YamlMember(Alias = "accessKeyId")]
    public string? AccessKeyId { get; set; }

    [YamlMember(Alias = "accessKeyIdFile")]
    public string? AccessKeyIdFile { get; set; }

    [YamlMember(Alias = "secretAccessKey")]
    public string? SecretAccessKey { get; set; }

    [YamlMember(Alias = "secretAccessKeyFile")]
    public string? SecretAccessKeyFile { get; set; }

    [YamlMember(Alias = "forcePathStyle")]
    public bool? ForcePathStyle { get; set; }

    [YamlMember(Alias = "payloadSignatureMode")]
    public string? PayloadSignatureMode { get; set; }

    [YamlMember(Alias = "bucket")]
    public string? Bucket { get; set; }

    [YamlMember(Alias = "retention")]
    public int? Retention { get; set; }

    [YamlMember(Alias = "retentionMinimum")]
    public int? RetentionMinimum { get; set; }
}
