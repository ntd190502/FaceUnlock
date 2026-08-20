using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using FaceUnlock.Core;

namespace FaceUnlock.Shell;

public enum ShellMode { Test, Shell }
public enum ShellState { INITIALIZING, SERVICE_UNAVAILABLE, NOT_PAIRED, WAITING_FACE_ID, APPROVED, REJECTED, TIMEOUT, OFFLINE, ERROR, RECOVERY, STARTING_DESKTOP, DESKTOP_FAILED, INPUT_GUARD_FAILED, TEST_PASS }

public interface IExplorerLauncher
{
    bool FileExists(string path);
    bool StartProcess(string path, out string? errorMessage);
}

public class DefaultExplorerLauncher : IExplorerLauncher
{
    public bool FileExists(string path) => File.Exists(path);
    public bool StartProcess(string path, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            var proc = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return proc != null;
        }
        catch (Exception ex) { errorMessage = ex.Message; return false; }
    }
}

public sealed class ShellEngine
{
    private readonly ShellMode _mode;
    private readonly string _pipeName;
    private readonly IExplorerLauncher _launcher;
    private readonly string _logFile;
    private readonly IShellInputGuard _inputGuard;
    private readonly object _stateLock = new();
    private ShellState _currentState = ShellState.INITIALIZING;
    private string _statusMessage = "Starting FaceUnlock Shell...";
    private string? _currentRequestId;
    private bool _isAttemptInProgress;
    private bool _explorerStarted;
    private bool _desktopReleaseAuthorized;
    private bool _inputGuardAttempted;
    private bool _inputGuardFailed;
    private CancellationTokenSource? _attemptCts;
    private readonly int _processId = Environment.ProcessId;

    public event Action<ShellState, string>? StateChanged;
    public ShellState CurrentState => _currentState;
    public string StatusMessage => _statusMessage;
    public ShellMode Mode => _mode;
    public bool ExplorerStarted => _explorerStarted;
    public bool InputGuardActive => _inputGuard.IsActive;
    public bool IsGateLocked => _mode == ShellMode.Shell && !_desktopReleaseAuthorized && !_explorerStarted;
    public bool CanClose => _mode == ShellMode.Test || _desktopReleaseAuthorized || _explorerStarted;
    public string? CurrentRequestId => _currentRequestId;

    public ShellEngine(ShellMode mode, string pipeName = "FaceUnlock.Auth.v1", IExplorerLauncher? launcher = null, string? customLogFile = null, IShellInputGuard? inputGuard = null)
    {
        _mode = mode; _pipeName = pipeName; _launcher = launcher ?? new DefaultExplorerLauncher(); _inputGuard = inputGuard ?? new ShellInputGuard();
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceUnlock", "logs");
        _logFile = customLogFile ?? Path.Combine(logDir, "shell.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!); } catch { }
    }

    public void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ}] [{_mode}] {message}{Environment.NewLine}";
            lock (_stateLock) File.AppendAllText(_logFile, line, Encoding.UTF8);
        }
        catch { }
    }

    private void SetState(ShellState newState, string message)
    {
        lock (_stateLock) { _currentState = newState; _statusMessage = message; }
        Log($"State -> {newState}: {message}");
        try { StateChanged?.Invoke(newState, message); } catch { }
    }

    public async Task InitializeAndAutoStartAsync(CancellationToken ct = default, int maxRetries = 15)
    {
        Log($"Startup: SID={GetCurrentWindowsUserSid() ?? "(null)"} SessionID={GetCurrentWindowsSessionId()} Mode={_mode}");
        if (!EnsureInputGuard()) return;
        var retryDelay = TimeSpan.FromSeconds(2);
        SetState(ShellState.INITIALIZING, "Connecting to FaceUnlock Service...");
        while (!ct.IsCancellationRequested && !_explorerStarted)
        {
            var serviceOk = false;
            for (var i = 0; i < maxRetries && !ct.IsCancellationRequested; i++)
            {
                var ping = await SendIpcAsync(new LocalAuthRequest(1, "ping", Guid.NewGuid().ToString("N")), 800);
                if (ping?.status == LocalAuthStatus.Ok) { serviceOk = true; break; }
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException)
                {
                    SetState(ShellState.SERVICE_UNAVAILABLE, "FaceUnlock Service unavailable.");
                    return;
                }
            }
            if (!serviceOk)
            {
                SetState(ShellState.SERVICE_UNAVAILABLE, "FaceUnlock Service unavailable. Retrying automatically...");
                if (maxRetries <= 0) return;
                if (!await DelayForRetryAsync(retryDelay, ct)) return;
                continue;
            }

            if (CurrentState == ShellState.SERVICE_UNAVAILABLE)
                SetState(ShellState.INITIALIZING, "FaceUnlock Service restored. Starting unlock request...");

            var approved = await TryStartFaceIdAttemptAsync(ct);
            if (approved || _explorerStarted || ct.IsCancellationRequested) return;
            var delay = CurrentState == ShellState.NOT_PAIRED ? TimeSpan.FromSeconds(8) : retryDelay;
            Log($"Attempt ended in {CurrentState}; retrying in {delay.TotalSeconds:0.#}s.");
            if (!await DelayForRetryAsync(delay, ct)) return;
        }
    }

    private static async Task<bool> DelayForRetryAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; } catch (OperationCanceledException) { return false; }
    }

    public async Task<bool> TryStartFaceIdAttemptAsync(CancellationToken ct = default)
    {
        if (!EnsureInputGuard()) return false;
        CancellationTokenSource attemptCts;
        lock (_stateLock)
        {
            if (_isAttemptInProgress) { Log("Attempt already in progress. Ignoring duplicate start request."); return false; }
            if (_explorerStarted && _mode == ShellMode.Shell) return false;
            _isAttemptInProgress = true;
            var previous = _attemptCts;
            _attemptCts = attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (previous != null)
            {
                try { previous.Cancel(); } catch (ObjectDisposedException) { }
                previous.Dispose();
            }
        }
        var attemptToken = attemptCts.Token;
        var reqId = Guid.NewGuid().ToString("N"); _currentRequestId = reqId;
        var sid = GetCurrentWindowsUserSid(); var session = GetCurrentWindowsSessionId();
        var shellClientType = _mode == ShellMode.Shell ? "shell" : "shell_test";
        SetState(ShellState.WAITING_FACE_ID, "Waiting for iPhone Face ID...");
        try
        {
            var req = new LocalAuthRequest(1, "request_unlock", reqId, usage: _mode == ShellMode.Shell ? "shell" : "shell_test",
                username: Environment.UserName, user_sid: sid, qualified_username: $"{Environment.UserDomainName}\\{Environment.UserName}",
                session_id: session, client_type: shellClientType, pc_id: Environment.MachineName, process_id: _processId);
            var ack = await SendIpcAsync(req, 3000);
            if (ack == null || ack.status == LocalAuthStatus.Error) { SetState(ShellState.ERROR, ack?.message ?? "Service communication failed. Retrying automatically..."); return false; }
            if (ack.status == LocalAuthStatus.NotPaired) { SetState(ShellState.NOT_PAIRED, "FaceUnlock is not paired with iPhone. Waiting for setup..."); return false; }

            while (!attemptToken.IsCancellationRequested)
            {
                await Task.Delay(1000, attemptToken);
                var statusResp = await SendIpcAsync(new LocalAuthRequest(1, "grant_status", reqId, user_sid: sid, session_id: session, client_type: shellClientType, process_id: _processId), 2000);
                if (statusResp == null) continue;
                switch (statusResp.status)
                {
                    case LocalAuthStatus.Approved: return await HandleApprovalFlowAsync(reqId, sid, session, attemptToken);
                    case LocalAuthStatus.Rejected: SetState(ShellState.REJECTED, "Request declined. Retrying automatically..."); return false;
                    case LocalAuthStatus.Timeout:
                    case LocalAuthStatus.Expired: SetState(ShellState.TIMEOUT, "Request timed out. Retrying automatically..."); return false;
                    case LocalAuthStatus.NotPaired: SetState(ShellState.NOT_PAIRED, "FaceUnlock is not paired with iPhone. Waiting for setup..."); return false;
                    case LocalAuthStatus.WaitingConnectivity: SetState(ShellState.WAITING_FACE_ID, statusResp.message ?? "Waiting for Bluetooth or Internet connectivity..."); break;
                    case LocalAuthStatus.Error: SetState(ShellState.ERROR, statusResp.message ?? "Authorization error. Retrying automatically..."); return false;
                }
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            await SendIpcAsync(new LocalAuthRequest(1, "cancel_request", reqId, session_id: GetCurrentWindowsSessionId(), client_type: shellClientType, process_id: _processId), 1500);
            return false;
        }
        catch (Exception ex) { Log($"Exception during Face ID attempt: {ex.Message}"); SetState(ShellState.ERROR, "Unexpected error. Retrying automatically..."); return false; }
        finally
        {
            lock (_stateLock)
            {
                _isAttemptInProgress = false;
                if (ReferenceEquals(_attemptCts, attemptCts)) _attemptCts = null;
            }
            attemptCts.Dispose();
        }
    }

    public void CancelFaceIdAttempt()
    {
        CancellationTokenSource? cts;
        lock (_stateLock) cts = _attemptCts;
        if (cts == null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task<bool> HandleApprovalFlowAsync(string reqId, string? sid, int session, CancellationToken ct)
    {
        SetState(ShellState.APPROVED, "Face ID approved. Unlocking...");
        var clientType = _mode == ShellMode.Shell ? "shell" : "shell_test";
        var reserveResp = await SendIpcAsync(new LocalAuthRequest(1, "reserve_grant", reqId, user_sid: sid, session_id: session, client_type: clientType, process_id: _processId), 3000);
        if (reserveResp?.status != LocalAuthStatus.Reserved) { SetState(ShellState.ERROR, "Authorization reservation failed. Retrying automatically..."); return false; }
        if (!string.IsNullOrWhiteSpace(reserveResp.user_sid) && !string.IsNullOrWhiteSpace(sid) && !string.Equals(reserveResp.user_sid, sid, StringComparison.OrdinalIgnoreCase)) { SetState(ShellState.ERROR, "Authorization binding mismatch."); return false; }
        var consumeResp = await SendIpcAsync(new LocalAuthRequest(1, "consume_grant", reqId, user_sid: sid, session_id: session, client_type: clientType, process_id: _processId), 3000);
        if (consumeResp?.status != LocalAuthStatus.Consumed || !string.Equals(consumeResp.user_sid, sid, StringComparison.OrdinalIgnoreCase) || consumeResp.session_id != session) { SetState(ShellState.ERROR, "Authorization consumption failed. Retrying automatically..."); return false; }
        if (_mode == ShellMode.Test) { SetState(ShellState.TEST_PASS, "TEST PASS — Explorer launch would occur in Shell Mode."); return true; }
        return CompleteApprovedGrantAndLaunchExplorer();
    }

    internal bool CompleteApprovedGrantAndLaunchExplorer()
    {
        lock (_stateLock) { if (_inputGuardFailed) return false; _desktopReleaseAuthorized = true; }
        if (!_inputGuard.TryUninstall(out var guardError))
        {
            lock (_stateLock) { _desktopReleaseAuthorized = false; _inputGuardFailed = true; }
            SetState(ShellState.INPUT_GUARD_FAILED, $"Could not release keyboard input safely: {guardError}"); return false;
        }
        Log("Input guard removed after approved grant consumption.");
        return LaunchExplorerSafe();
    }

    public bool RetryExplorerSafe()
    {
        lock (_stateLock) { if (!_desktopReleaseAuthorized || _inputGuardFailed) return false; }
        return LaunchExplorerSafe();
    }

    private bool LaunchExplorerSafe()
    {
        lock (_stateLock) { if (_explorerStarted) return true; _explorerStarted = true; }
        SetState(ShellState.STARTING_DESKTOP, "Starting Windows Desktop...");
        var explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!_launcher.FileExists(explorerPath))
        {
            lock (_stateLock) _explorerStarted = false;
            SetState(ShellState.DESKTOP_FAILED, "Windows Desktop executable was not found. Retrying automatically..."); _ = RetryDesktopWithBackoffAsync(); return false;
        }
        if (_launcher.StartProcess(explorerPath, out var err)) { Log("explorer.exe process launched successfully."); return true; }
        Log($"Failed to launch explorer.exe: {err}"); lock (_stateLock) _explorerStarted = false;
        SetState(ShellState.DESKTOP_FAILED, "Desktop failed to start. Retrying automatically..."); _ = RetryDesktopWithBackoffAsync(); return false;
    }

    private async Task RetryDesktopWithBackoffAsync()
    {
        for (var attempt = 1; attempt <= 5; attempt++) { await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt)); if (RetryExplorerSafe()) return; }
        Log("Desktop automatic retry exhausted; Shell remains available for recovery.");
    }

    private bool EnsureInputGuard()
    {
        if (_mode == ShellMode.Test) return true;
        lock (_stateLock) { if (_inputGuardFailed) return false; if (_inputGuardAttempted) return _inputGuard.IsActive; _inputGuardAttempted = true; }
        if (_inputGuard.TryInstall(out var error)) { Log("Shell input guard installed."); return true; }
        lock (_stateLock) _inputGuardFailed = true;
        SetState(ShellState.INPUT_GUARD_FAILED, $"Keyboard lockdown could not be installed: {error}"); return false;
    }

    public void Shutdown()
    {
        CancellationTokenSource? cts;
        lock (_stateLock) { cts = _attemptCts; _attemptCts = null; }
        if (cts != null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
        if (!_inputGuard.TryUninstall(out var error)) Log($"WARNING: input guard uninstall during shutdown failed: {error}");
        _inputGuard.Dispose();
    }

    private async Task<LocalAuthResponse?> SendIpcAsync(LocalAuthRequest req, int timeoutMs = 3000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(timeoutMs);
            await pipe.ConnectAsync(cts.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(req, new JsonSerializerOptions(JsonSerializerDefaults.Web)).AsMemory(), cts.Token);
            var line = await reader.ReadLineAsync(cts.Token);
            return string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<LocalAuthResponse>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex) { Log($"IPC communication error (cmd={req.command}): {ex.Message}"); return null; }
    }

    public static string? GetCurrentWindowsUserSid() { try { return WindowsIdentity.GetCurrent().User?.Value; } catch { return null; } }
    public static int GetCurrentWindowsSessionId() { try { return Process.GetCurrentProcess().SessionId; } catch { return -1; } }
}
