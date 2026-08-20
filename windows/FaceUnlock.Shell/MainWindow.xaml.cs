using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.ComponentModel;
using System.Windows.Threading;

namespace FaceUnlock.Shell;

public partial class MainWindow : Window
{
    private readonly ShellEngine _engine;
    private readonly string? _previewState;
    private bool _focusReclaimPending;

    public MainWindow(ShellEngine engine, string? previewState = null)
    {
        InitializeComponent();
        _engine = engine;
        _previewState = previewState;
        _engine.StateChanged += Engine_StateChanged;
        PcName.Text = Environment.MachineName.ToUpperInvariant();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Deactivated += MainWindow_Deactivated;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_engine.Mode == ShellMode.Test && !string.IsNullOrWhiteSpace(_previewState))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _engine.Log($"Shell visual preview render tier: {RenderCapability.Tier >> 16}; DPI: {dpi.DpiScaleX:0.##}x{dpi.DpiScaleY:0.##}; continuous storyboard groups: 0.");
            ApplyPreviewState(_previewState);
            return;
        }
        await _engine.InitializeAndAutoStartAsync();
    }

    private void Engine_StateChanged(ShellState state, string message) => Dispatcher.Invoke(() => UpdateUiForState(state, message));

    private void UpdateTransportIndicators(string message)
    {
        var hasBluetooth = message.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
        var waitingConnectivity = message.Contains("connectivity", StringComparison.OrdinalIgnoreCase);
        var internetRestored = message.Contains("Internet", StringComparison.OrdinalIgnoreCase)
            && message.Contains("restored", StringComparison.OrdinalIgnoreCase);

        if (hasBluetooth)
        {
            OnlineStatus.Text = "Offline";
            OnlineDot.Fill = BrushFromRgb(0xFB, 0xBF, 0x24);
            BluetoothStatus.Text = message.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                || message.Contains("off", StringComparison.OrdinalIgnoreCase)
                ? "Bluetooth Waiting" : "Bluetooth Active";
        }
        else if (internetRestored || !waitingConnectivity)
        {
            OnlineStatus.Text = "Online";
            OnlineDot.Fill = BrushFromRgb(0x54, 0xE4, 0x9A);
            BluetoothStatus.Text = "Bluetooth Standby";
        }
        else
        {
            OnlineStatus.Text = "Waiting";
            OnlineDot.Fill = BrushFromRgb(0xFB, 0xBF, 0x24);
            BluetoothStatus.Text = "Bluetooth Waiting";
        }
    }

    private void UpdateUiForState(ShellState state, string message)
    {
        UpdateTransportIndicators(message);
        switch (state)
        {
            case ShellState.INITIALIZING:
                StatusTitle.Text = "Waiting for iPhone";
                StatusTitle.Foreground = BrushFromRgb(0xEA, 0xF3, 0xFF);
                StatusDetail.Text = "Connecting to FaceUnlock Service";
                break;
            case ShellState.SERVICE_UNAVAILABLE:
                StatusTitle.Text = "Service unavailable";
                StatusTitle.Foreground = BrushFromRgb(0xF8, 0x71, 0x71);
                StatusDetail.Text = "Retrying automatically...";
                OnlineStatus.Text = "Waiting";
                break;
            case ShellState.NOT_PAIRED:
                StatusTitle.Text = "iPhone not paired";
                StatusTitle.Foreground = BrushFromRgb(0xFB, 0xBF, 0x24);
                StatusDetail.Text = "Waiting for setup...";
                break;
            case ShellState.WAITING_FACE_ID:
                if (message.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
                {
                    StatusTitle.Text = "Connecting via Bluetooth";
                    StatusTitle.Foreground = BrushFromRgb(0x38, 0xBD, 0xF8);
                    StatusDetail.Text = "Keep your iPhone nearby";
                }
                else
                {
                    StatusTitle.Text = "Waiting for approval";
                    StatusTitle.Foreground = BrushFromRgb(0xEA, 0xF3, 0xFF);
                    StatusDetail.Text = "Check your iPhone";
                }
                break;
            case ShellState.APPROVED:
                StatusTitle.Text = "Unlocked";
                StatusTitle.Foreground = BrushFromRgb(0x34, 0xD3, 0x99);
                StatusDetail.Text = "Verifying approval";
                break;
            case ShellState.REJECTED:
                StatusTitle.Text = "Request declined";
                StatusTitle.Foreground = BrushFromRgb(0xF8, 0x71, 0x71);
                StatusDetail.Text = "Retrying automatically...";
                break;
            case ShellState.TIMEOUT:
                StatusTitle.Text = "Request timed out";
                StatusTitle.Foreground = BrushFromRgb(0xFB, 0xBF, 0x24);
                StatusDetail.Text = "Retrying automatically...";
                break;
            case ShellState.OFFLINE:
            case ShellState.ERROR:
                StatusTitle.Text = "Unable to connect";
                StatusTitle.Foreground = BrushFromRgb(0xF8, 0x71, 0x71);
                StatusDetail.Text = "Retrying automatically...";
                break;
            case ShellState.INPUT_GUARD_FAILED:
                StatusTitle.Text = "Input guard unavailable";
                StatusTitle.Foreground = BrushFromRgb(0xF8, 0x71, 0x71);
                StatusDetail.Text = "Use recovery or restart FaceUnlock";
                break;
            case ShellState.STARTING_DESKTOP:
                StatusTitle.Text = "Unlocked";
                StatusTitle.Foreground = BrushFromRgb(0x34, 0xD3, 0x99);
                StatusDetail.Text = "Starting Windows Desktop";
                break;
            case ShellState.DESKTOP_FAILED:
                StatusTitle.Text = "Desktop start failed";
                StatusTitle.Foreground = BrushFromRgb(0xF8, 0x71, 0x71);
                StatusDetail.Text = "Retrying automatically...";
                break;
            case ShellState.TEST_PASS:
                StatusTitle.Text = "Unlocked";
                StatusTitle.Foreground = BrushFromRgb(0x34, 0xD3, 0x99);
                StatusDetail.Text = "Test approval complete";
                break;
        }

        if (state is ShellState.APPROVED or ShellState.TEST_PASS) PlayApprovedTransition();

        if (state == ShellState.STARTING_DESKTOP && _engine.Mode == ShellMode.Shell)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                Dispatcher.Invoke(() =>
                {
                    if (_engine.ExplorerStarted && _engine.CurrentState == ShellState.STARTING_DESKTOP)
                        Application.Current.Shutdown(0);
                });
            });
        }
    }

    private void ApplyPreviewState(string previewState)
    {
        OnlineStatus.Text = "Online";
        BluetoothStatus.Text = "Bluetooth Standby";
        switch (previewState.Trim().ToLowerInvariant())
        {
            case "bluetooth":
                StatusTitle.Text = "Connecting via Bluetooth";
                StatusTitle.Foreground = BrushFromRgb(0xEA, 0xF3, 0xFF);
                StatusDetail.Text = "Keep your iPhone nearby";
                OnlineStatus.Text = "Offline";
                BluetoothStatus.Text = "Bluetooth Active";
                break;
            case "approval":
                StatusTitle.Text = "Waiting for approval";
                StatusTitle.Foreground = BrushFromRgb(0xEA, 0xF3, 0xFF);
                StatusDetail.Text = "Check your iPhone";
                break;
            case "verifying":
                StatusTitle.Text = "Verifying approval";
                StatusTitle.Foreground = BrushFromRgb(0xC4, 0xB5, 0xFD);
                StatusDetail.Text = "Please keep this screen open";
                break;
            case "approved":
                StatusTitle.Text = "Unlocked";
                StatusTitle.Foreground = BrushFromRgb(0x34, 0xD3, 0x99);
                StatusDetail.Text = "Test approval complete";
                PlayApprovedTransition();
                break;
            case "error":
                StatusTitle.Text = "Unable to connect";
                StatusTitle.Foreground = BrushFromRgb(0xF8, 0x71, 0x71);
                StatusDetail.Text = "Retrying automatically...";
                break;
            default:
                StatusTitle.Text = "Waiting for iPhone";
                StatusTitle.Foreground = BrushFromRgb(0x38, 0xBD, 0xF8);
                StatusDetail.Text = "Connecting to your iPhone to unlock";
                break;
        }
    }

    private void PlayApprovedTransition()
    {
        SuccessCheck.Visibility = Visibility.Visible;
        ((Storyboard)FindResource("ApprovedTransition")).Begin(this);
    }

    private static SolidColorBrush BrushFromRgb(byte red, byte green, byte blue) => new(Color.FromRgb(red, green, blue));

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_engine.CanClose)
        {
            e.Cancel = true;
            _engine.Log("Window close request rejected while Shell Gate is locked.");
            ReassertLockedWindow();
            return;
        }
        _engine.Shutdown();
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        if (!_engine.IsGateLocked || _focusReclaimPending) return;
        _focusReclaimPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _focusReclaimPending = false;
            if (_engine.IsGateLocked && IsVisible && !IsActive) ReassertLockedWindow();
        });
    }

    private void ReassertLockedWindow()
    {
        if (!_engine.IsGateLocked) return;
        WindowState = WindowState.Maximized;
        Topmost = true;
        Activate();
        Focus();
    }
}
