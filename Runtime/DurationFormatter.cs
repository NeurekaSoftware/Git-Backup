namespace GitBackup.Runtime;

public static class DurationFormatter
{
    // Fixed unit sizes keep formatting deterministic for raw second counts.
    private static readonly UnitDefinition[] Units =
    [
        new(365L * 24 * 60 * 60, "year", "years", "y"),
        new(30L * 24 * 60 * 60, "month", "months", "mo"),
        new(7L * 24 * 60 * 60, "week", "weeks", "w"),
        new(24L * 60 * 60, "day", "days", "d"),
        new(60L * 60, "hour", "hours", "h"),
        new(60L, "minute", "minutes", "m"),
        new(1L, "second", "seconds", "s")
    ];

    public static string FormatLong(long totalSeconds)
    {
        var parts = Decompose(totalSeconds)
            .Select(part =>
                $"{part.Value} {(part.Value == 1 ? part.Unit.SingularName : part.Unit.PluralName)}")
            .ToList();

        return JoinHumanReadable(parts);
    }

    public static string FormatShort(long totalSeconds)
    {
        return string.Join(
            " ",
            Decompose(totalSeconds).Select(part => $"{part.Value}{part.Unit.ShortName}"));
    }

    private static IReadOnlyList<DurationPart> Decompose(long totalSeconds)
    {
        var remainingSeconds = Math.Max(0, totalSeconds);
        var parts = new List<DurationPart>();

        foreach (var unit in Units)
        {
            if (remainingSeconds < unit.Seconds)
            {
                continue;
            }

            var value = remainingSeconds / unit.Seconds;
            parts.Add(new DurationPart(value, unit));
            remainingSeconds -= value * unit.Seconds;
        }

        if (parts.Count == 0)
        {
            parts.Add(new DurationPart(0, Units[^1]));
        }

        return parts;
    }

    private static string JoinHumanReadable(IReadOnlyList<string> parts)
    {
        return parts.Count switch
        {
            0 => "0 seconds",
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => $"{string.Join(", ", parts.Take(parts.Count - 1))}, and {parts[^1]}"
        };
    }

    private readonly record struct UnitDefinition(
        long Seconds,
        string SingularName,
        string PluralName,
        string ShortName);

    private readonly record struct DurationPart(long Value, UnitDefinition Unit);
}
