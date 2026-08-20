using System.Windows;
using System.Windows.Media;
using System.ComponentModel;
using System.Windows.Threading;

namespace FaceUnlock.Shell;

public partial class MainWindow : Window
{
    private readonly ShellEngine _engine;
    private bool _focusReclaimPending;

    public MainWindow(ShellEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        _engine.StateChanged += Engine_StateChanged;

        ModeBadge.Text = _engine.Mode == ShellMode.Test ? "TEST MODE" : "SHELL GATE";
        if (_engine.Mode == ShellMode.Test)
        {
            BtnExitTest.Visibility = Visibility.Visible;
        }

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Deactivated += MainWindow_Deactivated;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
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
        BtnTryAgain.Visibility = Visibility.Collapsed;
        BtnRetryDesktop.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case ShellState.INITIALIZING:
                StatusTitle.Text = "Connecting to Service...";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)); // Sky
                StatusDetail.Text = message;
                break;

            case ShellState.SERVICE_UNAVAILABLE:
                StatusTitle.Text = "Service Unavailable";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // Red
                StatusDetail.Text = "FaceUnlock background service is not running. Please start the service or use Recovery.";
                BtnTryAgain.Visibility = Visibility.Visible;
                break;

            case ShellState.NOT_PAIRED:
                StatusTitle.Text = "PC Not Paired";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)); // Amber
                StatusDetail.Text = "This PC has not been paired with an iPhone Face ID device.";
                BtnTryAgain.Visibility = Visibility.Visible;
                break;

            case ShellState.WAITING_FACE_ID:
                StatusTitle.Text = "Waiting for iPhone Face ID...";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)); // Sky
                StatusDetail.Text = message;
                break;

            case ShellState.APPROVED:
                StatusTitle.Text = "Face ID Approved";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)); // Emerald
                StatusDetail.Text = "Unlocking...";
                break;

            case ShellState.REJECTED:
                StatusTitle.Text = "Face ID Rejected";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // Red
                StatusDetail.Text = "Face ID was rejected on iPhone.";
                BtnTryAgain.Visibility = Visibility.Visible;
                break;

            case ShellState.TIMEOUT:
                StatusTitle.Text = "Face ID Timed Out";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)); // Amber
                StatusDetail.Text = "Unlock request timed out. Make sure iPhone is nearby and unlocked.";
                BtnTryAgain.Visibility = Visibility.Visible;
                break;

            case ShellState.OFFLINE:
            case ShellState.ERROR:
                StatusTitle.Text = "Authorization Error";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // Red
                StatusDetail.Text = message;
                BtnTryAgain.Visibility = Visibility.Visible;
                break;

            case ShellState.INPUT_GUARD_FAILED:
                StatusTitle.Text = "Input Guard Failed";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                StatusDetail.Text = $"Shell Gate remains locked because keyboard lockdown failed. {message} Use Recovery.";
                break;

            case ShellState.STARTING_DESKTOP:
                StatusTitle.Text = "Starting Windows Desktop...";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)); // Emerald
                StatusDetail.Text = "Launching explorer.exe...";
                break;

            case ShellState.DESKTOP_FAILED:
                StatusTitle.Text = "Desktop Failed to Start";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // Red
                StatusDetail.Text = message;
                BtnRetryDesktop.Visibility = Visibility.Visible;
                break;

            case ShellState.TEST_PASS:
                StatusTitle.Text = "FACE ID APPROVED — TEST PASS";
                StatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)); // Emerald
                StatusDetail.Text = "Explorer launch would occur in Shell Mode. Test complete.";
                BtnTryAgain.Visibility = Visibility.Visible;
                break;
        }

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

    private async void BtnTryAgain_Click(object sender, RoutedEventArgs e)
    {
        BtnTryAgain.Visibility = Visibility.Collapsed;
        await _engine.TryStartFaceIdAttemptAsync();
    }

    private void BtnRetryDesktop_Click(object sender, RoutedEventArgs e)
    {
        BtnRetryDesktop.Visibility = Visibility.Collapsed;
        _engine.RetryExplorerSafe();
    }

    private void BtnRecovery_Click(object sender, RoutedEventArgs e)
    {
        _engine.CancelFaceIdAttempt();
        RecoveryModal.Visibility = Visibility.Visible;
    }

    private void BtnDismissRecovery_Click(object sender, RoutedEventArgs e)
    {
        RecoveryModal.Visibility = Visibility.Collapsed;
    }

    private void BtnExitTest_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown(0);
    }

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
