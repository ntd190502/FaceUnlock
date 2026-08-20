namespace FaceUnlock.Core;

/// <summary>One authoritative Windows request shared by every network transport.</summary>
public sealed class LogicalUnlockAttempt
{
    private readonly object _sync = new();

    public LogicalUnlockAttempt(string requestId)
    {
        RequestId = requestId;
    }

    public string RequestId { get; }
    public bool IsApproved { get; private set; }
    public string? ApprovedTransport { get; private set; }

    public bool TryAcceptApproval(string transport)
    {
        lock (_sync)
        {
            if (IsApproved)
                return false;
            IsApproved = true;
            ApprovedTransport = transport;
            return true;
        }
    }
}
