using YamlDotNet.Serialization;

namespace GitBackup.Configuration.Models;

public sealed class RepositoryJobConfig
{
    [YamlMember(Alias = "mode")]
    public string? Mode { get; set; }

    [YamlMember(Alias = "provider")]
    public string? Provider { get; set; }

    [YamlMember(Alias = "url")]
    public List<string>? Urls { get; set; }

    [YamlMember(Alias = "credential")]
    public string? Credential { get; set; }

    [YamlMember(Alias = "baseUrl")]
    public string? BaseUrl { get; set; }

    [YamlMember(Alias = "lfs")]
    public bool? Lfs { get; set; }

    [YamlMember(Alias = "cache")]
    public bool? Cache { get; set; }

    [YamlMember(Alias = "enabled")]
    public bool? Enabled { get; set; }

    [YamlMember(Alias = "includeStarred")]
    public bool? IncludeStarred { get; set; }

    [YamlMember(Alias = "includeSnippets")]
    public bool? IncludeSnippets { get; set; }

    [YamlMember(Alias = "includeIssues")]
    public bool? IncludeIssues { get; set; }

    [YamlMember(Alias = "includeIssueArtifacts")]
    public bool? IncludeIssueArtifacts { get; set; }

    [YamlMember(Alias = "includeMergeRequests")]
    public bool? IncludeMergeRequests { get; set; }

    [YamlMember(Alias = "includeMergeRequestsArtifacts")]
    public bool? IncludeMergeRequestsArtifacts { get; set; }
}
