namespace GitBackup.Configuration.Models;

/// <summary>
/// The supported S3 payload-signature modes and their canonical string values. Centralized so the
/// settings validator, the settings normalizer, and the storage client cannot drift out of sync.
/// </summary>
public static class PayloadSignatureModes
{
    public const string Full = "full";
    public const string Streaming = "streaming";
    public const string Unsigned = "unsigned";

    public static readonly IReadOnlySet<string> Supported =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Full, Streaming, Unsigned };

    /// <summary>
    /// Maps a configured value to a canonical mode, defaulting to <see cref="Full"/>.
    /// </summary>
    public static string Normalize(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            Streaming => Streaming,
            Unsigned => Unsigned,
            _ => Full
        };
    }
}
