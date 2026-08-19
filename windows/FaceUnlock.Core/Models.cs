namespace FaceUnlock.Core;

public record PairStartRequest(string pc_id, string pc_name, string pc_public_key_pem);
public record PairStartResponse(bool ok, string pair_id, string pair_code, long expires_at, string pc_token);
public record PairedDevice(string id, string name, string public_key_pem);
public record PairStatusResponse(bool ok, bool paired, PairedDevice? device);
public record UnlockRequest(string? device_id = null);
public record UnlockRequestResponse(bool ok, string session_id, string challenge, long expires_at, bool push_sent, string? push_error, string? device_id = null);
public record UnlockStatusResponse(bool ok, string session_id, string status, string challenge, long expires_at, string? signature, string device_public_key_pem, string? device_id = null);
public record RevokeDeviceResponse(bool ok, bool revoked, string device_id);
public record OfflineUnlockPayload(string type, string session_id, string pc_id, string pc_name, string challenge, long expires_at, string pc_signature);
public record OfflineBleResponse(string ok, string? session_id, string? signature, string? error);

// Local IPC models for CredentialProvider <-> FaceUnlock.Service
public sealed record LocalAuthRequest(
    int version,
    string command,
    string request_id,
    string? usage = null,
    string? username = null,
    int? session_id = null
);

public sealed record LocalAuthResponse(
    int version,
    string request_id,
    string status,
    string? message = null,
    long? expires_at = null
);

public static class LocalAuthStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Timeout = "timeout";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
    public const string NotPaired = "not_paired";
    public const string Busy = "busy";
    public const string Expired = "expired";
    public const string NotFound = "not_found";
}

public sealed class LocalConfig {
    public string ServerUrl { get; set; } = "https://face.bobabliss.io.vn";
    public string PcId { get; set; } = Guid.NewGuid().ToString("N");
    public string PcName { get; set; } = Environment.MachineName;
    public string? PcToken { get; set; }
    public string? PairId { get; set; }
    public string? DeviceId { get; set; }
    public string? DevicePublicKeyPem { get; set; }
    public List<PairedDevice> Devices { get; set; } = new();
}
