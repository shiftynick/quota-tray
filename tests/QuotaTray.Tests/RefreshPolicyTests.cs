namespace QuotaTray.Tests;

public sealed class RefreshPolicyTests
{
    [Fact]
    public void BackgroundInterval_IsBetweenFifteenAndSixteenAndAHalfMinutes()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(15),
            RefreshPolicy.NextBackgroundInterval(0));
        Assert.Equal(
            TimeSpan.FromMinutes(16.5),
            RefreshPolicy.NextBackgroundInterval(1));
    }

    [Fact]
    public void OpeningFlyout_RefreshesWhenEitherProviderIsOlderThanTwoMinutes()
    {
        var now = DateTimeOffset.UtcNow;

        var shouldRefresh = RefreshPolicy.ShouldRefreshOnOpen(
            now,
            now.AddMinutes(-3),
            now.AddSeconds(-30));

        Assert.True(shouldRefresh);
    }

    [Fact]
    public void OpeningFlyout_RefreshesWhenCursorIsOlderThanTwoMinutes()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(RefreshPolicy.ShouldRefreshOnOpen(
            now,
            now.AddSeconds(-30),
            now.AddSeconds(-30),
            now.AddMinutes(-3)));
    }

    [Fact]
    public void OpeningFlyout_DoesNotRefreshFreshSnapshots()
    {
        var now = DateTimeOffset.UtcNow;

        var shouldRefresh = RefreshPolicy.ShouldRefreshOnOpen(
            now,
            now.AddSeconds(-90),
            now.AddSeconds(-30),
            now.AddSeconds(-45));

        Assert.False(shouldRefresh);
    }

    [Fact]
    public void OpeningFlyout_RefreshesWhenAProviderHasNoSnapshot()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(RefreshPolicy.ShouldRefreshOnOpen(
            now,
            claudeFetchedAt: null,
            codexFetchedAt: now,
            cursorFetchedAt: now));
    }
}
