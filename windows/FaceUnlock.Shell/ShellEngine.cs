using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using FaceUnlock.Core;

namespace FaceUnlock.Shell;

public enum ShellMode
{
    Test,
    Shell
}

public enum ShellState
{
    INITIALIZING,
    SERVICE_UNAVAILABLE,
    NOT_PAIRED,
    WAITING_FACE_ID,
    APPROVED,
    REJECTED,
    TIMEOUT,
    OFFLINE,
    ERROR,
    RECOVERY,
    STARTING_DESKTOP,
    DESKTOP_FAILED,
    TEST_PASS
}

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
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            };
            var proc = Process.Start(psi);
            return proc != null;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}

public sealed class ShellEngine
{
    private readonly ShellMode _mode;
    private readonly string _pipeName;
    private readonly IExplorerLauncher _launcher;
    private readonly string _logFile;
    private readonly object _stateLock = new();

    private ShellState _currentState = ShellState.INITIALIZING;
    private string _statusMessage = "Starting FaceUnlock Shell...";
    private string? _currentRequestId;
    private bool _isAttemptInProgress = false;
    private bool _explorerStarted = false;
    private CancellationTokenSource? _attemptCts;

    public event Action<ShellState, string>? StateChanged;

    public ShellState CurrentState => _currentState;
    public string StatusMessage => _statusMessage;
    public ShellMode Mode => _mode;
    public bool ExplorerStarted => _explorerStarted;
    public string? CurrentRequestId => _currentRequestId;

    public ShellEngine(ShellMode mode, string pipeName = "FaceUnlock.Auth.v1", IExplorerLauncher? launcher = null, string? customLogFile = null)
    {
        _mode = mode;
        _pipeName = pipeName;
        _launcher = launcher ?? new DefaultExplorerLauncher();

        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceUnlock", "logs");
        _logFile = customLogFile ?? Path.Combine(logDir, "shell.log");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
        }
        catch { }
    }

    public void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ}] [{_mode}] {message}{Environment.NewLine}";
            lock (_stateLock)
            {
                File.AppendAllText(_logFile, line, Encoding.UTF8);
            }
        }
        catch { }
    }

    private void SetState(ShellState newState, string message)
    {
        lock (_stateLock)
        {
            _currentState = newState;
            _statusMessage = message;
        }
        Log($"State -> {newState}: {message}");
        try
        {
            StateChanged?.Invoke(newState, message);
        }
        catch { }
    }

    public async Task InitializeAndAutoStartAsync(CancellationToken ct = default, int maxRetries = 15)
    {
        var sid = GetCurrentWindowsUserSid();
        var session = GetCurrentWindowsSessionId();
        Log($"Startup: SID={sid ?? "(null)"} SessionID={session} Mode={_mode}");

        SetState(ShellState.INITIALIZING, "Connecting to FaceUnlock Service...");

        // 1. Health check service with retries (up to ~10-15s)
        bool serviceOk = false;
        try
        {
            for (int i = 0; i < maxRetries; i++)
            {
                if (ct.IsCancellationRequested) break;

                var pingResp = await SendIpcAsync(new LocalAuthRequest(1, "ping", Guid.NewGuid().ToString("N")), timeoutMs: 800);
                if (pingResp != null && pingResp.status == LocalAuthStatus.Ok)
                {
                    serviceOk = true;
                    break;
                }
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout reached during connection attempts
        }

        if (!serviceOk)
        {
            Log("Service health check failed: Service unavailable");
            SetState(ShellState.SERVICE_UNAVAILABLE, "FaceUnlock Service unavailable.");
            return;
        }

        Log("Service connected. Starting auto Face ID unlock request...");
        // 2. Automatically initiate exactly ONE Face ID attempt
        await TryStartFaceIdAttemptAsync(ct);
    }

    public async Task<bool> TryStartFaceIdAttemptAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_isAttemptInProgress)
            {
                Log("Attempt already in progress. Ignoring duplicate start request.");
                return false;
            }
            if (_explorerStarted && _mode == ShellMode.Shell)
            {
                Log("Explorer already started. Ignoring request.");
                return false;
            }
            _isAttemptInProgress = true;
        }

        _attemptCts?.Cancel();
        _attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attemptToken = _attemptCts.Token;

        var reqId = Guid.NewGuid().ToString("N");
        _currentRequestId = reqId;
        var sid = GetCurrentWindowsUserSid();
        var session = GetCurrentWindowsSessionId();

        SetState(ShellState.WAITING_FACE_ID, "Waiting for iPhone Face ID...");

        try
        {
            // Send request_unlock
            var req = new LocalAuthRequest(
                version: 1,
                command: "request_unlock",
                request_id: reqId,
                usage: "shell",
                username: Environment.UserName,
                user_sid: sid,
                qualified_username: $"{Environment.UserDomainName}\\{Environment.UserName}",
                session_id: session,
                client_type: "shell",
                pc_id: Environment.MachineName
            );

            var ack = await SendIpcAsync(req, timeoutMs: 3000);
            if (ack == null || ack.status == LocalAuthStatus.Error)
            {
                Log($"request_unlock failed: {ack?.message ?? "no response"}");
                SetState(ShellState.ERROR, ack?.message ?? "Service error communicating with FaceUnlock Service");
                return false;
            }

            if (ack.status == LocalAuthStatus.NotPaired)
            {
                Log("request_unlock rejected: PC is not paired");
                SetState(ShellState.NOT_PAIRED, "FaceUnlock is not paired with iPhone.");
                return false;
            }

            // The Service owns the unbounded connectivity wait. The Shell keeps
            // this same request ID alive until cancellation or a terminal result.
            while (!attemptToken.IsCancellationRequested)
            {
                await Task.Delay(1000, attemptToken);

                var statusReq = new LocalAuthRequest(
                    version: 1,
                    command: "grant_status",
                    request_id: reqId,
                    user_sid: sid,
                    session_id: session,
                    client_type: "shell"
                );

                var statusResp = await SendIpcAsync(statusReq, timeoutMs: 2000);
                if (statusResp == null) continue;

                if (statusResp.status == LocalAuthStatus.Approved)
                {
                    Log($"Face ID approved for request_id={reqId}. Proceeding to grant reservation.");
                    return await HandleApprovalFlowAsync(reqId, sid, session, attemptToken);
                }

                if (statusResp.status == LocalAuthStatus.Rejected)
                {
                    Log($"Face ID rejected for request_id={reqId}");
                    SetState(ShellState.REJECTED, "Face ID was rejected on iPhone.");
                    return false;
                }

                if (statusResp.status == LocalAuthStatus.Timeout || statusResp.status == LocalAuthStatus.Expired)
                {
                    Log($"Face ID timeout for request_id={reqId}");
                    SetState(ShellState.TIMEOUT, "Face ID request timed out.");
                    return false;
                }

                if (statusResp.status == LocalAuthStatus.NotPaired)
                {
                    SetState(ShellState.NOT_PAIRED, "FaceUnlock is not paired with iPhone.");
                    return false;
                }

                if (statusResp.status == LocalAuthStatus.WaitingConnectivity)
                {
                    SetState(ShellState.WAITING_FACE_ID, statusResp.message ?? "Waiting for Bluetooth or Internet connectivity...");
                }

                if (statusResp.status == LocalAuthStatus.Error)
                {
                    SetState(ShellState.ERROR, statusResp.message ?? "Face ID authorization error.");
                    return false;
                }
            }

            if (attemptToken.IsCancellationRequested)
            {
                Log($"Attempt cancelled for request_id={reqId}");
                return false;
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            await SendIpcAsync(new LocalAuthRequest(1, "cancel_request", reqId), timeoutMs: 1500);
            return false;
        }
        catch (Exception ex)
        {
            Log($"Exception during Face ID attempt: {ex.Message}");
            SetState(ShellState.ERROR, $"Error: {ex.Message}");
            return false;
        }
        finally
        {
            lock (_stateLock)
            {
                _isAttemptInProgress = false;
            }
        }
    }

    public void CancelFaceIdAttempt()
    {
        _attemptCts?.Cancel();
    }

    private async Task<bool> HandleApprovalFlowAsync(string reqId, string? sid, int session, CancellationToken ct)
    {
        SetState(ShellState.APPROVED, "Face ID approved. Unlocking...");

        // 1. Reserve grant
        var reserveReq = new LocalAuthRequest(
            version: 1,
            command: "reserve_grant",
            request_id: reqId,
            user_sid: sid,
            session_id: session,
            client_type: "shell"
        );

        var reserveResp = await SendIpcAsync(reserveReq, timeoutMs: 3000);
        if (reserveResp == null || reserveResp.status != LocalAuthStatus.Reserved)
        {
            Log($"Failed to reserve grant for request_id={reqId}: status={reserveResp?.status} msg={reserveResp?.message}");
            SetState(ShellState.ERROR, "Authorization grant reservation failed.");
            return false;
        }

        // Verify session and user SID bindings returned from grant if available
        if (!string.IsNullOrWhiteSpace(reserveResp.user_sid) && !string.IsNullOrWhiteSpace(sid))
        {
            if (!string.Equals(reserveResp.user_sid, sid, StringComparison.OrdinalIgnoreCase))
            {
                Log($"User SID mismatch in reservation: grant={reserveResp.user_sid} current={sid}");
                SetState(ShellState.ERROR, "Security validation failed: User SID mismatch.");
                return false;
            }
        }

        // 2. Consume grant
        var consumeReq = new LocalAuthRequest(
            version: 1,
            command: "consume_grant",
            request_id: reqId,
            user_sid: sid,
            session_id: session,
            client_type: "shell"
        );

        var consumeResp = await SendIpcAsync(consumeReq, timeoutMs: 3000);
        if (consumeResp == null || consumeResp.status != LocalAuthStatus.Consumed)
        {
            Log($"Failed to consume grant for request_id={reqId}: status={consumeResp?.status} msg={consumeResp?.message}");
            SetState(ShellState.ERROR, "Authorization grant consumption failed.");
            return false;
        }

        Log($"Grant consumed successfully for request_id={reqId}");

        // 3. Process Desktop Release
        if (_mode == ShellMode.Test)
        {
            Log("TEST MODE: Explorer launch skipped. Setting TEST_PASS.");
            SetState(ShellState.TEST_PASS, "TEST PASS — Explorer launch would occur in Shell Mode.");
            return true;
        }

        return LaunchExplorerSafe();
    }

    public bool LaunchExplorerSafe()
    {
        lock (_stateLock)
        {
            if (_explorerStarted)
            {
                Log("Explorer already started. Guard prevented duplicate start.");
                return true;
            }
            _explorerStarted = true;
        }

        SetState(ShellState.STARTING_DESKTOP, "Starting Windows Desktop...");

        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorerPath = Path.Combine(winDir, "explorer.exe");

        if (!_launcher.FileExists(explorerPath))
        {
            Log($"explorer.exe not found at {explorerPath}");
            SetState(ShellState.DESKTOP_FAILED, "Windows Desktop executable (explorer.exe) not found.");
            return false;
        }

        Log($"Launching explorer: {explorerPath}");
        if (_launcher.StartProcess(explorerPath, out var err))
        {
            Log("explorer.exe process launched successfully.");
            return true;
        }
        else
        {
            Log($"Failed to launch explorer.exe: {err}");
            lock (_stateLock)
            {
                _explorerStarted = false; // Allow Retry Desktop
            }
            SetState(ShellState.DESKTOP_FAILED, $"Desktop failed to start: {err}");
            return false;
        }
    }

    private async Task<LocalAuthResponse?> SendIpcAsync(LocalAuthRequest req, int timeoutMs = 3000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(timeoutMs);
            await pipe.ConnectAsync(cts.Token);

            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };

            var json = JsonSerializer.Serialize(req, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await writer.WriteLineAsync(json.AsMemory(), cts.Token);

            var line = await reader.ReadLineAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(line)) return null;

            return JsonSerializer.Deserialize<LocalAuthResponse>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex)
        {
            Log($"IPC communication error (cmd={req.command}): {ex.Message}");
            return null;
        }
    }

    public static string? GetCurrentWindowsUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value;
        }
        catch
        {
            return null;
        }
    }

    public static int GetCurrentWindowsSessionId()
    {
        try
        {
            return Process.GetCurrentProcess().SessionId;
        }
        catch
        {
            return -1;
        }
    }
}
