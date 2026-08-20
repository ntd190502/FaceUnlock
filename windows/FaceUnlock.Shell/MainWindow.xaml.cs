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
            _engine.Log($"Shell visual preview render tier: {RenderCapability.Tier >> 16}; continuous storyboard groups: 4.");
            ApplyPreviewState(_previewState);
            return;
        }
        await _engine.InitializeAndAutoStartAsync();
    }

    private void Engine_StateChanged(ShellState state, string message)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateUiForState(state, message);
        });
    }

    private void UpdateUiForState(ShellState state, string message)
    {
        OnlineStatus.Text = "Online";
        BluetoothStatus.Text = "Bluetooth Ready";

        switch (state)
        {
            case ShellState.INITIALIZING:
                StatusTitle.Text = "Waiting for iPhone";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)); // Sky
                StatusDetail.Text = "Connecting to your iPhone to unlock";
                break;

            case ShellState.SERVICE_UNAVAILABLE:
                StatusTitle.Text = "Unable to connect";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // Red
                StatusDetail.Text = "Retrying automatically...";
                break;

            case ShellState.NOT_PAIRED:
                StatusTitle.Text = "Unable to connect";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)); // Amber
                StatusDetail.Text = "Retrying automatically...";
                break;

            case ShellState.WAITING_FACE_ID:
                StatusTitle.Text = "Waiting for approval";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)); // Sky
                StatusDetail.Text = message.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ? "Keep your iPhone nearby" : "Check your iPhone";
                if (message.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) { StatusTitle.Text = "Connecting via Bluetooth"; OnlineStatus.Text = "Offline"; BluetoothStatus.Text = "Bluetooth Connected"; }
                break;

            case ShellState.APPROVED:
                StatusTitle.Text = "Unlocked";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)); // Emerald
                StatusDetail.Text = "Verifying approval";
                break;

            case ShellState.REJECTED:
                StatusTitle.Text = "Request declined";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // Red
                StatusDetail.Text = "Retrying automatically...";
                break;

            case ShellState.TIMEOUT:
                StatusTitle.Text = "Unable to connect";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)); // Amber
                StatusDetail.Text = "Retrying automatically...";
                break;

            case ShellState.OFFLINE:
            case ShellState.ERROR:
                StatusTitle.Text = "Unable to connect";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // Red
                StatusDetail.Text = "Retrying automatically...";
                break;

            case ShellState.INPUT_GUARD_FAILED:
                StatusTitle.Text = "Unable to connect";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                StatusDetail.Text = "Retrying automatically...";
                break;

            case ShellState.STARTING_DESKTOP:
                StatusTitle.Text = "Unlocked";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)); // Emerald
                StatusDetail.Text = "Verifying approval";
                break;

            case ShellState.DESKTOP_FAILED:
                StatusTitle.Text = "Unable to connect";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // Red
                StatusDetail.Text = "Retrying automatically...";
                break;

            case ShellState.TEST_PASS:
                StatusTitle.Text = "Unlocked";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)); // Emerald
                StatusDetail.Text = "Test approval complete";
                break;
        }

        ApplyStateMotion(state == ShellState.APPROVED || state == ShellState.STARTING_DESKTOP || state == ShellState.TEST_PASS);

        // If desktop started successfully in shell mode, wait 1.5s then exit application cleanly
        if (state == ShellState.STARTING_DESKTOP && _engine.Mode == ShellMode.Shell)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                Dispatcher.Invoke(() =>
                {
                    if (_engine.ExplorerStarted && _engine.CurrentState == ShellState.STARTING_DESKTOP)
                    {
                        Application.Current.Shutdown(0);
                    }
                });
            });
        }
    }

    // Test-only visual states never start the engine or grant access to the desktop.
    private void ApplyPreviewState(string previewState)
    {
        OnlineStatus.Text = "Online";
        BluetoothStatus.Text = "Bluetooth Ready";

        switch (previewState.Trim().ToLowerInvariant())
        {
            case "bluetooth":
                StatusTitle.Text = "Connecting via Bluetooth";
                StatusTitle.Foreground = BrushFromRgb(0x38, 0xBD, 0xF8);
                StatusDetail.Text = "Keep your iPhone nearby";
                OnlineStatus.Text = "Offline";
                BluetoothStatus.Text = "Bluetooth Connected";
                break;
            case "approval":
                StatusTitle.Text = "Waiting for approval";
                StatusTitle.Foreground = BrushFromRgb(0x38, 0xBD, 0xF8);
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

        ApplyStateMotion(previewState.Trim().Equals("approved", StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyStateMotion(bool showSuccess)
    {
        SuccessCheck.Visibility = showSuccess ? Visibility.Visible : Visibility.Collapsed;

        StatusVisual.Opacity = 0;
        StatusMove.Y = 5;
        var duration = TimeSpan.FromMilliseconds(190);
        StatusVisual.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        StatusMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, duration));
        StatusDetail.BeginAnimation(OpacityProperty, new DoubleAnimation(.25, 1, duration));
    }

    private static SolidColorBrush BrushFromRgb(byte red, byte green, byte blue) =>
        new(Color.FromRgb(red, green, blue));


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
        if (!_engine.IsGateLocked || _focusReclaimPending)
        {
            return;
        }

        _focusReclaimPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _focusReclaimPending = false;
            if (_engine.IsGateLocked && IsVisible && !IsActive)
            {
                ReassertLockedWindow();
            }
        });
    }

    private void ReassertLockedWindow()
    {
        if (!_engine.IsGateLocked)
        {
            return;
        }

        WindowState = WindowState.Maximized;
        Topmost = true;
        Activate();
        Focus();
    }
}
