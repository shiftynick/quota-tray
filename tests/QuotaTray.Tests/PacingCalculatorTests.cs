using System.Windows;

namespace QuotaTray.Tests;

public sealed class PacingCalculatorTests
{
    [Fact]
    public void Calculate_ReturnsExpectedDailyBudgetAndPace()
    {
        var now = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        var window = new QuotaWindowSnapshot(
            "weekly",
            "7-day limit",
            70,
            now.AddDays(4),
            TimeSpan.FromDays(7));

        var result = PacingCalculator.Calculate(window, now);

        Assert.NotNull(result);
        Assert.Contains("30% used vs 42.9% target", result.PaceLabel);
        Assert.Contains("10%/day avg", result.AverageLabel);
        Assert.Contains("17.5%/day available", result.AverageLabel);
    }

    [Fact]
    public void Calculate_IgnoresShortQuotaWindows()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new QuotaWindowSnapshot(
            "five-hour",
            "5-hour limit",
            50,
            now.AddHours(2),
            TimeSpan.FromHours(5));

        Assert.Null(PacingCalculator.Calculate(window, now));
    }

    [Fact]
    public void ViewModel_DoesNotBuildPacingWhenSettingIsDisabled()
    {
        var snapshot = SnapshotWithWeeklyWindow();
        var viewModel = new QuotaViewModel(showPacingInsights: false);

        viewModel.Apply(
            snapshot,
            snapshot with { Provider = "Codex" },
            snapshot with { Provider = "Cursor" });

        Assert.Equal(Visibility.Collapsed, viewModel.Claude.Windows[0].PacingVisibility);

        viewModel.ShowPacingInsights = true;

        Assert.Equal(Visibility.Visible, viewModel.Claude.Windows[0].PacingVisibility);
    }

    private static ProviderSnapshot SnapshotWithWeeklyWindow()
    {
        var now = DateTimeOffset.UtcNow;
        return new ProviderSnapshot(
            "Claude",
            "Max",
            [
                new QuotaWindowSnapshot(
                    "weekly",
                    "7-day limit",
                    75,
                    now.AddDays(5),
                    TimeSpan.FromDays(7))
            ],
            null,
            now);
    }
}
