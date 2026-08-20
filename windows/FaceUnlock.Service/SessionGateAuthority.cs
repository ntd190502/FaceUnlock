namespace FaceUnlock.Service;

public enum SessionGateStatus
{
    Locked,
    Unlocking,
    Unlocked
}

public sealed record SessionGateSnapshot(
    int SessionId,
    string UserSid,
    SessionGateStatus State,
    string? CurrentRequestId,
    string? AuthorizedRequestId,
    DateTimeOffset? AuthorizedAt,
    bool ExplorerAllowed,
    int? ShellProcessId);

public sealed class SessionGateAuthority
{
    private sealed class MutableSessionGate
    {
        public required int SessionId { get; init; }
        public required string UserSid { get; set; }
        public SessionGateStatus State { get; set; } = SessionGateStatus.Locked;
        public string? CurrentRequestId { get; set; }
        public string? AuthorizedRequestId { get; set; }
        public DateTimeOffset? AuthorizedAt { get; set; }
        public int? ShellProcessId { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<int, MutableSessionGate> _sessions = new();

    public SessionGateSnapshot ObserveLockedSession(int sessionId, string userSid)
    {
        lock (_sync)
        {
            var gate = GetOrResetForIdentity(sessionId, userSid);
            return Snapshot(gate);
        }
    }

    public bool TryRegisterShellProcess(int sessionId, string userSid, int processId, out string? invalidatedRequestId)
    {
        lock (_sync)
        {
            invalidatedRequestId = null;
            var gate = GetOrResetForIdentity(sessionId, userSid);
            if (gate.State == SessionGateStatus.Unlocked)
            {
                if (gate.ShellProcessId == processId)
                {
                    return false;
                }
                ResetForNewShellCycle(gate, processId);
                return true;
            }

            if (gate.ShellProcessId.HasValue && gate.ShellProcessId.Value != processId)
            {
                invalidatedRequestId = gate.CurrentRequestId;
                gate.CurrentRequestId = null;
            }
            gate.ShellProcessId = processId;
            gate.State = SessionGateStatus.Locked;
            return true;
        }
    }

    public bool TryBeginShellRequest(int sessionId, string userSid, int processId, string requestId, out string? replacedRequestId)
    {
        lock (_sync)
        {
            replacedRequestId = null;
            var gate = GetOrResetForIdentity(sessionId, userSid);
            if (gate.State == SessionGateStatus.Unlocked)
            {
                if (gate.ShellProcessId == processId)
                {
                    return false;
                }
                ResetForNewShellCycle(gate, processId);
            }
            if (gate.ShellProcessId.HasValue && gate.ShellProcessId.Value != processId)
            {
                return false;
            }

            if (!string.Equals(gate.CurrentRequestId, requestId, StringComparison.Ordinal))
            {
                replacedRequestId = gate.CurrentRequestId;
            }
            gate.ShellProcessId = processId;
            gate.CurrentRequestId = requestId;
            gate.AuthorizedRequestId = null;
            gate.AuthorizedAt = null;
            gate.State = SessionGateStatus.Locked;
            return true;
        }
    }

    public string? MarkShellMissing(int sessionId, string userSid, int? expectedProcessId = null)
    {
        lock (_sync)
        {
            var gate = GetOrResetForIdentity(sessionId, userSid);
            if (gate.State == SessionGateStatus.Unlocked)
            {
                return null;
            }
            if (expectedProcessId.HasValue && gate.ShellProcessId.HasValue && gate.ShellProcessId != expectedProcessId)
            {
                return null;
            }

            var invalidated = gate.CurrentRequestId;
            gate.State = SessionGateStatus.Locked;
            gate.CurrentRequestId = null;
            gate.AuthorizedRequestId = null;
            gate.AuthorizedAt = null;
            gate.ShellProcessId = null;
            return invalidated;
        }
    }

    public bool TryAuthorizeConsumedGrant(string requestId, string userSid, int sessionId, int processId)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var gate))
            {
                return false;
            }
            if (gate.State != SessionGateStatus.Locked
                || !string.Equals(gate.UserSid, userSid, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(gate.CurrentRequestId, requestId, StringComparison.Ordinal)
                || gate.ShellProcessId != processId)
            {
                return false;
            }

            gate.State = SessionGateStatus.Unlocking;
            gate.AuthorizedRequestId = requestId;
            gate.AuthorizedAt = DateTimeOffset.UtcNow;
            gate.CurrentRequestId = null;
            gate.State = SessionGateStatus.Unlocked;
            return true;
        }
    }

    public bool IsCurrentShellRequest(string requestId, string userSid, int sessionId, int processId)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(sessionId, out var gate)
                && gate.State == SessionGateStatus.Locked
                && string.Equals(gate.UserSid, userSid, StringComparison.OrdinalIgnoreCase)
                && string.Equals(gate.CurrentRequestId, requestId, StringComparison.Ordinal)
                && gate.ShellProcessId == processId;
        }
    }

    public SessionGateSnapshot GetSnapshot(int sessionId, string userSid)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var gate)
                || !string.Equals(gate.UserSid, userSid, StringComparison.OrdinalIgnoreCase))
            {
                return new SessionGateSnapshot(sessionId, userSid, SessionGateStatus.Locked, null, null, null, false, null);
            }
            return Snapshot(gate);
        }
    }

    public void RemoveMissingSessions(IReadOnlySet<int> activeSessionIds)
    {
        lock (_sync)
        {
            foreach (var sessionId in _sessions.Keys.Where(id => !activeSessionIds.Contains(id)).ToArray())
            {
                _sessions.Remove(sessionId);
            }
        }
    }

    private MutableSessionGate GetOrResetForIdentity(int sessionId, string userSid)
    {
        if (!_sessions.TryGetValue(sessionId, out var gate))
        {
            gate = new MutableSessionGate { SessionId = sessionId, UserSid = userSid };
            _sessions[sessionId] = gate;
            return gate;
        }

        if (!string.Equals(gate.UserSid, userSid, StringComparison.OrdinalIgnoreCase))
        {
            gate = new MutableSessionGate { SessionId = sessionId, UserSid = userSid };
            _sessions[sessionId] = gate;
        }
        return gate;
    }

    private static SessionGateSnapshot Snapshot(MutableSessionGate gate) => new(
        gate.SessionId,
        gate.UserSid,
        gate.State,
        gate.CurrentRequestId,
        gate.AuthorizedRequestId,
        gate.AuthorizedAt,
        gate.State == SessionGateStatus.Unlocked,
        gate.ShellProcessId);

    private static void ResetForNewShellCycle(MutableSessionGate gate, int processId)
    {
        gate.State = SessionGateStatus.Locked;
        gate.CurrentRequestId = null;
        gate.AuthorizedRequestId = null;
        gate.AuthorizedAt = null;
        gate.ShellProcessId = processId;
    }
}
