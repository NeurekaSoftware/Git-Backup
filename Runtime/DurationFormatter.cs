namespace GitBackup.Runtime;

public static class DurationFormatter
{
    // Fixed unit sizes keep formatting deterministic for raw second counts.
    private static readonly UnitDefinition[] Units =
    [
        new(365L * 24 * 60 * 60, "y"),
        new(30L * 24 * 60 * 60, "mo"),
        new(7L * 24 * 60 * 60, "w"),
        new(24L * 60 * 60, "d"),
        new(60L * 60, "h"),
        new(60L, "m"),
        new(1L, "s")
    ];

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

    private readonly record struct UnitDefinition(long Seconds, string ShortName);

    private readonly record struct DurationPart(long Value, UnitDefinition Unit);
}
