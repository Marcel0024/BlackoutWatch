namespace BlackoutWatch.Services;

internal static class OutageDuration
{
    public static string Format(int durationSeconds)
    {
        var duration = TimeSpan.FromSeconds(durationSeconds);
        return FormattableString.Invariant(
            $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}");
    }
}
