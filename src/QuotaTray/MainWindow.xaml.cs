using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace QuotaTray;

public partial class MainWindow : Window
{
    public event EventHandler? RefreshRequested;
    public bool AllowClose { get; set; }

    public MainWindow(QuotaViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;
    }

    public void SetRefreshEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        RefreshButton.Content = enabled ? "↻" : "…";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void HeaderArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
