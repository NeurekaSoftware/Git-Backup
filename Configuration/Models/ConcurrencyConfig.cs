using YamlDotNet.Serialization;

namespace GitBackup.Configuration.Models;

/// <summary>
/// Optional parallelism limits for a sync run. Both default to 1 (fully sequential — the historical
/// behavior); raise them to overlap network-bound work, at the cost of more concurrent memory, disk,
/// and provider/S3 request pressure. Tune with measurement.
/// </summary>
public sealed class ConcurrencyConfig
{
    // How many repositories to clone/upload in parallel within a run.
    [YamlMember(Alias = "repositories")]
    public int? Repositories { get; set; }

    // How many issues/merge requests to fetch (comments) and upload in parallel within one
    // repository's metadata backup.
    [YamlMember(Alias = "metadata")]
    public int? Metadata { get; set; }
}
