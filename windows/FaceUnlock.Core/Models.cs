namespace FaceUnlock.Core;

public record PairStartRequest(string pc_id, string pc_name, string pc_public_key_pem);
public record PairStartResponse(bool ok, string pair_id, string pair_code, long expires_at, string pc_token);
public record PairedDevice(string id, string name, string public_key_pem);
public record PairStatusResponse(bool ok, bool paired, PairedDevice? device);
public record UnlockRequest(string? device_id = null);
public record UnlockRequestResponse(bool ok, string session_id, string challenge, long expires_at, bool push_sent, string? push_error, string? device_id = null);
public record UnlockStatusResponse(bool ok, string session_id, string status, string challenge, long expires_at, string? signature, string? device_public_key_pem, string? device_id = null, string? winning_device_id = null);
public record RevokeDeviceResponse(bool ok, bool revoked, string device_id);
public record PairedDeviceInfo(string id, string name, string? nickname, string status, string? paired_at, string? last_used_at, string? last_seen_at);
public record DeviceListResponse(bool ok, List<PairedDeviceInfo> devices);
public record OfflineUnlockPayload(string type,string session_id,string pc_id,string pc_name,string challenge,long expires_at,string pc_signature,string? logical_request_id=null,string? online_session_id=null);
public record OfflineBleResponse(string ok,string? session_id,string? signature,string? error);

public record RemoteCommand(string id,string type,Dictionary<string,object>? payload);
public record RemotePendingResponse(bool ok,bool pending,RemoteCommand? command);
public record RemoteResultRequest(string status,object result);

public sealed record LocalAuthRequest(int version,string command,string request_id,string? usage=null,string? username=null,string? user_sid=null,string? qualified_username=null,int? session_id=null,string? client_type=null,string? pc_id=null,int? process_id=null);
public sealed record LocalAuthResponse(int version,string request_id,string status,string? message=null,long? expires_at=null,string? service_version=null,string? user_sid=null,int? session_id=null);

public static class LocalAuthStatus { public const string Ok="ok",Pending="pending",WaitingConnectivity="waiting_connectivity",InternetRestored="internet_restored",Approved="approved",Reserved="reserved",Consumed="consumed",Released="released",Rejected="rejected",Timeout="timeout",Error="error",Cancelled="cancelled",NotPaired="not_paired",Busy="busy",Expired="expired",NotFound="not_found"; }

public sealed class LocalConfig {
 public string ServerUrl { get; set; }="https://face.bobabliss.io.vn"; public string PcId { get; set; }=Guid.NewGuid().ToString("N"); public string PcName { get; set; }=Environment.MachineName;
 public string? PcToken { get; set; } public string? PairId { get; set; } public string? DeviceId { get; set; } public string? DevicePublicKeyPem { get; set; } public List<PairedDevice> Devices { get; set; }=new();
 public bool TemperatureAlertEnabled { get; set; }=true; public double TemperatureAlertCelsius { get; set; }=80; public bool RamAlertEnabled { get; set; }=true; public double RamAlertPercent { get; set; }=90;
 public int AlertCooldownSeconds { get; set; }=300; public string? TelegramBotToken { get; set; } public string? TelegramChatId { get; set; }
}
