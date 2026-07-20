using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace GitBackup.Runtime;

public enum AppLogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

public static class AppLogger
{
    private static readonly TimeZoneInfo LocalTimeZone = TimeZoneInfo.Local;
    private static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);

    // The zone abbreviation has only two possible values for a given local zone (standard vs daylight),
    // so compute each once at startup instead of rebuilding it (with a StringBuilder and a split) on
    // every log event.
    private static readonly string StandardAbbreviation = BuildAbbreviation(LocalTimeZone.StandardName);
    private static readonly string DaylightAbbreviation = BuildAbbreviation(LocalTimeZone.DaylightName);

    public const string DefaultLogLevel = "info";

    // The one place a configured level name is tied to its enum value. SupportedLogLevels feeds the
    // settings validator's error message and TryParseLogLevel decides what it accepts, so deriving both
    // from this table keeps that message from ever disagreeing with what actually loads.
    private static readonly Dictionary<string, AppLogLevel> LevelsByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["debug"] = AppLogLevel.Debug,
        ["info"] = AppLogLevel.Info,
        ["warn"] = AppLogLevel.Warn,
        ["error"] = AppLogLevel.Error
    };

    public static IReadOnlyList<string> SupportedLogLevels => [.. LevelsByName.Keys];

    static AppLogger()
    {
        // Build the logger once and steer its threshold with a LoggingLevelSwitch, so a settings reload
        // mutates the level in place rather than swapping and disposing the logger while other threads
        // are mid-write.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.With(new TimeZoneEnricher())
            .WriteTo.Console(
                outputTemplate:
                "[{Timestamp:yyyy-MM-dd HH:mm:ss} ({TimeZone})] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public static bool TryParseLogLevel(string? value, out AppLogLevel level)
    {
        if (value is not null && LevelsByName.TryGetValue(value.Trim(), out level))
        {
            return true;
        }

        level = AppLogLevel.Info;
        return false;
    }

    public static string ToConfigValue(AppLogLevel level)
    {
        return LevelsByName.First(pair => pair.Value == level).Key;
    }

    public static void SetMinimumLevel(AppLogLevel level)
    {
        // Mutate the threshold in place — no logger rebuild, so concurrent writers never race a disposed
        // logger, and a reload that leaves the level unchanged costs nothing.
        LevelSwitch.MinimumLevel = ToSerilogLevel(level);
    }

    public static string FormatTimestamp(DateTimeOffset value)
    {
        var localTime = TimeZoneInfo.ConvertTime(value, LocalTimeZone);
        return $"{localTime:yyyy-MM-dd HH:mm:ss} ({ResolveAbbreviation(localTime)})";
    }

    public static void Debug(string messageTemplate, params object?[] propertyValues)
    {
        Log.Debug(messageTemplate, propertyValues);
    }

    public static void Info(string messageTemplate, params object?[] propertyValues)
    {
        Log.Information(messageTemplate, propertyValues);
    }

    public static void Warn(string messageTemplate, params object?[] propertyValues)
    {
        Log.Warning(messageTemplate, propertyValues);
    }

    public static void Error(string messageTemplate, params object?[] propertyValues)
    {
        Log.Error(messageTemplate, propertyValues);
    }

    public static void Error(Exception exception, string messageTemplate, params object?[] propertyValues)
    {
        Log.Error(exception, messageTemplate, propertyValues);
    }

    public static void Shutdown()
    {
        Log.CloseAndFlush();
    }

    // Selects the cached abbreviation for an already-local timestamp based on whether DST is in effect,
    // so a long-running process that crosses a DST boundary labels each line with the zone in effect at
    // the time.
    private static string ResolveAbbreviation(DateTimeOffset localTime)
    {
        return LocalTimeZone.IsDaylightSavingTime(localTime.DateTime) ? DaylightAbbreviation : StandardAbbreviation;
    }

    // Derives a short zone label from a zone display name: "UTC" for universal zones, otherwise the
    // initials of each word (e.g. "Central European Time" -> "CET"), falling back to the raw zone id
    // when that heuristic does not yield a 2-6 character token. Called only twice, at startup.
    private static string BuildAbbreviation(string displayName)
    {
        if (displayName.Contains("Universal", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("UTC", StringComparison.OrdinalIgnoreCase))
        {
            return "UTC";
        }

        var abbreviation = new StringBuilder();
        foreach (var segment in displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            abbreviation.Append(char.ToUpperInvariant(segment[0]));
        }

        return abbreviation.Length is >= 2 and <= 6 ? abbreviation.ToString() : LocalTimeZone.Id;
    }

    private sealed class TimeZoneEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var localTime = TimeZoneInfo.ConvertTime(logEvent.Timestamp, LocalTimeZone);
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("TimeZone", ResolveAbbreviation(localTime)));
        }
    }

    private static LogEventLevel ToSerilogLevel(AppLogLevel level)
    {
        return level switch
        {
            AppLogLevel.Debug => LogEventLevel.Debug,
            AppLogLevel.Info => LogEventLevel.Information,
            AppLogLevel.Warn => LogEventLevel.Warning,
            _ => LogEventLevel.Error
        };
    }
}
