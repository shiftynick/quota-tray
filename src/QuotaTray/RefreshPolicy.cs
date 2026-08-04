namespace QuotaTray;

internal static class RefreshPolicy
{
    internal static readonly TimeSpan BackgroundInterval = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan RefreshOnOpenAge = TimeSpan.FromMinutes(2);
    private const double MaximumJitterFraction = 0.10;

    internal static TimeSpan NextBackgroundInterval(double jitterSample)
    {
        var normalizedSample = Math.Clamp(jitterSample, 0, 1);
        return TimeSpan.FromTicks(
            (long)(BackgroundInterval.Ticks *
                (1 + normalizedSample * MaximumJitterFraction)));
    }

    internal static bool ShouldRefreshOnOpen(
        DateTimeOffset now,
        DateTimeOffset? claudeFetchedAt,
        DateTimeOffset? codexFetchedAt,
        DateTimeOffset? cursorFetchedAt = null) =>
        IsStale(now, claudeFetchedAt) ||
        IsStale(now, codexFetchedAt) ||
        IsStale(now, cursorFetchedAt);

    private static bool IsStale(
        DateTimeOffset now,
        DateTimeOffset? fetchedAt) =>
        fetchedAt is null ||
        now - fetchedAt.Value >= RefreshOnOpenAge;
}
