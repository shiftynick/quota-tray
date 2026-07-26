namespace QuotaTray.Tests;

public sealed class ClaudeRefreshTests
{
    private static readonly ProviderSnapshot CachedSnapshot = new(
        "Claude",
        "Max",
        [
            new QuotaWindowSnapshot(
                "weekly",
                "7-day limit",
                50,
                DateTimeOffset.UtcNow.AddDays(3),
                TimeSpan.FromDays(7))
        ],
        null,
        DateTimeOffset.UtcNow.AddMinutes(-2));

    [Fact]
    public void ScheduledRefresh_UsesValidFiveMinuteCache()
    {
        var now = DateTimeOffset.UtcNow;

        var useCache = ClaudeQuotaService.ShouldUseCachedSnapshot(
            CachedSnapshot,
            now,
            now.AddMinutes(3),
            now.AddMinutes(-2),
            forceRefresh: false);

        Assert.True(useCache);
    }

    [Fact]
    public void ManualRefresh_BypassesNormalCacheAfterThirtySeconds()
    {
        var now = DateTimeOffset.UtcNow;

        var useCache = ClaudeQuotaService.ShouldUseCachedSnapshot(
            CachedSnapshot,
            now,
            now.AddMinutes(3),
            now.AddSeconds(-31),
            forceRefresh: true);

        Assert.False(useCache);
    }

    [Fact]
    public void ManualRefresh_CoalescesRepeatedClicks()
    {
        var now = DateTimeOffset.UtcNow;

        var useCache = ClaudeQuotaService.ShouldUseCachedSnapshot(
            CachedSnapshot,
            now,
            now.AddMinutes(3),
            now.AddSeconds(-10),
            forceRefresh: true);

        Assert.True(useCache);
    }

    [Fact]
    public void RateLimitedCache_IsMarkedStaleWithoutLosingSnapshotTime()
    {
        var retryAt = DateTimeOffset.Now.AddMinutes(10);

        var result = ClaudeQuotaService.MarkRateLimited(CachedSnapshot, retryAt);

        Assert.Equal(CachedSnapshot.FetchedAt, result.FetchedAt);
        Assert.Equal(CachedSnapshot.Windows, result.Windows);
        Assert.Contains("rate limiting", result.Error);
        Assert.Contains(retryAt.LocalDateTime.ToString("t"), result.Error);
    }
}
