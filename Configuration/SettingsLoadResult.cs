namespace GitBackup.Configuration;

public sealed class SettingsLoadResult
{
    private SettingsLoadResult(Settings? settings, IReadOnlyList<string> errors)
    {
        Settings = settings;
        Errors = errors;
    }

    public Settings? Settings { get; }

    public IReadOnlyList<string> Errors { get; }

    public bool IsSuccess => Settings is not null && Errors.Count == 0;

    public static SettingsLoadResult Success(Settings settings)
    {
        return new SettingsLoadResult(settings, []);
    }

    public static SettingsLoadResult Failure(IReadOnlyList<string> errors)
    {
        return new SettingsLoadResult(null, errors);
    }
}
