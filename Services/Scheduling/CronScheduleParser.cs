using Cronos;

namespace GitBackup.Services.Scheduling;

public static class CronScheduleParser
{
    public static bool TryParse(string? expression, out CronExpression? schedule, out string? error)
    {
        schedule = null;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "cron expression is required.";
            return false;
        }

        if (TryParseInternal(expression, CronFormat.Standard, out schedule) ||
            TryParseInternal(expression, CronFormat.IncludeSeconds, out schedule))
        {
            return true;
        }

        error = "must be a valid 5-field or 6-field cron expression.";
        return false;
    }

    private static bool TryParseInternal(string expression, CronFormat format, out CronExpression? schedule)
    {
        try
        {
            schedule = CronExpression.Parse(expression, format);
            return true;
        }
        catch
        {
            schedule = null;
            return false;
        }
    }
}
