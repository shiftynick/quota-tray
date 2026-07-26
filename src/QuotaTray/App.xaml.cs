using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace QuotaTray;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\QuotaTray.Singleton";
    private const string ShowEventName = @"Local\QuotaTray.ShowWindow";

    private readonly ClaudeQuotaService _claudeService = new();
    private readonly CodexQuotaService _codexService = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SettingsStore _settingsStore = new();
    private QuotaViewModel? _viewModel;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _pacingMenuItem;
    private Forms.ToolStripMenuItem? _alwaysOnTopMenuItem;
    private DispatcherTimer? _refreshTimer;
    private Icon? _appIcon;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private Task? _showListener;
    private int _refreshing;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        StartInstanceListener();

        var settings = _settingsStore.Load();
        _viewModel = new QuotaViewModel(
            settings.ShowPacingInsights,
            settings.AlwaysOnTop);
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _window = new MainWindow(_viewModel);
        _window.RefreshRequested += async (_, _) => await RefreshAsync(forceClaude: true);
        _window.SizeChanged += (_, _) =>
        {
            if (_window.IsVisible)
            {
                Dispatcher.BeginInvoke(() => PositionNearTray(_window));
            }
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open quotas", null, (_, _) => ShowWindow());
        menu.Items.Add("Refresh", null, async (_, _) => await RefreshAsync(forceClaude: true));
        _pacingMenuItem = new Forms.ToolStripMenuItem("Show weekly pacing and daily budget")
        {
            Checked = _viewModel.ShowPacingInsights,
            CheckOnClick = true
        };
        _pacingMenuItem.CheckedChanged += (_, _) =>
        {
            if (_viewModel is not null &&
                _viewModel.ShowPacingInsights != _pacingMenuItem.Checked)
            {
                _viewModel.ShowPacingInsights = _pacingMenuItem.Checked;
            }
        };
        menu.Items.Add(_pacingMenuItem);
        _alwaysOnTopMenuItem = new Forms.ToolStripMenuItem("Always on top")
        {
            Checked = _viewModel.AlwaysOnTop,
            CheckOnClick = true
        };
        _alwaysOnTopMenuItem.CheckedChanged += (_, _) =>
        {
            if (_viewModel is not null &&
                _viewModel.AlwaysOnTop != _alwaysOnTopMenuItem.Checked)
            {
                _viewModel.AlwaysOnTop = _alwaysOnTopMenuItem.Checked;
            }
        };
        menu.Items.Add(_alwaysOnTopMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        var iconResource = GetResourceStream(
            new Uri("pack://application:,,,/Assets/quota-tray.ico"));
        if (iconResource is not null)
        {
            _appIcon = new Icon(iconResource.Stream);
        }

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _appIcon ?? SystemIcons.Application,
            Text = "Claude + Codex quotas",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                ToggleWindow();
            }
        };

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();

        ShowWindow();
        _ = RefreshAsync();
    }

    private void StartInstanceListener()
    {
        _showEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ShowEventName);
        _showListener = Task.Run(() =>
        {
            while (!_shutdown.IsCancellationRequested)
            {
                _showEvent.WaitOne();
                if (_shutdown.IsCancellationRequested)
                {
                    break;
                }

                Dispatcher.BeginInvoke(ShowWindow);
            }
        });
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
            showEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance may still be starting.
        }
    }

    private async Task RefreshAsync(bool forceClaude = false)
    {
        if (_viewModel is null ||
            Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        _viewModel.IsRefreshing = true;
        _window?.SetRefreshEnabled(false);

        try
        {
            var claudeTask = ReadSafelyAsync(
                token => _claudeService.ReadAsync(forceClaude, token),
                "Claude",
                _shutdown.Token);
            var codexTask = ReadSafelyAsync(
                _codexService.ReadAsync,
                "Codex",
                _shutdown.Token);
            var results = await Task.WhenAll(claudeTask, codexTask);

            _viewModel.Apply(results[0], results[1]);
            UpdateTrayText();
        }
        finally
        {
            _viewModel.IsRefreshing = false;
            _window?.SetRefreshEnabled(true);
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private static async Task<ProviderSnapshot> ReadSafelyAsync(
        Func<CancellationToken, Task<ProviderSnapshot>> reader,
        string provider,
        CancellationToken shutdownToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            return await reader(timeout.Token);
        }
        catch (OperationCanceledException) when (!shutdownToken.IsCancellationRequested)
        {
            return ProviderSnapshot.Failed(provider, "Timed out while refreshing.");
        }
        catch (OperationCanceledException)
        {
            return ProviderSnapshot.Failed(provider, "Refresh cancelled.");
        }
        catch (Exception ex)
        {
            return ProviderSnapshot.Failed(provider, FriendlyError(ex));
        }
    }

    private static string FriendlyError(Exception ex)
    {
        var message = ex.Message.Trim();
        if (message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "Sign-in expired. Open the provider CLI and sign in again.";
        }

        return message.Length > 140 ? message[..140] + "..." : message;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null ||
            e.PropertyName is not (
                nameof(QuotaViewModel.ShowPacingInsights) or
                nameof(QuotaViewModel.AlwaysOnTop)))
        {
            return;
        }

        if (_pacingMenuItem is not null)
        {
            _pacingMenuItem.Checked = _viewModel.ShowPacingInsights;
        }
        if (_alwaysOnTopMenuItem is not null)
        {
            _alwaysOnTopMenuItem.Checked = _viewModel.AlwaysOnTop;
        }

        try
        {
            _settingsStore.Save(new AppSettings
            {
                ShowPacingInsights = _viewModel.ShowPacingInsights,
                AlwaysOnTop = _viewModel.AlwaysOnTop
            });
        }
        catch (IOException)
        {
            // The UI setting still applies for this session.
        }
        catch (UnauthorizedAccessException)
        {
            // The UI setting still applies for this session.
        }
    }

    private void UpdateTrayText()
    {
        if (_trayIcon is null || _viewModel is null)
        {
            return;
        }

        var text =
            $"Claude {_viewModel.Claude.ShortSummary} | Codex {_viewModel.Codex.ShortSummary}";
        _trayIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private void ToggleWindow()
    {
        if (_window?.IsVisible == true)
        {
            _window.Hide();
        }
        else
        {
            ShowWindow();
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        _window.UpdateLayout();
        PositionNearTray(_window);
        _window.Activate();
    }

    private static void PositionNearTray(Window window)
    {
        var helper = new WindowInteropHelper(window);
        var handle = helper.Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var cursor = Forms.Cursor.Position;
        var screen = Forms.Screen.FromPoint(cursor);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        var x = screen.WorkingArea.Right - width - 12;
        var y = screen.WorkingArea.Bottom - height - 12;

        SetWindowPos(
            handle,
            IntPtr.Zero,
            x,
            y,
            0,
            0,
            SetWindowPosFlags.NoActivate |
            SetWindowPosFlags.NoSize |
            SetWindowPosFlags.NoZOrder);
    }

    private void ExitApplication()
    {
        _refreshTimer?.Stop();
        _shutdown.Cancel();
        _showEvent?.Set();
        try
        {
            _showListener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The listener is best-effort during shutdown.
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _appIcon?.Dispose();

        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }

        _showEvent?.Dispose();
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex was not acquired by this process.
        }
        _instanceMutex?.Dispose();
        Shutdown();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        SetWindowPosFlags flags);

    [Flags]
    private enum SetWindowPosFlags : uint
    {
        NoSize = 0x0001,
        NoZOrder = 0x0004,
        NoActivate = 0x0010
    }
}
