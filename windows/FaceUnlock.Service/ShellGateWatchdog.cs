namespace FaceUnlock.Service;

public sealed record InteractiveGateSession(int SessionId, string UserSid, bool ShellGateEnabled);
public sealed record SessionProcess(int ProcessId, int SessionId);

public interface IShellGateSystem
{
    bool IsMachinePaired { get; }
    IReadOnlyList<InteractiveGateSession> GetInteractiveSessions();
    IReadOnlyList<SessionProcess> GetShellProcesses(int sessionId);
    IReadOnlyList<SessionProcess> GetExplorerProcesses(int sessionId);
    bool IsProcessAlive(int processId, int sessionId, string processName);
    bool IsTrustedShellProcess(int processId, int sessionId);
    bool TryLaunchShell(InteractiveGateSession session, out int processId, out string? errorMessage);
    bool TryTerminateProcess(SessionProcess process, string expectedProcessName, out string? errorMessage);
}

public interface IShellGateWatchdog
{
    Task RunAsync(CancellationToken cancellationToken);
}

public sealed class ShellGateWatchdog : IShellGateWatchdog
{
    private sealed class RestartBackoff
    {
        public int FailureCount { get; set; }
        public DateTimeOffset NextAttemptAt { get; set; }
        public int? LastStartedProcessId { get; set; }
    }

    private readonly SessionGateAuthority _authority;
    private readonly IShellGateSystem _system;
    private readonly Action<string> _invalidateRequest;
    private readonly Action<string> _log;
    private readonly TimeSpan _interval;
    private readonly Dictionary<int, RestartBackoff> _restartBackoff = new();
    private readonly Dictionary<string, DateTimeOffset> _lastLog = new(StringComparer.Ordinal);

    public ShellGateWatchdog(
        SessionGateAuthority authority,
        IShellGateSystem system,
        Action<string> invalidateRequest,
        Action<string> log,
        TimeSpan? interval = null)
    {
        _authority = authority;
        _system = system;
        _invalidateRequest = invalidateRequest;
        _log = log;
        _interval = interval ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (NativeApiFailureException ex)
            {
                var bindingException = ex.InnerException?.GetType().Name ?? ex.GetType().Name;
                LogRateLimited(
                    $"native-{ex.Dll}-{ex.Api}",
                    $"[WATCHDOG][NATIVE_FAILURE] api={ex.Api} dll={ex.Dll} exception={bindingException}",
                    TimeSpan.FromMinutes(1));
            }
            catch (Exception ex)
            {
                LogRateLimited("tick-error", $"[WATCHDOG] error={ex.GetType().Name} message={ex.Message}");
            }

            try
            {
                await Task.Delay(_interval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public void Tick()
    {
        if (!_system.IsMachinePaired)
        {
            return;
        }

        var sessions = _system.GetInteractiveSessions()
            .Where(session => session.ShellGateEnabled)
            .GroupBy(session => session.SessionId)
            .Select(group => group.First())
            .ToArray();
        _authority.RemoveMissingSessions(sessions.Select(s => s.SessionId).ToHashSet());

        foreach (var session in sessions)
        {
            ProcessSession(session);
        }
    }

    private void ProcessSession(InteractiveGateSession session)
    {
        var snapshot = _authority.ObserveLockedSession(session.SessionId, session.UserSid);
        var shells = _system.GetShellProcesses(session.SessionId).OrderBy(p => p.ProcessId).ToList();

        if (snapshot.State == SessionGateStatus.Unlocked)
        {
            var newCycleShell = shells.FirstOrDefault(process => process.ProcessId != snapshot.ShellProcessId);
            if (newCycleShell == null
                || !_authority.TryRegisterShellProcess(session.SessionId, session.UserSid, newCycleShell.ProcessId, out _))
            {
                _restartBackoff.Remove(session.SessionId);
                return;
            }
            snapshot = _authority.GetSnapshot(session.SessionId, session.UserSid);
            _log($"[WATCHDOG] session={session.SessionId} new Shell cycle detected; gate=LOCKED pid={newCycleShell.ProcessId}");
        }

        var canonical = ChooseCanonicalShell(snapshot, shells);
        foreach (var duplicate in shells.Where(p => canonical == null || p.ProcessId != canonical.ProcessId))
        {
            if (duplicate.SessionId != session.SessionId)
            {
                continue;
            }
            if (_system.TryTerminateProcess(duplicate, "FaceUnlockShell", out var duplicateError))
            {
                LogRateLimited($"duplicate-{session.SessionId}", $"[WATCHDOG] session={session.SessionId} duplicate shell terminated pid={duplicate.ProcessId}");
            }
            else
            {
                LogRateLimited($"duplicate-error-{session.SessionId}", $"[WATCHDOG] session={session.SessionId} duplicate shell terminate failed pid={duplicate.ProcessId} error={duplicateError}");
            }
        }

        if (canonical != null)
        {
            if (_authority.TryRegisterShellProcess(session.SessionId, session.UserSid, canonical.ProcessId, out var invalidated)
                && !string.IsNullOrWhiteSpace(invalidated))
            {
                _invalidateRequest(invalidated);
            }
            _restartBackoff.Remove(session.SessionId);
        }
        else
        {
            var invalidated = _authority.MarkShellMissing(session.SessionId, session.UserSid, snapshot.ShellProcessId);
            if (!string.IsNullOrWhiteSpace(invalidated))
            {
                _invalidateRequest(invalidated);
            }
            LogRateLimited($"missing-{session.SessionId}", $"[WATCHDOG] session={session.SessionId} gate=LOCKED shell missing");
            var latest = _authority.GetSnapshot(session.SessionId, session.UserSid);
            if (latest.ExplorerAllowed)
            {
                return;
            }
            if (latest.ShellProcessId.HasValue
                && _system.IsProcessAlive(latest.ShellProcessId.Value, session.SessionId, "FaceUnlockShell"))
            {
                return;
            }
            TryRestartShell(session);
        }

        foreach (var explorer in _system.GetExplorerProcesses(session.SessionId).ToArray())
        {
            if (_authority.GetSnapshot(session.SessionId, session.UserSid).ExplorerAllowed)
            {
                break;
            }
            if (explorer.SessionId != session.SessionId)
            {
                continue;
            }
            LogRateLimited($"explorer-{session.SessionId}-{explorer.ProcessId}", $"[WATCHDOG] session={session.SessionId} unauthorized explorer detected pid={explorer.ProcessId}");
            if (_system.TryTerminateProcess(explorer, "explorer", out var error))
            {
                _log($"[WATCHDOG] session={session.SessionId} unauthorized explorer terminated pid={explorer.ProcessId}");
            }
            else
            {
                LogRateLimited($"explorer-error-{session.SessionId}-{explorer.ProcessId}", $"[WATCHDOG] session={session.SessionId} unauthorized explorer terminate failed pid={explorer.ProcessId} error={error}");
            }
        }
    }

    private static SessionProcess? ChooseCanonicalShell(SessionGateSnapshot snapshot, List<SessionProcess> shells)
    {
        if (snapshot.ShellProcessId.HasValue)
        {
            var registered = shells.FirstOrDefault(process => process.ProcessId == snapshot.ShellProcessId.Value);
            if (registered != null)
            {
                return registered;
            }
        }
        return shells.FirstOrDefault();
    }

    private void TryRestartShell(InteractiveGateSession session)
    {
        if (!_restartBackoff.TryGetValue(session.SessionId, out var backoff))
        {
            backoff = new RestartBackoff();
            _restartBackoff[session.SessionId] = backoff;
        }
        if (backoff.LastStartedProcessId.HasValue)
        {
            if (_system.IsProcessAlive(backoff.LastStartedProcessId.Value, session.SessionId, "FaceUnlockShell"))
            {
                _authority.TryRegisterShellProcess(session.SessionId, session.UserSid, backoff.LastStartedProcessId.Value, out _);
                return;
            }
            backoff.LastStartedProcessId = null;
        }
        if (DateTimeOffset.UtcNow < backoff.NextAttemptAt)
        {
            return;
        }

        if (_system.TryLaunchShell(session, out var processId, out var error))
        {
            backoff.FailureCount = Math.Min(backoff.FailureCount + 1, 5);
            var verifyDelaySeconds = Math.Min(Math.Pow(2, backoff.FailureCount - 1), 10);
            backoff.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(verifyDelaySeconds);
            backoff.LastStartedProcessId = processId;
            _authority.TryRegisterShellProcess(session.SessionId, session.UserSid, processId, out _);
            _log($"[WATCHDOG] session={session.SessionId} shell restarted pid={processId}");
            return;
        }

        backoff.FailureCount = Math.Min(backoff.FailureCount + 1, 5);
        var delaySeconds = Math.Min(Math.Pow(2, backoff.FailureCount - 1), 10);
        backoff.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
        LogRateLimited($"restart-error-{session.SessionId}", $"[WATCHDOG] session={session.SessionId} shell restart failed backoff={delaySeconds:F0}s error={error}");
    }

    private void LogRateLimited(string key, string message, TimeSpan? minimumInterval = null)
    {
        var now = DateTimeOffset.UtcNow;
        var interval = minimumInterval ?? TimeSpan.FromSeconds(5);
        if (_lastLog.TryGetValue(key, out var last) && now - last < interval)
        {
            return;
        }
        _lastLog[key] = now;
        _log(message);
    }
}
