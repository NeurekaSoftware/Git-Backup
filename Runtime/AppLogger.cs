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
    private static readonly object Sync = new();
    private static readonly TimeZoneInfo LocalTimeZone = TimeZoneInfo.Local;
    private static AppLogLevel _minimumLevel = AppLogLevel.Info;

    public const string DefaultLogLevel = "info";

    public static IReadOnlyList<string> SupportedLogLevels => ["debug", "info", "warn", "error"];

    static AppLogger()
    {
        ReconfigureLogger(_minimumLevel);
    }

    public static bool TryParseLogLevel(string? value, out AppLogLevel level)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "debug":
                level = AppLogLevel.Debug;
                return true;
            case "info":
                level = AppLogLevel.Info;
                return true;
            case "warn":
                level = AppLogLevel.Warn;
                return true;
            case "error":
                level = AppLogLevel.Error;
                return true;
            default:
                level = AppLogLevel.Info;
                return false;
        }
    }

    public static string ToConfigValue(AppLogLevel level)
    {
        return level switch
        {
            AppLogLevel.Debug => "debug",
            AppLogLevel.Info => "info",
            AppLogLevel.Warn => "warn",
            _ => "error"
        };
    }

    public static void SetMinimumLevel(AppLogLevel level)
    {
        lock (Sync)
        {
            _minimumLevel = level;
            ReconfigureLogger(_minimumLevel);
        }
    }

    public static string FormatTimestamp(DateTimeOffset value)
    {
        var localTime = TimeZoneInfo.ConvertTime(value, LocalTimeZone);
        var timeZone = GetTimeZoneAbbreviation(localTime);
        return $"{localTime:yyyy-MM-dd HH:mm:ss} ({timeZone})";
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

    private static void ReconfigureLogger(AppLogLevel minimumLevel)
    {
        var configuredLogger = new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilogLevel(minimumLevel))
            .Enrich.With(new TimeZoneEnricher())
            .WriteTo.Console(
                outputTemplate:
                "[{Timestamp:yyyy-MM-dd HH:mm:ss} ({TimeZone})] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var previousLogger = Log.Logger;
        Log.Logger = configuredLogger;

        if (previousLogger is IDisposable disposableLogger)
        {
            disposableLogger.Dispose();
        }
    }

    private static string GetTimeZoneAbbreviation(DateTimeOffset value)
    {
        var localTime = TimeZoneInfo.ConvertTime(value, LocalTimeZone);
        var displayName = LocalTimeZone.IsDaylightSavingTime(localTime.DateTime)
            ? LocalTimeZone.DaylightName
            : LocalTimeZone.StandardName;

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

        if (abbreviation.Length is >= 2 and <= 6)
        {
            return abbreviation.ToString();
        }

        return LocalTimeZone.Id;
    }

    // Computes the timezone abbreviation from each event's own timestamp, so a long-running process
    // that crosses a DST boundary labels lines with the zone in effect at the time — rather than the
    // one captured once when the logger was configured.
    private sealed class TimeZoneEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var abbreviation = GetTimeZoneAbbreviation(logEvent.Timestamp);
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("TimeZone", abbreviation));
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
