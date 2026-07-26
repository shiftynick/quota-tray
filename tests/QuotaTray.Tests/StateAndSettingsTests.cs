namespace QuotaTray.Tests;

public sealed class StateAndSettingsTests
{
    [Fact]
    public void AlwaysOnTop_NotifiesWhenChanged()
    {
        var viewModel = new QuotaViewModel();
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.AlwaysOnTop = true;

        Assert.True(viewModel.AlwaysOnTop);
        Assert.Contains(nameof(QuotaViewModel.AlwaysOnTop), changedProperties);
    }

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
            store.Save(new AppSettings
            {
                ShowPacingInsights = true,
                AlwaysOnTop = true
            });

            var settings = store.Load();
            Assert.True(settings.ShowPacingInsights);
            Assert.True(settings.AlwaysOnTop);
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
            var settings = new SettingsStore(path).Load();
            Assert.False(settings.ShowPacingInsights);
            Assert.False(settings.AlwaysOnTop);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
