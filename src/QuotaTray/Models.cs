using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace QuotaTray;

public sealed record QuotaWindowSnapshot(
    string Id,
    string Label,
    double RemainingPercent,
    DateTimeOffset? ResetsAt,
    TimeSpan? Duration);

public sealed record ProviderSnapshot(
    string Provider,
    string? Plan,
    IReadOnlyList<QuotaWindowSnapshot> Windows,
    string? Error,
    DateTimeOffset? FetchedAt)
{
    public static ProviderSnapshot Failed(string provider, string error) =>
        new(provider, null, [], error, null);
}

public sealed record PacingInsight(
    string PaceLabel,
    string AverageLabel,
    MediaBrush PaceBrush);

public static class PacingCalculator
{
    public static PacingInsight? Calculate(
        QuotaWindowSnapshot window,
        DateTimeOffset? now = null)
    {
        if (window.ResetsAt is null ||
            window.Duration is null ||
            window.Duration.Value.TotalDays < 6)
        {
            return null;
        }

        var durationDays = window.Duration.Value.TotalDays;
        var remainingDays = Math.Clamp(
            (window.ResetsAt.Value - (now ?? DateTimeOffset.Now)).TotalDays,
            0,
            durationDays);
        var elapsedDays = durationDays - remainingDays;
        var usedPercent = Math.Clamp(100 - window.RemainingPercent, 0, 100);
        var targetUsed = Math.Clamp(elapsedDays / durationDays * 100, 0, 100);
        var difference = usedPercent - targetUsed;
        var dailyTarget = 100 / durationDays;
        var averagePerDay = elapsedDays < 1.0 / 1440
            ? 0
            : usedPercent / elapsedDays;
        var dailyBudget = remainingDays < 1.0 / 1440
            ? 0
            : window.RemainingPercent / remainingDays;

        string paceText;
        MediaBrush paceBrush;
        if (difference > 1)
        {
            paceText = $"{difference:0.#} pts over pace";
            paceBrush = new SolidColorBrush(MediaColor.FromRgb(245, 176, 65));
        }
        else if (difference < -1)
        {
            paceText = $"{Math.Abs(difference):0.#} pts under pace";
            paceBrush = new SolidColorBrush(MediaColor.FromRgb(57, 198, 138));
        }
        else
        {
            paceText = "on pace";
            paceBrush = new SolidColorBrush(MediaColor.FromRgb(57, 198, 138));
        }

        return new PacingInsight(
            $"{usedPercent:0.#}% used vs {targetUsed:0.#}% target · {paceText}",
            $"{averagePerDay:0.#}%/day avg vs {dailyTarget:0.##}% target; " +
            $"{dailyBudget:0.#}%/day available for {remainingDays:0.#} days",
            paceBrush);
    }
}

public sealed class QuotaWindowViewModel
{
    public required string Label { get; init; }
    public required double RemainingPercent { get; init; }
    public required MediaBrush Accent { get; init; }
    public string RemainingLabel => $"{Math.Round(RemainingPercent):0}% left";
    public required string ResetLabel { get; init; }
    public string PaceLabel { get; init; } = "";
    public string AverageLabel { get; init; } = "";
    public MediaBrush PaceBrush { get; init; } =
        new SolidColorBrush(MediaColor.FromRgb(155, 164, 178));
    public System.Windows.Visibility PacingVisibility { get; init; } =
        System.Windows.Visibility.Collapsed;
}

public sealed class ProviderQuotaViewModel : INotifyPropertyChanged
{
    private static readonly MediaBrush NeutralStatusForeground =
        new SolidColorBrush(MediaColor.FromRgb(167, 173, 184));
    private static readonly MediaBrush NeutralStatusBackground =
        new SolidColorBrush(MediaColor.FromRgb(32, 36, 44));
    private static readonly MediaBrush WarningStatusForeground =
        new SolidColorBrush(MediaColor.FromRgb(255, 205, 112));
    private static readonly MediaBrush WarningStatusBackground =
        new SolidColorBrush(MediaColor.FromRgb(58, 45, 24));
    private readonly MediaBrush _accent;
    private ProviderSnapshot? _lastSnapshot;
    private string _planLabel = "";
    private string _statusMessage = "Loading…";
    private MediaBrush _statusForeground = NeutralStatusForeground;
    private MediaBrush _statusBackground = NeutralStatusBackground;
    private System.Windows.Visibility _statusVisibility = System.Windows.Visibility.Visible;
    private bool _isStale;

    public ProviderQuotaViewModel(string name, MediaBrush accent)
    {
        Name = name;
        _accent = accent;
    }

    public string Name { get; }
    public ObservableCollection<QuotaWindowViewModel> Windows { get; } = [];

    public string PlanLabel
    {
        get => _planLabel;
        private set => SetField(ref _planLabel, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public MediaBrush StatusForeground
    {
        get => _statusForeground;
        private set => SetField(ref _statusForeground, value);
    }

    public MediaBrush StatusBackground
    {
        get => _statusBackground;
        private set => SetField(ref _statusBackground, value);
    }

    public System.Windows.Visibility StatusVisibility
    {
        get => _statusVisibility;
        private set => SetField(ref _statusVisibility, value);
    }

    public bool IsStale
    {
        get => _isStale;
        private set => SetField(ref _isStale, value);
    }

    public string ShortSummary
    {
        get
        {
            var first = Windows.FirstOrDefault();
            return first is null ? "--" : $"{Math.Round(first.RemainingPercent):0}%";
        }
    }

    public void Apply(ProviderSnapshot snapshot, bool showPacingInsights)
    {
        if (snapshot.Error is not null)
        {
            IsStale = true;
            StatusForeground = WarningStatusForeground;
            StatusBackground = WarningStatusBackground;
            StatusVisibility = System.Windows.Visibility.Visible;

            var lastSuccess = snapshot.FetchedAt is null
                ? ""
                : $" Last successful fetch {snapshot.FetchedAt.Value.LocalDateTime:t}.";
            StatusMessage = Windows.Count == 0
                ? $"REFRESH FAILED · {snapshot.Error}"
                : $"STALE DATA ·{lastSuccess} {snapshot.Error}";
            if (Windows.Count == 0)
            {
                PlanLabel = "";
            }
            return;
        }

        _lastSnapshot = snapshot;
        IsStale = false;
        Rebuild(showPacingInsights);
    }

    public void Rebuild(bool showPacingInsights)
    {
        if (_lastSnapshot is null)
        {
            return;
        }

        PlanLabel = string.IsNullOrWhiteSpace(_lastSnapshot.Plan) ? "" : _lastSnapshot.Plan;
        StatusMessage = _lastSnapshot.Windows.Count == 0 ? "No quota windows returned." : "";
        StatusForeground = NeutralStatusForeground;
        StatusBackground = NeutralStatusBackground;
        StatusVisibility = _lastSnapshot.Windows.Count == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        Windows.Clear();

        foreach (var window in _lastSnapshot.Windows)
        {
            var pacing = showPacingInsights ? PacingCalculator.Calculate(window) : null;
            Windows.Add(new QuotaWindowViewModel
            {
                Label = window.Label,
                RemainingPercent = window.RemainingPercent,
                Accent = _accent,
                ResetLabel = window.ResetsAt is null
                    ? "Reset time unavailable"
                    : $"Resets {window.ResetsAt.Value.LocalDateTime:g}",
                PaceLabel = pacing?.PaceLabel ?? "",
                AverageLabel = pacing?.AverageLabel ?? "",
                PaceBrush = pacing?.PaceBrush ??
                    new SolidColorBrush(MediaColor.FromArgb(0, 0, 0, 0)),
                PacingVisibility = pacing is null
                    ? System.Windows.Visibility.Collapsed
                    : System.Windows.Visibility.Visible
            });
        }

        OnPropertyChanged(nameof(ShortSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class QuotaViewModel : INotifyPropertyChanged
{
    private bool _isRefreshing;
    private bool _showPacingInsights;
    private bool _alwaysOnTop;
    private string _lastUpdatedText = "Starting…";

    public QuotaViewModel(
        bool showPacingInsights = false,
        bool alwaysOnTop = false)
    {
        _showPacingInsights = showPacingInsights;
        _alwaysOnTop = alwaysOnTop;
        Claude = new ProviderQuotaViewModel(
            "Claude",
            new SolidColorBrush(MediaColor.FromRgb(217, 119, 87)));
        Codex = new ProviderQuotaViewModel(
            "Codex",
            new SolidColorBrush(MediaColor.FromRgb(57, 198, 138)));
    }

    public ProviderQuotaViewModel Claude { get; }
    public ProviderQuotaViewModel Codex { get; }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetField(ref _isRefreshing, value);
    }

    public bool ShowPacingInsights
    {
        get => _showPacingInsights;
        set
        {
            if (!SetField(ref _showPacingInsights, value))
            {
                return;
            }

            Claude.Rebuild(value);
            Codex.Rebuild(value);
        }
    }

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set => SetField(ref _alwaysOnTop, value);
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetField(ref _lastUpdatedText, value);
    }

    public void Apply(ProviderSnapshot claude, ProviderSnapshot codex)
    {
        Claude.Apply(claude, ShowPacingInsights);
        Codex.Apply(codex, ShowPacingInsights);

        var successfulTimes = new[] { claude, codex }
            .Where(snapshot => snapshot.Error is null && snapshot.FetchedAt is not null)
            .Select(snapshot => snapshot.FetchedAt!.Value)
            .ToArray();
        var hasError = claude.Error is not null || codex.Error is not null;

        if (successfulTimes.Length == 0)
        {
            LastUpdatedText = "Refresh failed · showing the last available data";
            return;
        }

        var latest = successfulTimes.Max().LocalDateTime;
        LastUpdatedText = hasError
            ? $"Last successful update {latest:t} · some data is stale"
            : $"Updated {latest:t}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
