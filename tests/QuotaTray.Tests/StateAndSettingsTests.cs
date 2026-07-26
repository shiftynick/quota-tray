namespace QuotaTray.Tests;

public sealed class StateAndSettingsTests
{
    [Fact]
    public void FailedRefresh_PreservesExistingWindowsAndMarksThemStale()
    {
        var now = DateTimeOffset.UtcNow;
        var success = new ProviderSnapshot(
            "Claude",
            "Max",
            [new QuotaWindowSnapshot("weekly", "7-day limit", 80, now.AddDays(6), TimeSpan.FromDays(7))],
            null,
            now);
        var viewModel = new ProviderQuotaViewModel(
            "Claude",
            System.Windows.Media.Brushes.Orange);
        viewModel.Apply(success, showPacingInsights: false);

        viewModel.Apply(
            ProviderSnapshot.Failed("Claude", "Network unavailable."),
            showPacingInsights: false);

        Assert.Single(viewModel.Windows);
        Assert.StartsWith("Stale", viewModel.StatusMessage);
        Assert.Contains("Network unavailable", viewModel.StatusMessage);
    }

    [Fact]
    public void SettingsStore_RoundTripsPacingPreference()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuotaTray.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings { ShowPacingInsights = true });

            Assert.True(store.Load().ShowPacingInsights);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SettingsStore_UsesDefaultsForInvalidJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuotaTray.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(path, "{ definitely not json");
        try
        {
            Assert.False(new SettingsStore(path).Load().ShowPacingInsights);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
