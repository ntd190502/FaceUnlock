using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FaceUnlock.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FaceUnlock.Service;

public sealed class UnlockWorker : BackgroundService
{
    private const string PipeName = "FaceUnlock.Auth.v1";
    private readonly ILogger<UnlockWorker> _log;
    private readonly ConfigStore _configStore;
    private readonly KeyStore _keyStore;
    private readonly BleScanner _bleScanner;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly string _logDir;
    private readonly string _serviceLogFile;

    // In-memory grant cache: requestId -> (approvedAt, expiresAt)
    private readonly Dictionary<string, (DateTimeOffset ApprovedAt, long ExpiresAt)> _activeGrants = new();

    public UnlockWorker(ILogger<UnlockWorker> log)
    {
        _log = log;
        _configStore = new ConfigStore();
        _keyStore = new KeyStore();
        _bleScanner = new BleScanner();

        _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceUnlock", "logs");
        _serviceLogFile = Path.Combine(_logDir, "service.log");

        try
        {
            Directory.CreateDirectory(_logDir);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to create log directory: {LogDir}", _logDir);
        }
    }

    private void AppendServiceLog(string message)
    {
        try
        {
            var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ}] {message}{Environment.NewLine}";
            File.AppendAllText(_serviceLogFile, line, Encoding.UTF8);
        }
        catch
        {
            // Ignore file logging failures to keep service robust
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("FaceUnlock Local Authentication Broker Service starting...");
        AppendServiceLog("FaceUnlock Service started. Listening on named pipe: FaceUnlock.Auth.v1");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pipeSecurity = CreatePipeSecurity();
                using var pipe = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 4096,
                    pipeSecurity
                );

                await pipe.WaitForConnectionAsync(stoppingToken);

                // Handle the client request asynchronously
                _ = HandleClientConnectionAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Named pipe server error");
                AppendServiceLog($"Named pipe listener exception: {ex.Message}");
                await Task.Delay(500, stoppingToken);
            }
        }

        _log.LogInformation("FaceUnlock Service stopped.");
        AppendServiceLog("FaceUnlock Service stopped.");
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var ps = new PipeSecurity();
        // Allow Local System full control
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        ps.AddAccessRule(new PipeAccessRule(systemSid, PipeAccessRights.FullControl, AccessControlType.Allow));

        // Allow Builtin Administrators full control
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        ps.AddAccessRule(new PipeAccessRule(adminSid, PipeAccessRights.FullControl, AccessControlType.Allow));

        // Allow Authenticated Users (including LogonUI context) read/write
        var authUserSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        ps.AddAccessRule(new PipeAccessRule(authUserSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return ps;
    }

    private async Task HandleClientConnectionAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        using (pipe)
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, true) { AutoFlush = true };

            try
            {
                var line = await reader.ReadLineAsync(stoppingToken);
                if (string.IsNullOrWhiteSpace(line))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new LocalAuthResponse(1, Guid.NewGuid().ToString("N"), LocalAuthStatus.Error, "Empty request payload")));
                    return;
                }

                LocalAuthRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<LocalAuthRequest>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Malformed IPC JSON: {Error}", ex.Message);
                    AppendServiceLog($"Malformed IPC JSON received: {ex.Message}");
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new LocalAuthResponse(1, Guid.NewGuid().ToString("N"), LocalAuthStatus.Error, "Malformed JSON request")));
                    return;
                }

                if (request == null || string.IsNullOrWhiteSpace(request.request_id))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new LocalAuthResponse(1, Guid.NewGuid().ToString("N"), LocalAuthStatus.Error, "Invalid request structure")));
                    return;
                }

                if (request.command != "request_unlock")
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new LocalAuthResponse(1, request.request_id, LocalAuthStatus.Error, $"Unsupported command: {request.command}")));
                    return;
                }

                var response = await ProcessAuthRequestAsync(request, writer, stoppingToken);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            catch (OperationCanceledException)
            {
                // Graceful cancellation
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Exception handling client connection");
                AppendServiceLog($"Exception processing client connection: {ex.Message}");
                try
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new LocalAuthResponse(1, Guid.NewGuid().ToString("N"), LocalAuthStatus.Error, "Internal service error")));
                }
                catch
                {
                    // Ignore write failures on closed pipe
                }
            }
        }
    }

    private async Task<LocalAuthResponse> ProcessAuthRequestAsync(LocalAuthRequest req, StreamWriter writer, CancellationToken stoppingToken)
    {
        var requestId = req.request_id;
        var start = DateTimeOffset.UtcNow;
        AppendServiceLog($"[REQUEST START] request_id={requestId} usage={req.usage} username={req.username ?? "(none)"}");

        // 1. Check if another auth request is active
        if (!await _authLock.WaitAsync(TimeSpan.FromMilliseconds(100), stoppingToken))
        {
            AppendServiceLog($"[BUSY] request_id={requestId} - another authentication is in progress");
            return new LocalAuthResponse(1, requestId, LocalAuthStatus.Busy, "Another authentication request is in progress");
        }

        try
        {
            // 2. Load and validate local config
            var cfg = _configStore.Load();
            if (string.IsNullOrWhiteSpace(cfg.DeviceId) || string.IsNullOrWhiteSpace(cfg.DevicePublicKeyPem) || string.IsNullOrWhiteSpace(cfg.PcToken))
            {
                AppendServiceLog($"[NOT PAIRED] request_id={requestId} - PC is not paired with an iPhone");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.NotPaired, "FaceUnlock is not paired on this PC");
            }

            var deviceId = cfg.DeviceId;
            var devicePubKey = cfg.DevicePublicKeyPem;
            var api = new ApiClient(cfg);

            // Send pending progress to client
            await writer.WriteLineAsync(JsonSerializer.Serialize(new LocalAuthResponse(1, requestId, LocalAuthStatus.Pending, "Waiting for iPhone Face ID..."), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            // 3. Try Online Unlock Flow first
            try
            {
                _log.LogInformation("Attempting online unlock for request {RequestId}...", requestId);
                var onlineResp = await TryOnlineUnlockAsync(api, cfg, deviceId, devicePubKey, stoppingToken);

                if (onlineResp.Status == LocalAuthStatus.Approved)
                {
                    var duration = (DateTimeOffset.UtcNow - start).TotalSeconds;
                    RecordGrant(requestId, onlineResp.ExpiresAt ?? (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30));
                    AppendServiceLog($"[APPROVED] request_id={requestId} device_id={deviceId} transport=Online duration={duration:F2}s verify=PASS");
                    return new LocalAuthResponse(1, requestId, LocalAuthStatus.Approved, "Face ID approved via Online service", onlineResp.ExpiresAt);
                }
                else if (onlineResp.Status is LocalAuthStatus.Rejected or LocalAuthStatus.Timeout)
                {
                    var duration = (DateTimeOffset.UtcNow - start).TotalSeconds;
                    AppendServiceLog($"[{onlineResp.Status.ToUpperInvariant()}] request_id={requestId} device_id={deviceId} transport=Online duration={duration:F2}s");
                    return new LocalAuthResponse(1, requestId, onlineResp.Status, onlineResp.Message);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Online unlock attempt failed for request {RequestId}, falling back to BLE", requestId);
                AppendServiceLog($"Online transport failed for request_id={requestId}: {ex.Message}. Falling back to BLE.");
            }

            // 4. Fallback to BLE Offline Flow if Online was unavailable or errored out
            try
            {
                _log.LogInformation("Attempting BLE offline unlock for request {RequestId}...", requestId);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new LocalAuthResponse(1, requestId, LocalAuthStatus.Pending, "Scanning for iPhone via BLE..."), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

                var bleResp = await TryBleUnlockAsync(cfg, deviceId, devicePubKey, stoppingToken);
                var duration = (DateTimeOffset.UtcNow - start).TotalSeconds;

                if (bleResp.Status == LocalAuthStatus.Approved)
                {
                    RecordGrant(requestId, bleResp.ExpiresAt ?? (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30));
                    AppendServiceLog($"[APPROVED] request_id={requestId} device_id={deviceId} transport=BLE duration={duration:F2}s verify=PASS");
                    return new LocalAuthResponse(1, requestId, LocalAuthStatus.Approved, "Face ID approved via Bluetooth LE", bleResp.ExpiresAt);
                }
                else
                {
                    AppendServiceLog($"[{bleResp.Status.ToUpperInvariant()}] request_id={requestId} device_id={deviceId} transport=BLE duration={duration:F2}s error={bleResp.Message}");
                    return new LocalAuthResponse(1, requestId, bleResp.Status, bleResp.Message ?? "BLE authentication failed");
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "BLE offline unlock failed for request {RequestId}", requestId);
                AppendServiceLog($"BLE transport exception for request_id={requestId}: {ex.Message}");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.Error, "Bluetooth authentication failed or iPhone not nearby");
            }
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<(string Status, string? Message, long? ExpiresAt)> TryOnlineUnlockAsync(
        ApiClient api,
        LocalConfig cfg,
        string deviceId,
        string devicePubKey,
        CancellationToken stoppingToken)
    {
        var r = await api.RequestUnlockAsync(deviceId, stoppingToken);
        var deadline = DateTimeOffset.FromUnixTimeSeconds(r.expires_at);

        while (DateTimeOffset.UtcNow < deadline && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
            var s = await api.GetUnlockStatusAsync(r.session_id, stoppingToken);

            if (s.status == "APPROVED")
            {
                var canonical = Protocol.Canonical(s.session_id, s.challenge, cfg.PcId, s.expires_at);
                var pubFp = KeyStore.ComputeFingerprint(devicePubKey);

                if (!string.IsNullOrWhiteSpace(s.device_public_key_pem))
                {
                    var respFp = KeyStore.ComputeFingerprint(s.device_public_key_pem);
                    if (respFp != pubFp)
                    {
                        _log.LogError("Device public key mismatch: server={ServerKeyFp} expected={ExpectedKeyFp}", respFp, pubFp);
                        return (LocalAuthStatus.Error, "Device public key fingerprint mismatch", null);
                    }
                }

                if (string.IsNullOrWhiteSpace(s.signature) || !KeyStore.VerifyPem(devicePubKey, canonical, s.signature))
                {
                    _log.LogError("Server says APPROVED but signature verification failed");
                    return (LocalAuthStatus.Error, "Invalid iPhone ECDSA signature", null);
                }

                return (LocalAuthStatus.Approved, null, s.expires_at);
            }
            else if (s.status == "REJECTED")
            {
                return (LocalAuthStatus.Rejected, "Face ID rejected by user", null);
            }
            else if (s.status == "EXPIRED")
            {
                return (LocalAuthStatus.Timeout, "Unlock session expired", null);
            }
        }

        return (LocalAuthStatus.Timeout, "Request timed out waiting for iPhone Face ID", null);
    }

    private async Task<(string Status, string? Message, long? ExpiresAt)> TryBleUnlockAsync(
        LocalConfig cfg,
        string deviceId,
        string devicePubKey,
        CancellationToken stoppingToken)
    {
        var session = Guid.NewGuid().ToString("N");
        var challenge = Protocol.RandomToken();
        var exp = DateTimeOffset.UtcNow.AddSeconds(45).ToUnixTimeSeconds();
        var msg = Protocol.OfflineRequestCanonical(session, challenge, cfg.PcId, exp);
        var payload = new OfflineUnlockPayload(
            "faceunlock-offline-v1",
            session,
            cfg.PcId,
            cfg.PcName,
            challenge,
            exp,
            _keyStore.SignBase64(msg)
        );

        using var bleCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        bleCts.CancelAfter(TimeSpan.FromSeconds(15));

        var result = await _bleScanner.DiscoverAndApproveAsync(payload, deviceId, TimeSpan.FromSeconds(15), bleCts.Token);
        if (result == null)
        {
            return (LocalAuthStatus.Timeout, "iPhone BLE peripheral not found or timed out", null);
        }

        if (result.ok == "true" && !string.IsNullOrWhiteSpace(result.signature))
        {
            var canonical = Protocol.Canonical(session, challenge, cfg.PcId, exp);
            if (!KeyStore.VerifyPem(devicePubKey, canonical, result.signature))
            {
                _log.LogError("BLE Face ID approval has invalid signature");
                return (LocalAuthStatus.Error, "Invalid iPhone BLE signature", null);
            }

            return (LocalAuthStatus.Approved, null, exp);
        }

        return (LocalAuthStatus.Rejected, result.error ?? "BLE approval rejected", null);
    }

    private void RecordGrant(string requestId, long expiresAt)
    {
        lock (_activeGrants)
        {
            // Prune expired grants
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expiredKeys = _activeGrants.Where(kvp => kvp.Value.ExpiresAt < now).Select(kvp => kvp.Key).ToList();
            foreach (var k in expiredKeys) _activeGrants.Remove(k);

            _activeGrants[requestId] = (DateTimeOffset.UtcNow, expiresAt);
        }
    }
}
