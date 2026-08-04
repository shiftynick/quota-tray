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
            new ProviderSnapshot(
                "Claude",
                null,
                [],
                "Network unavailable.",
                now),
            showPacingInsights: false);

        Assert.Single(viewModel.Windows);
        Assert.True(viewModel.IsStale);
        Assert.StartsWith("STALE DATA", viewModel.StatusMessage);
        Assert.Contains($"Last successful fetch {now.LocalDateTime:t}", viewModel.StatusMessage);
        Assert.Contains("Network unavailable", viewModel.StatusMessage);
        Assert.Equal(System.Windows.Visibility.Visible, viewModel.StatusVisibility);
    }

    [Fact]
    public void SuccessfulRefresh_ClearsStaleWarning()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ProviderSnapshot(
            "Claude",
            "Max",
            [new QuotaWindowSnapshot("weekly", "7-day limit", 80, now.AddDays(6), TimeSpan.FromDays(7))],
            null,
            now);
        var viewModel = new ProviderQuotaViewModel(
            "Claude",
            System.Windows.Media.Brushes.Orange);

        viewModel.Apply(ProviderSnapshot.Failed("Claude", "Rate limited."), false);
        viewModel.Apply(snapshot, false);

        Assert.False(viewModel.IsStale);
        Assert.Equal("", viewModel.StatusMessage);
        Assert.Equal(System.Windows.Visibility.Collapsed, viewModel.StatusVisibility);
    }

    [Fact]
    public void ProviderWindows_ShowResetOnlyOnFirstRow()
    {
        var now = DateTimeOffset.UtcNow;
        var viewModel = new ProviderQuotaViewModel(
            "Cursor",
            System.Windows.Media.Brushes.CornflowerBlue);

        viewModel.Apply(
            new ProviderSnapshot(
                "Cursor",
                "Pro",
                [
                    new QuotaWindowSnapshot("included", "Included", 10, now.AddDays(20), TimeSpan.FromDays(30)),
                    new QuotaWindowSnapshot("auto", "Auto + Composer", 60, now.AddDays(20), TimeSpan.FromDays(30)),
                    new QuotaWindowSnapshot("api", "API models", 20, now.AddDays(20), TimeSpan.FromDays(30))
                ],
                null,
                now),
            showPacingInsights: false);

        Assert.Equal(System.Windows.Visibility.Visible, viewModel.Windows[0].ResetVisibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, viewModel.Windows[1].ResetVisibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, viewModel.Windows[2].ResetVisibility);
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
