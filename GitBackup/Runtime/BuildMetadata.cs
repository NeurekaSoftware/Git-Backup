namespace GitBackup.Runtime;

public static class BuildMetadata
{
    public static string Version { get; private set; } = "dev";

    public static string Commit { get; private set; } = "unknown";

    public static void LoadFromEnvironment()
    {
        Version = ReadValue("GIT_TAG", "dev");
        Commit = ReadValue("GIT_HASH", "unknown");
    }

    private static string ReadValue(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
    }
}
