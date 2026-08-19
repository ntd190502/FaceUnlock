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
    private readonly string _pipeName;
    private const string ServiceVersion = "1.3.0";
    private const long MaxLogSizeBytes = 5 * 1024 * 1024; // 5 MB

    private readonly ILogger<UnlockWorker> _log;
    private readonly ConfigStore _configStore;
    private readonly LsaMachineSecretStore _lsaSecretStore;
    private readonly KeyStore _keyStore;
    private readonly BleScanner _bleScanner;
    private readonly IBluetoothRadioManager _bluetoothRadioManager;
    private readonly WindowsInternetMonitor _internetMonitor;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly string _logDir;
    private readonly string _serviceLogFile;
    private readonly string _serviceLogBackupFile;

    // Active auth session tracking for cancel_request
    private readonly object _sessionSync = new();
    private string? _currentActiveRequestId;
    private string? _currentActiveIdentity;
    private CancellationTokenSource? _currentActiveCts;

    private enum GrantState
    {
        Pending,
        WaitingConnectivity,
        Approved,
        Reserved,
        Consumed,
        Rejected,
        Timeout,
        Cancelled,
        NotPaired,
        Error
    }

    private sealed class AuthGrant
    {
        public required string RequestId { get; init; }
        public required DateTimeOffset ApprovedAt { get; init; }
        public required long ExpiresAt { get; init; }
        public GrantState State { get; set; } = GrantState.Approved;
        public string? LastMessage { get; set; }
        public string? UserSid { get; set; }
        public string? QualifiedUsername { get; set; }
        public string? DeviceId { get; set; }
        public int? SessionId { get; set; }
        public string? ClientType { get; set; }
        public string? PcId { get; set; }
    }

    // In-memory grant cache: requestId -> AuthGrant
    private readonly Dictionary<string, AuthGrant> _activeGrants = new();

    public UnlockWorker(ILogger<UnlockWorker> log, string pipeName = "FaceUnlock.Auth.v1")
    {
        _log = log;
        _pipeName = pipeName;
        _configStore = new ConfigStore();
        _lsaSecretStore = new LsaMachineSecretStore();
        _keyStore = new KeyStore();
        _bluetoothRadioManager = new WindowsBluetoothRadioManager();
        _bleScanner = new BleScanner(_bluetoothRadioManager);
        _internetMonitor = new WindowsInternetMonitor();
        _internetMonitor.StateChanged += (_, state) => AppendServiceLog($"[INTERNET STATE] {state}");

        _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceUnlock", "logs");
        _serviceLogFile = Path.Combine(_logDir, "service.log");
        _serviceLogBackupFile = Path.Combine(_logDir, "service.log.1");

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
            lock (_sessionSync)
            {
                if (File.Exists(_serviceLogFile))
                {
                    var fileInfo = new FileInfo(_serviceLogFile);
                    if (fileInfo.Length >= MaxLogSizeBytes)
                    {
                        try
                        {
                            if (File.Exists(_serviceLogBackupFile))
                            {
                                File.Delete(_serviceLogBackupFile);
                            }
                            File.Move(_serviceLogFile, _serviceLogBackupFile);
                        }
                        catch
                        {
                            // Ignore rotation errors and keep logging
                        }
                    }
                }

                var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ}] {message}{Environment.NewLine}";
                File.AppendAllText(_serviceLogFile, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Ignore file logging failures to keep service robust
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("FaceUnlock Local Authentication Broker Service starting (v{Version})...", ServiceVersion);
        AppendServiceLog($"FaceUnlock Service v{ServiceVersion} started. Listening on named pipe: FaceUnlock.Auth.v1");

        // 1. Initialize and ensure LSA machine secret is present for passwordless authentication
        try
        {
            var (secret, status, error) = _lsaSecretStore.LoadOrCreate();
            if (status == LsaSecretStatus.Created)
            {
                _log.LogInformation("[LSA SECRET] created at {Path}", _lsaSecretStore.SecretFilePath);
                AppendServiceLog($"[LSA SECRET] created ({_lsaSecretStore.SecretFilePath})");
            }
            else if (status == LsaSecretStatus.Loaded)
            {
                _log.LogInformation("[LSA SECRET] loaded from {Path}", _lsaSecretStore.SecretFilePath);
                AppendServiceLog($"[LSA SECRET] loaded ({_lsaSecretStore.SecretFilePath})");
            }
            else
            {
                _log.LogError("[LSA SECRET ERROR] status={Status} message={Error}", status, error);
                AppendServiceLog($"[LSA SECRET ERROR] status={status} message={error}");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[LSA SECRET ERROR] {Message}", ex.Message);
            AppendServiceLog($"[LSA SECRET ERROR] exception={ex.Message}");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                var pipeSecurity = CreatePipeSecurity();
                pipe = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 4096,
                    pipeSecurity
                );

                await pipe.WaitForConnectionAsync(stoppingToken);

                // Pass ownership of pipe directly into the worker task
                var clientPipe = pipe;
                pipe = null; // Do not dispose here
                _ = Task.Run(() => HandleClientConnectionAsync(clientPipe, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _log.LogError(ex, "Named pipe server error");
                AppendServiceLog($"Named pipe listener exception: {ex.Message}");
                await Task.Delay(200, stoppingToken);
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

            string opName = "READ";
            string? currentReqId = null;

            try
            {
                AppendServiceLog("[PIPE CONNECTED]");

                opName = "READ";
                var line = await reader.ReadLineAsync(stoppingToken);
                if (string.IsNullOrWhiteSpace(line))
                {
                    opName = "WRITE_EMPTY";
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
                    AppendServiceLog($"[MALFORMED_JSON] {ex.Message}");
                    opName = "WRITE_MALFORMED";
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new LocalAuthResponse(1, Guid.NewGuid().ToString("N"), LocalAuthStatus.Error, "Malformed JSON request")));
                    return;
                }

                if (request == null)
                {
                    opName = "WRITE_INVALID";
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new LocalAuthResponse(1, Guid.NewGuid().ToString("N"), LocalAuthStatus.Error, "Invalid request structure")));
                    return;
                }

                currentReqId = request.request_id;
                AppendServiceLog($"[REQUEST RECEIVED] command={request.command} request_id={currentReqId ?? "(none)"} user_sid={request.user_sid ?? "(none)"} qualified_username={request.qualified_username ?? request.username ?? "(none)"}");

                // Handle ping command (health check)
                if (request.command == "ping")
                {
                    var reqId = string.IsNullOrWhiteSpace(request.request_id) ? Guid.NewGuid().ToString("N") : request.request_id;
                    opName = "WRITE_PING";
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new LocalAuthResponse(1, reqId, LocalAuthStatus.Ok, "FaceUnlock Service is healthy", null, ServiceVersion), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    AppendServiceLog($"[ACK WRITTEN] ping request_id={reqId}");
                    return;
                }

                if (string.IsNullOrWhiteSpace(request.request_id))
                {
                    opName = "WRITE_MISSING_REQ_ID";
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new LocalAuthResponse(1, Guid.NewGuid().ToString("N"), LocalAuthStatus.Error, "Missing request_id")));
                    return;
                }

                // Handle cancel_request command
                if (request.command == "cancel_request")
                {
                    var cancelResp = CancelRequest(request.request_id);
                    opName = "WRITE_CANCEL";
                    await writer.WriteLineAsync(JsonSerializer.Serialize(cancelResp, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    AppendServiceLog($"[ACK WRITTEN] cancel_request request_id={request.request_id}");
                    return;
                }

                // Handle grant lifecycle commands
                if (request.command == "reserve_grant")
                {
                    var reserveResp = ReserveGrant(request);
                    opName = "WRITE_RESERVE";
                    await writer.WriteLineAsync(JsonSerializer.Serialize(reserveResp, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    AppendServiceLog($"[ACK WRITTEN] reserve_grant request_id={request.request_id} status={reserveResp.status}");
                    return;
                }

                if (request.command == "release_grant")
                {
                    var releaseResp = ReleaseGrant(request);
                    opName = "WRITE_RELEASE";
                    await writer.WriteLineAsync(JsonSerializer.Serialize(releaseResp, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    AppendServiceLog($"[ACK WRITTEN] release_grant request_id={request.request_id} status={releaseResp.status}");
                    return;
                }

                if (request.command == "consume_grant")
                {
                    var consumeResp = ConsumeGrant(request);
                    opName = "WRITE_CONSUME";
                    await writer.WriteLineAsync(JsonSerializer.Serialize(consumeResp, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    AppendServiceLog($"[ACK WRITTEN] consume_grant request_id={request.request_id} status={consumeResp.status}");
                    return;
                }

                if (request.command == "issue_lsa_ticket")
                {
                    var ticketResp = IssueLsaTicket(request);
                    opName = "WRITE_ISSUE_LSA_TICKET";
                    await writer.WriteLineAsync(JsonSerializer.Serialize(ticketResp, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    AppendServiceLog($"[ACK WRITTEN] issue_lsa_ticket request_id={request.request_id} status={ticketResp.status}");
                    return;
                }

                if (request.command == "grant_status")
                {
                    var statusResp = GetGrantStatus(request);
                    opName = "WRITE_GRANT_STATUS";
                    await writer.WriteLineAsync(JsonSerializer.Serialize(statusResp, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    return;
                }

                if (request.command == "request_unlock")
                {
                    // Start auth in background task (or queue) and respond with ACK immediately
                    var ackResp = StartAuthRequest(request, stoppingToken);
                    opName = "WRITE_REQUEST_UNLOCK_ACK";
                    await writer.WriteLineAsync(JsonSerializer.Serialize(ackResp, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    AppendServiceLog($"[ACK WRITTEN] request_unlock request_id={request.request_id} status={ackResp.status}");
                    return;
                }

                opName = "WRITE_UNSUPPORTED";
                await writer.WriteLineAsync(JsonSerializer.Serialize(new LocalAuthResponse(1, request.request_id, LocalAuthStatus.Error, $"Unsupported command: {request.command}")));
            }
            catch (OperationCanceledException)
            {
                // Graceful cancellation
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Exception handling client connection (op={Op}, reqId={ReqId})", opName, currentReqId);
                AppendServiceLog($"[PIPE EXCEPTION] op={opName} request_id={currentReqId ?? "(none)"} type={ex.GetType().Name} message={ex.Message}");
                try
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new LocalAuthResponse(1, currentReqId ?? Guid.NewGuid().ToString("N"), LocalAuthStatus.Error, $"Internal service error: {ex.Message}")));
                }
                catch
                {
                    // Ignore write failures on closed pipe
                }
            }
            finally
            {
                try
                {
                    await writer.FlushAsync();
                }
                catch
                {
                }
                AppendServiceLog("[PIPE DISCONNECTED]");
            }
        }
    }

    private LocalAuthResponse CancelRequest(string requestId)
    {
        lock (_sessionSync)
        {
            if (_currentActiveRequestId == requestId && _currentActiveCts != null)
            {
                try
                {
                    _currentActiveCts.Cancel();
                    AppendServiceLog($"[CANCEL_REQUEST SUCCESS] request_id={requestId} active auth cancelled");
                    return new LocalAuthResponse(1, requestId, LocalAuthStatus.Cancelled, "Authentication request cancelled");
                }
                catch (Exception ex)
                {
                    AppendServiceLog($"[CANCEL_REQUEST ERROR] request_id={requestId} error={ex.Message}");
                }
            }
        }

        // Also release any active grant for this request_id
        lock (_activeGrants)
        {
            _activeGrants.Remove(requestId);
        }

        AppendServiceLog($"[CANCEL_REQUEST ACK] request_id={requestId}");
        return new LocalAuthResponse(1, requestId, LocalAuthStatus.Cancelled, "Request cancelled or not active");
    }

    private LocalAuthResponse ReserveGrant(LocalAuthRequest req)
    {
        var requestId = req.request_id;
        lock (_activeGrants)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            PruneExpiredGrants(now);

            if (!_activeGrants.TryGetValue(requestId, out var grant))
            {
                AppendServiceLog($"[RESERVE_GRANT NOT_FOUND] request_id={requestId}");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.NotFound, "Grant not found or expired");
            }

            if (grant.ExpiresAt < now)
            {
                _activeGrants.Remove(requestId);
                AppendServiceLog($"[RESERVE_GRANT EXPIRED] request_id={requestId}");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.Expired, "Grant expired (>30s)");
            }

            if (grant.State == GrantState.Consumed)
            {
                _activeGrants.Remove(requestId);
                AppendServiceLog($"[RESERVE_GRANT CONSUMED] request_id={requestId}");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.Rejected, "Grant already consumed");
            }

            // SID binding verification
            if (!string.IsNullOrWhiteSpace(req.user_sid) && !string.IsNullOrWhiteSpace(grant.UserSid))
            {
                if (!string.Equals(req.user_sid, grant.UserSid, StringComparison.OrdinalIgnoreCase))
                {
                    AppendServiceLog($"[RESERVE_GRANT SID_MISMATCH] request_id={requestId} req_sid={req.user_sid} grant_sid={grant.UserSid}");
                    return new LocalAuthResponse(1, requestId, LocalAuthStatus.Rejected, "User SID mismatch");
                }
            }

            // Session ID binding verification
            if (req.session_id.HasValue && grant.SessionId.HasValue)
            {
                if (req.session_id.Value != grant.SessionId.Value)
                {
                    AppendServiceLog($"[RESERVE_GRANT SESSION_MISMATCH] request_id={requestId} req_session={req.session_id.Value} grant_session={grant.SessionId.Value}");
                    return new LocalAuthResponse(1, requestId, LocalAuthStatus.Rejected, "Windows Session ID mismatch");
                }
            }

            grant.State = GrantState.Reserved;
            if (string.Equals(req.client_type, "shell", StringComparison.OrdinalIgnoreCase))
            {
                AppendServiceLog($"[SHELL GRANT RESERVED] request_id={requestId} user_sid={grant.UserSid ?? req.user_sid ?? "(none)"} session_id={grant.SessionId ?? req.session_id ?? -1}");
            }
            else
            {
                AppendServiceLog($"[RESERVE_GRANT SUCCESS] request_id={requestId}");
            }

            return new LocalAuthResponse(1, requestId, LocalAuthStatus.Reserved, "grant_reserved", grant.ExpiresAt, null, null, grant.UserSid, grant.SessionId);
        }
    }

    private LocalAuthResponse ReleaseGrant(LocalAuthRequest req)
    {
        var requestId = req.request_id;
        lock (_activeGrants)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            PruneExpiredGrants(now);

            if (!_activeGrants.TryGetValue(requestId, out var grant))
            {
                AppendServiceLog($"[RELEASE_GRANT NOT_FOUND] request_id={requestId}");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.NotFound, "Grant not found or expired");
            }

            if (grant.ExpiresAt < now)
            {
                _activeGrants.Remove(requestId);
                AppendServiceLog($"[RELEASE_GRANT EXPIRED] request_id={requestId}");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.Expired, "Grant expired (>30s)");
            }

            if (grant.State == GrantState.Consumed)
            {
                _activeGrants.Remove(requestId);
                AppendServiceLog($"[RELEASE_GRANT ALREADY_CONSUMED] request_id={requestId}");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.Rejected, "Grant already consumed");
            }

            // Return grant to Approved state so user can retry password within remaining TTL
            grant.State = GrantState.Approved;
            AppendServiceLog($"[RELEASE_GRANT SUCCESS] request_id={requestId} returned to Approved state, expires_in={grant.ExpiresAt - now}s");
            return new LocalAuthResponse(1, requestId, LocalAuthStatus.Approved, "grant_released", grant.ExpiresAt, null, null, grant.UserSid, grant.SessionId);
        }
    }

    private LocalAuthResponse ConsumeGrant(LocalAuthRequest req)
    {
        var requestId = req.request_id;
        lock (_activeGrants)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            PruneExpiredGrants(now);

            if (!_activeGrants.TryGetValue(requestId, out var grant))
            {
                AppendServiceLog($"[CONSUME_GRANT NOT_FOUND] request_id={requestId}");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.NotFound, "Grant not found or already consumed");
            }

            if (grant.State == GrantState.Consumed)
            {
                _activeGrants.Remove(requestId);
                AppendServiceLog($"[CONSUME_GRANT ALREADY_CONSUMED] request_id={requestId}");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.Rejected, "Grant already consumed");
            }

            if (grant.ExpiresAt < now)
            {
                _activeGrants.Remove(requestId);
                AppendServiceLog($"[CONSUME_GRANT EXPIRED] request_id={requestId}");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.Expired, "Grant expired (>30s)");
            }

            // SID binding verification
            if (!string.IsNullOrWhiteSpace(req.user_sid) && !string.IsNullOrWhiteSpace(grant.UserSid))
            {
                if (!string.Equals(req.user_sid, grant.UserSid, StringComparison.OrdinalIgnoreCase))
                {
                    AppendServiceLog($"[CONSUME_GRANT SID_MISMATCH] request_id={requestId} req_sid={req.user_sid} grant_sid={grant.UserSid}");
                    return new LocalAuthResponse(1, requestId, LocalAuthStatus.Rejected, "User SID mismatch");
                }
            }

            // Session ID binding verification
            if (req.session_id.HasValue && grant.SessionId.HasValue)
            {
                if (req.session_id.Value != grant.SessionId.Value)
                {
                    AppendServiceLog($"[CONSUME_GRANT SESSION_MISMATCH] request_id={requestId} req_session={req.session_id.Value} grant_session={grant.SessionId.Value}");
                    return new LocalAuthResponse(1, requestId, LocalAuthStatus.Rejected, "Windows Session ID mismatch");
                }
            }

            // Grant is valid: consume immediately and remove
            grant.State = GrantState.Consumed;
            _activeGrants.Remove(requestId);

            if (string.Equals(req.client_type, "shell", StringComparison.OrdinalIgnoreCase))
            {
                AppendServiceLog($"[SHELL GRANT CONSUMED] request_id={requestId} user_sid={grant.UserSid ?? req.user_sid ?? "(none)"} session_id={grant.SessionId ?? req.session_id ?? -1}");
                AppendServiceLog($"[SHELL DESKTOP RELEASED] request_id={requestId}");
            }
            else
            {
                AppendServiceLog($"[CONSUME_GRANT SUCCESS] request_id={requestId}");
            }

            return new LocalAuthResponse(1, requestId, LocalAuthStatus.Consumed, "grant_consumed", grant.ExpiresAt, null, null, grant.UserSid, grant.SessionId);
        }
    }

    private LocalAuthResponse GetGrantStatus(LocalAuthRequest req)
    {
        var requestId = req.request_id;
        lock (_activeGrants)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            PruneExpiredGrants(now);

            if (!_activeGrants.TryGetValue(requestId, out var grant))
            {
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.NotFound, "Grant not found or expired");
            }

            if (grant.ExpiresAt < now && grant.State != GrantState.Approved && grant.State != GrantState.Reserved)
            {
                _activeGrants.Remove(requestId);
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.Expired, "Grant expired");
            }

            var statusStr = grant.State switch
            {
                GrantState.Pending => LocalAuthStatus.Pending,
                GrantState.WaitingConnectivity => LocalAuthStatus.WaitingConnectivity,
                GrantState.Approved => LocalAuthStatus.Approved,
                GrantState.Reserved => LocalAuthStatus.Reserved,
                GrantState.Consumed => LocalAuthStatus.Consumed,
                GrantState.Rejected => LocalAuthStatus.Rejected,
                GrantState.Timeout => LocalAuthStatus.Timeout,
                GrantState.Cancelled => LocalAuthStatus.Cancelled,
                GrantState.NotPaired => LocalAuthStatus.NotPaired,
                _ => LocalAuthStatus.Error
            };

            return new LocalAuthResponse(1, requestId, statusStr, grant.LastMessage ?? "Grant active", grant.ExpiresAt, null, null, grant.UserSid, grant.SessionId);
        }
    }

    private void PruneExpiredGrants(long nowSec)
    {
        var expiredKeys = _activeGrants.Where(kvp => kvp.Value.ExpiresAt < nowSec).Select(kvp => kvp.Key).ToList();
        foreach (var k in expiredKeys) _activeGrants.Remove(k);
    }

    private LocalAuthResponse StartAuthRequest(LocalAuthRequest req, CancellationToken stoppingToken)
    {
        var requestId = req.request_id;
        if (string.Equals(req.client_type, "shell", StringComparison.OrdinalIgnoreCase))
        {
            AppendServiceLog($"[SHELL AUTH START] request_id={requestId} user_sid={req.user_sid ?? "(none)"} session_id={req.session_id ?? -1}");
        }
        else
        {
            AppendServiceLog($"[REQUEST START] request_id={requestId} usage={req.usage} user_sid={req.user_sid ?? "(none)"} qualified_username={req.qualified_username ?? req.username ?? "(none)"}");
        }

        // Check local config first
        var cfg = _configStore.Load();
        if (string.IsNullOrWhiteSpace(cfg.DeviceId) || string.IsNullOrWhiteSpace(cfg.DevicePublicKeyPem) || string.IsNullOrWhiteSpace(cfg.PcToken))
        {
            AppendServiceLog($"[NOT PAIRED] request_id={requestId} - PC is not paired with an iPhone");
            lock (_activeGrants)
            {
                _activeGrants[requestId] = new AuthGrant
                {
                    RequestId = requestId,
                    ApprovedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds(),
                    State = GrantState.NotPaired,
                    LastMessage = "FaceUnlock is not paired on this PC",
                    UserSid = req.user_sid,
                    SessionId = req.session_id,
                    ClientType = req.client_type,
                    PcId = req.pc_id
                };
            }
            return new LocalAuthResponse(1, requestId, LocalAuthStatus.NotPaired, "FaceUnlock is not paired on this PC");
        }

        lock (_sessionSync)
        {
            var identity = RequestIdentity.From(req);
            // If the same request is already in progress, return Pending ACK
            if (_currentActiveRequestId == requestId && _currentActiveIdentity == identity && _currentActiveCts != null && !_currentActiveCts.IsCancellationRequested)
            {
                AppendServiceLog($"[REQUEST RE-ENTER] request_id={requestId} already active");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.Pending, "Authentication in progress");
            }

            lock (_activeGrants)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                PruneExpiredGrants(now);
                if (_activeGrants.TryGetValue(requestId, out var existing))
                {
                    var sameBinding = string.Equals(existing.UserSid, req.user_sid, StringComparison.OrdinalIgnoreCase)
                        && existing.SessionId == req.session_id
                        && string.Equals(existing.ClientType, req.client_type, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existing.PcId, req.pc_id, StringComparison.Ordinal);
                    if (!sameBinding)
                    {
                        AppendServiceLog($"[REQUEST DEDUP REJECTED] request_id={requestId} binding mismatch");
                        return new LocalAuthResponse(1, requestId, LocalAuthStatus.Rejected, "Duplicate request_id has different security bindings");
                    }

                    var status = existing.State switch
                    {
                        GrantState.Pending => LocalAuthStatus.Pending,
                        GrantState.WaitingConnectivity => LocalAuthStatus.WaitingConnectivity,
                        GrantState.Approved => LocalAuthStatus.Approved,
                        GrantState.Reserved => LocalAuthStatus.Reserved,
                        GrantState.Rejected => LocalAuthStatus.Rejected,
                        GrantState.Timeout => LocalAuthStatus.Timeout,
                        GrantState.Cancelled => LocalAuthStatus.Cancelled,
                        GrantState.NotPaired => LocalAuthStatus.NotPaired,
                        _ => LocalAuthStatus.Error
                    };
                    AppendServiceLog($"[REQUEST DEDUP HIT] request_id={requestId} state={existing.State}");
                    return new LocalAuthResponse(1, requestId, status, existing.LastMessage, existing.ExpiresAt);
                }
            }

            // If another request is active, cancel it or return busy
            if (_currentActiveRequestId != null && _currentActiveCts != null && !_currentActiveCts.IsCancellationRequested)
            {
                AppendServiceLog($"[BUSY] request_id={requestId} - cancelling previous active request {_currentActiveRequestId}");
                try { _currentActiveCts.Cancel(); } catch { }
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _currentActiveRequestId = requestId;
            _currentActiveIdentity = identity;
            _currentActiveCts = cts;

            lock (_activeGrants)
            {
                _activeGrants[requestId] = new AuthGrant
                {
                    RequestId = requestId,
                    ApprovedAt = DateTimeOffset.UtcNow,
                    // A pending Phase F.1 connectivity wait is explicitly
                    // unbounded. Approved grants still retain their strict 30s TTL.
                    ExpiresAt = DateTimeOffset.MaxValue.ToUnixTimeSeconds(),
                    State = GrantState.Pending,
                    LastMessage = "Waiting for iPhone Face ID...",
                    UserSid = req.user_sid,
                    QualifiedUsername = req.qualified_username ?? req.username,
                    SessionId = req.session_id,
                    ClientType = req.client_type,
                    PcId = req.pc_id
                };
            }

            // Spawn background task to process auth and update _activeGrants[requestId]
            _ = Task.Run(() => RunAuthFlowAsync(req, cfg, cts.Token), CancellationToken.None);

            return new LocalAuthResponse(1, requestId, LocalAuthStatus.Pending, "Waiting for iPhone Face ID...");
        }
    }

    private async Task RunAuthFlowAsync(LocalAuthRequest req, LocalConfig cfg, CancellationToken cancellationToken)
    {
        var requestId = req.request_id;
        var start = DateTimeOffset.UtcNow;
        var deviceId = cfg.DeviceId!;
        var devicePubKey = cfg.DevicePublicKeyPem!;
        var api = new ApiClient(cfg);
        var onlineAttempted = false;

        try
        {
            // 1. Try Online Unlock Flow first
            if (_internetMonitor.Current != InternetState.Offline)
            {
                try
                {
                    onlineAttempted = true;
                    _log.LogInformation("Attempting online unlock for request {RequestId}...", requestId);
                    AppendServiceLog($"[TRANSPORT SELECTED] request_id={requestId} transport=Online internet={_internetMonitor.Current}");
                    var onlineResp = await TryOnlineUnlockAsync(api, cfg, deviceId, devicePubKey, cancellationToken);

                if (onlineResp.Status == LocalAuthStatus.Approved)
                {
                    var duration = (DateTimeOffset.UtcNow - start).TotalSeconds;
                    var localGrantExp = RecordGrant(requestId, req.user_sid, req.qualified_username ?? req.username, deviceId, req.session_id, req.client_type, req.pc_id);
                    if (string.Equals(req.client_type, "shell", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendServiceLog($"[SHELL AUTH APPROVED] request_id={requestId} user_sid={req.user_sid ?? "(none)"} session_id={req.session_id ?? -1} transport=Online duration={duration:F2}s");
                    }
                    else
                    {
                        AppendServiceLog($"[APPROVED] request_id={requestId} device_id={deviceId} transport=Online duration={duration:F2}s verify=PASS grant_ttl=30s");
                    }
                    return;
                }
                    else if (onlineResp.Status == LocalAuthStatus.Rejected)
                    {
                    var duration = (DateTimeOffset.UtcNow - start).TotalSeconds;
                    AppendServiceLog($"[{onlineResp.Status.ToUpperInvariant()}] request_id={requestId} device_id={deviceId} transport=Online duration={duration:F2}s");
                    SetGrantTerminalState(requestId, onlineResp.Status, onlineResp.Message);
                        return;
                    }
                    AppendServiceLog($"[TRANSPORT SWITCH] request_id={requestId} Online->BLE reason={onlineResp.Status}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                AppendServiceLog($"[CANCELLED] request_id={requestId} online flow cancelled");
                SetGrantTerminalState(requestId, LocalAuthStatus.Cancelled, "Authentication cancelled");
                    return;
                }
                catch (Exception ex)
                {
                _log.LogWarning(ex, "Online unlock attempt failed for request {RequestId}, falling back to BLE", requestId);
                    AppendServiceLog($"[TRANSPORT SWITCH] request_id={requestId} Online->BLE reason={ex.Message}");
                }
            }
            else AppendServiceLog($"[TRANSPORT SELECTED] request_id={requestId} transport=BLE internet=Offline");

            // 2. Fallback to BLE Offline Flow
            try
            {
                _log.LogInformation("Attempting BLE offline unlock for request {RequestId}...", requestId);
                SetGrantMessage(requestId, "Scanning for iPhone via BLE...");

                var bleResp = await TryBleUnlockAsync(cfg, deviceId, devicePubKey, !onlineAttempted, cancellationToken);
                var duration = (DateTimeOffset.UtcNow - start).TotalSeconds;

                // If the request began offline and connectivity returned during the
                // bounded BLE attempts, make one online attempt. Never bounce back
                // to a transport that has already been attempted for this request.
                if (bleResp.Status == LocalAuthStatus.InternetRestored && !onlineAttempted)
                {
                    AppendServiceLog($"[TRANSPORT SWITCH] request_id={requestId} BLE->Online reason=InternetRestored");
                    SetGrantMessage(requestId, "Internet restored; contacting iPhone online...");
                    try
                    {
                        onlineAttempted = true;
                        var restoredOnlineResp = await TryOnlineUnlockAsync(api, cfg, deviceId, devicePubKey, cancellationToken);
                        if (restoredOnlineResp.Status == LocalAuthStatus.Approved)
                        {
                            duration = (DateTimeOffset.UtcNow - start).TotalSeconds;
                            RecordGrant(requestId, req.user_sid, req.qualified_username ?? req.username, deviceId, req.session_id, req.client_type, req.pc_id);
                            AppendServiceLog($"[APPROVED] request_id={requestId} device_id={deviceId} transport=Online duration={duration:F2}s verify=PASS grant_ttl=30s");
                            return;
                        }
                        bleResp = restoredOnlineResp;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        AppendServiceLog($"[TRANSPORT ERROR] request_id={requestId} transport=Online-after-BLE reason={ex.Message}");
                    }
                }

                if (bleResp.Status == LocalAuthStatus.Approved)
                {
                    var localGrantExp = RecordGrant(requestId, req.user_sid, req.qualified_username ?? req.username, deviceId, req.session_id, req.client_type, req.pc_id);
                    if (string.Equals(req.client_type, "shell", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendServiceLog($"[SHELL AUTH APPROVED] request_id={requestId} user_sid={req.user_sid ?? "(none)"} session_id={req.session_id ?? -1} transport=BLE duration={duration:F2}s");
                    }
                    else
                    {
                        AppendServiceLog($"[APPROVED] request_id={requestId} device_id={deviceId} transport=BLE duration={duration:F2}s verify=PASS grant_ttl=30s");
                    }
                    return;
                }
                else
                {
                    AppendServiceLog($"[{bleResp.Status.ToUpperInvariant()}] request_id={requestId} device_id={deviceId} transport=BLE duration={duration:F2}s error={bleResp.Message}");
                    SetGrantTerminalState(requestId, bleResp.Status, bleResp.Message ?? "BLE authentication failed");
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AppendServiceLog($"[CANCELLED] request_id={requestId} BLE flow cancelled");
                SetGrantTerminalState(requestId, LocalAuthStatus.Cancelled, "Authentication cancelled");
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "BLE offline unlock failed for request {RequestId}", requestId);
                AppendServiceLog($"BLE transport exception for request_id={requestId}: {ex.Message}");
                SetGrantTerminalState(requestId, LocalAuthStatus.Error, "Bluetooth authentication failed or iPhone not nearby");
            }
        }
        finally
        {
            lock (_sessionSync)
            {
                if (_currentActiveRequestId == requestId)
                {
                    _currentActiveRequestId = null;
                    _currentActiveIdentity = null;
                    _currentActiveCts = null;
                }
            }
        }
    }

    private void SetGrantMessage(string requestId, string message)
    {
        lock (_activeGrants)
        {
            if (_activeGrants.TryGetValue(requestId, out var grant))
            {
                if (grant.State == GrantState.WaitingConnectivity)
                    grant.State = GrantState.Pending;
                grant.LastMessage = message;
            }
        }
    }

    private void SetGrantTerminalState(string requestId, string status, string? message)
    {
        lock (_activeGrants)
        {
            if (_activeGrants.TryGetValue(requestId, out var grant))
            {
                grant.State = status switch
                {
                    LocalAuthStatus.Rejected => GrantState.Rejected,
                    LocalAuthStatus.Timeout => GrantState.Timeout,
                    LocalAuthStatus.Cancelled => GrantState.Cancelled,
                    LocalAuthStatus.NotPaired => GrantState.NotPaired,
                    _ => GrantState.Error
                };
                grant.LastMessage = message;
            }
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
            if (_internetMonitor.Current == InternetState.Offline)
                throw new HttpRequestException("Internet connection was lost");
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
        bool switchToOnlineWhenInternetRestored,
        CancellationToken stoppingToken)
    {
        var requestId = _currentActiveRequestId ?? "unknown";
        var loop = new BleConnectivityWaitLoop(_bluetoothRadioManager, _internetMonitor);
        var wait = await loop.WaitAsync(async scanToken =>
        {
            // Keep the IPC request and its one worker intact. Each 9-second scan
            // gets a fresh short-lived signed BLE payload so a late iPhone is safe.
            var session = Guid.NewGuid().ToString("N");
            var challenge = Protocol.RandomToken();
            var exp = DateTimeOffset.UtcNow.AddSeconds(45).ToUnixTimeSeconds();
            var canonical = Protocol.OfflineRequestCanonical(session, challenge, cfg.PcId, exp);
            var payload = new OfflineUnlockPayload("faceunlock-offline-v1", session, cfg.PcId, cfg.PcName, challenge, exp, _keyStore.SignBase64(canonical));
            var response = await _bleScanner.DiscoverAndApproveAsync(payload, deviceId, TimeSpan.FromSeconds(9), scanToken);
            return response is null ? null : new BleApprovalEnvelope(response, session, challenge, exp);
        }, switchToOnlineWhenInternetRestored, (state, attempt, radio) =>
        {
            if (state == BleWaitState.WaitingConnectivity)
            {
                var message = radio?.Message ?? "Bluetooth is unavailable";
                SetGrantWaitingConnectivity(requestId, message);
                AppendServiceLog($"[WAITING_CONNECTIVITY] request_id={requestId} bluetooth={radio?.State} message={message}");
            }
            else if (state == BleWaitState.Scanning)
            {
                SetGrantMessage(requestId, $"Scanning for paired iPhone via BLE (attempt {attempt})...");
                AppendServiceLog($"[BLE SCAN] request_id={requestId} attempt={attempt} duration_s=9");
            }
        }, stoppingToken);

        if (wait.Outcome == BleWaitOutcome.InternetRestored)
            return (LocalAuthStatus.InternetRestored, "Internet connection restored", null);

        var envelope = wait.Response!;
        var result = envelope.Response;

        if (result.ok == "true" && !string.IsNullOrWhiteSpace(result.signature))
        {
            var canonical = Protocol.Canonical(envelope.Session, envelope.Challenge, cfg.PcId, envelope.ExpiresAt);
            if (!KeyStore.VerifyPem(devicePubKey, canonical, result.signature))
            {
                _log.LogError("BLE Face ID approval has invalid signature");
                return (LocalAuthStatus.Error, "Invalid iPhone BLE signature", null);
            }

            return (LocalAuthStatus.Approved, null, envelope.ExpiresAt);
        }

        return (LocalAuthStatus.Rejected, result.error ?? "BLE approval rejected", null);
    }

    private sealed record BleApprovalEnvelope(OfflineBleResponse Response, string Session, string Challenge, long ExpiresAt);

    private void SetGrantWaitingConnectivity(string requestId, string message)
    {
        lock (_activeGrants)
        {
            if (_activeGrants.TryGetValue(requestId, out var grant))
            {
                grant.State = GrantState.WaitingConnectivity;
                grant.LastMessage = message;
            }
        }
    }

    private LocalAuthResponse IssueLsaTicket(LocalAuthRequest req)
    {
        var requestId = req.request_id;
        lock (_activeGrants)
        {
            var now = DateTimeOffset.UtcNow;
            var nowSec = now.ToUnixTimeSeconds();

            if (!_activeGrants.TryGetValue(requestId, out var grant))
            {
                AppendServiceLog($"[ISSUE_LSA_TICKET NOT_FOUND] request_id={requestId}");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.NotFound, "Grant not found or expired");
            }

            if (grant.ExpiresAt < nowSec)
            {
                _activeGrants.Remove(requestId);
                AppendServiceLog($"[ISSUE_LSA_TICKET EXPIRED] request_id={requestId}");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.Expired, "Grant expired");
            }

            PruneExpiredGrants(nowSec);

            if (grant.State != GrantState.Approved && grant.State != GrantState.Reserved)
            {
                AppendServiceLog($"[ISSUE_LSA_TICKET INVALID_STATE] request_id={requestId} state={grant.State}");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.Rejected, $"Grant in state {grant.State}");
            }

            // Verify UserSid binding if provided
            if (!string.IsNullOrWhiteSpace(req.user_sid) && !string.IsNullOrWhiteSpace(grant.UserSid))
            {
                if (!string.Equals(req.user_sid, grant.UserSid, StringComparison.OrdinalIgnoreCase))
                {
                    AppendServiceLog($"[ISSUE_LSA_TICKET SID_MISMATCH] request_id={requestId} req_sid={req.user_sid} grant_sid={grant.UserSid}");
                    return new LocalAuthResponse(1, requestId, LocalAuthStatus.Rejected, "User SID mismatch");
                }
            }

            // Load LSA machine secret
            var (machineSecret, secretStatus, secretErr) = _lsaSecretStore.LoadOrCreate();
            if (machineSecret == null || machineSecret.Length != 32)
            {
                AppendServiceLog($"[ISSUE_LSA_TICKET FAILED] request_id={requestId} LSA secret unavailable (status={secretStatus}, error={secretErr})");
                return new LocalAuthResponse(1, requestId, LocalAuthStatus.LsaSecretUnavailable, "LSA machine secret is not available on this system");
            }

            // Create FACEUNLOCK_LOGON_V1 binary structure
            var cfg = _configStore.Load();

            var userSid = req.user_sid ?? grant.UserSid ?? string.Empty;
            var qualifiedUser = req.qualified_username ?? grant.QualifiedUsername ?? req.username ?? string.Empty;
            // Extract local username part if domain\user or machine\user
            var accountName = qualifiedUser.Contains('\\') ? qualifiedUser.Split('\\')[1] : qualifiedUser;
            var machineName = Environment.MachineName;
            var deviceId = grant.DeviceId ?? cfg.DeviceId ?? string.Empty;
            var issuedAt = nowSec;
            var expiresAt = grant.ExpiresAt;

            var nonce = new byte[16];
            RandomNumberGenerator.Fill(nonce);

            // Serialize header + fields for HMAC
            // Struct size:
            // dwMagic(4) + dwVersion(4) + cbTotalSize(4) + szRequestId(64) + wszUserSid(256) + wszAccountName(512) + wszMachineName(512) + szDeviceId(64) + nIssuedAt(8) + nExpiresAt(8) + bNonce(16) + bHmacSignature(32)
            // Total size = 4+4+4+64+256+512+512+64+8+8+16+32 = 1484 bytes (sizeof(FACEUNLOCK_LOGON_V1))
            const int totalSize = 1484;
            using var ms = new MemoryStream(totalSize);
            using var bw = new BinaryWriter(ms);

            bw.Write((uint)0x46554C4B); // 'FULK'
            bw.Write((uint)1);          // version 1
            bw.Write((uint)totalSize);  // total size

            // szRequestId (64 bytes ASCII)
            var reqIdBytes = new byte[64];
            Encoding.ASCII.GetBytes(requestId, 0, Math.Min(requestId.Length, 63), reqIdBytes, 0);
            bw.Write(reqIdBytes);

            // wszUserSid (128 WCHARs = 256 bytes)
            var sidBytes = new byte[256];
            Encoding.Unicode.GetBytes(userSid, 0, Math.Min(userSid.Length, 127), sidBytes, 0);
            bw.Write(sidBytes);

            // wszAccountName (256 WCHARs = 512 bytes)
            var accBytes = new byte[512];
            Encoding.Unicode.GetBytes(accountName, 0, Math.Min(accountName.Length, 255), accBytes, 0);
            bw.Write(accBytes);

            // wszMachineName (256 WCHARs = 512 bytes)
            var machBytes = new byte[512];
            Encoding.Unicode.GetBytes(machineName, 0, Math.Min(machineName.Length, 255), machBytes, 0);
            bw.Write(machBytes);

            // szDeviceId (64 bytes ASCII)
            var devBytes = new byte[64];
            Encoding.ASCII.GetBytes(deviceId, 0, Math.Min(deviceId.Length, 63), devBytes, 0);
            bw.Write(devBytes);

            // nIssuedAt (8 bytes)
            bw.Write(issuedAt);

            // nExpiresAt (8 bytes)
            bw.Write(expiresAt);

            // bNonce (16 bytes)
            bw.Write(nonce);

            // Flush and get the 1452-byte payload to compute HMAC-SHA256
            bw.Flush();
            var payloadToSign = ms.ToArray();
            using var hmac = new HMACSHA256(machineSecret);
            var signature = hmac.ComputeHash(payloadToSign);
            bw.Write(signature);
            bw.Flush();

            var fullBuffer = ms.ToArray();

            // Mark grant consumed
            grant.State = GrantState.Consumed;

            var ticketBase64 = Convert.ToBase64String(fullBuffer);
            AppendServiceLog($"[ISSUE_LSA_TICKET SUCCESS] request_id={requestId} user_sid={userSid} account={accountName} issued_at={issuedAt} expires_at={expiresAt}");

            return new LocalAuthResponse(1, requestId, LocalAuthStatus.Approved, "lsa_ticket_issued", expiresAt, ServiceVersion, ticketBase64);
        }
    }

    private long RecordGrant(string requestId, string? userSid = null, string? qualifiedUser = null, string? deviceId = null, int? sessionId = null, string? clientType = null, string? pcId = null)
    {
        lock (_activeGrants)
        {
            var now = DateTimeOffset.UtcNow;
            var nowSec = now.ToUnixTimeSeconds();
            var expiresAt = nowSec + 30; // Strictly 30 seconds local TTL

            PruneExpiredGrants(nowSec);

            _activeGrants[requestId] = new AuthGrant
            {
                RequestId = requestId,
                ApprovedAt = now,
                ExpiresAt = expiresAt,
                State = GrantState.Approved,
                UserSid = userSid,
                QualifiedUsername = qualifiedUser,
                DeviceId = deviceId,
                SessionId = sessionId,
                ClientType = clientType,
                PcId = pcId
            };

            return expiresAt;
        }
    }

    public void InjectApprovedGrantForTesting(string requestId, string userSid, string qualifiedUser, string deviceId, long expiresAtSec, int? sessionId = null, string? clientType = null)
    {
        lock (_activeGrants)
        {
            _activeGrants[requestId] = new AuthGrant
            {
                RequestId = requestId,
                ApprovedAt = DateTimeOffset.UtcNow,
                ExpiresAt = expiresAtSec,
                State = GrantState.Approved,
                UserSid = userSid,
                QualifiedUsername = qualifiedUser,
                DeviceId = deviceId,
                SessionId = sessionId,
                ClientType = clientType
            };
        }
    }
}
