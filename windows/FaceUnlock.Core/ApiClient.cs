using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
namespace FaceUnlock.Core;

public sealed class ApiClient {
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly LocalConfig _cfg;
    public ApiClient(LocalConfig cfg) => _cfg = cfg;
    private string Url(string p) => _cfg.ServerUrl.TrimEnd('/') + p;
    private HttpRequestMessage Req(HttpMethod method, string path, string? explicitToken=null, bool auth=false) {
        var r = new HttpRequestMessage(method, Url(path));
        var token = explicitToken ?? _cfg.PcToken;
        if (auth && !string.IsNullOrWhiteSpace(token)) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }
    public async Task<PairStartResponse> StartPairAsync(string pem, CancellationToken ct=default) {
        using var r=Req(HttpMethod.Post,"/v1/pair/start");
        r.Content=JsonContent.Create(new PairStartRequest(_cfg.PcId,_cfg.PcName,pem));
        using var resp=await _http.SendAsync(r,ct); return await Read<PairStartResponse>(resp,ct);
    }
    public async Task<PairStatusResponse> PairStatusAsync(string id,string? pcToken=null,CancellationToken ct=default) {
        using var r=Req(HttpMethod.Get,$"/v1/pair/status/{Uri.EscapeDataString(id)}",pcToken,true);
        using var resp=await _http.SendAsync(r,ct); return await Read<PairStatusResponse>(resp,ct);
    }
    public async Task<UnlockRequestResponse> RequestUnlockAsync(string? deviceId=null,CancellationToken ct=default) {
        using var r=Req(HttpMethod.Post,"/v1/unlock/request",auth:true);
        r.Content=JsonContent.Create(new UnlockRequest(deviceId));
        using var resp=await _http.SendAsync(r,ct); return await Read<UnlockRequestResponse>(resp,ct);
    }
    public async Task<RevokeDeviceResponse> RevokeDeviceAsync(string deviceId,CancellationToken ct=default) {
        using var r=Req(HttpMethod.Post,$"/v1/devices/{Uri.EscapeDataString(deviceId)}/revoke",auth:true);
        r.Content=JsonContent.Create(new {});
        using var resp=await _http.SendAsync(r,ct); return await Read<RevokeDeviceResponse>(resp,ct);
    }
    public async Task<DeviceListResponse> GetDevicesAsync(CancellationToken ct=default) {
        using var r=Req(HttpMethod.Get,"/v1/devices",auth:true);
        using var resp=await _http.SendAsync(r,ct); return await Read<DeviceListResponse>(resp,ct);
    }
    public async Task<UnlockStatusResponse> GetUnlockStatusAsync(string id,CancellationToken ct=default) {
        using var r=Req(HttpMethod.Get,$"/v1/unlock/status/{Uri.EscapeDataString(id)}",auth:true);
        using var resp=await _http.SendAsync(r,ct); return await Read<UnlockStatusResponse>(resp,ct);
    }
    private static async Task<T> Read<T>(HttpResponseMessage resp,CancellationToken ct) {
        var text=await resp.Content.ReadAsStringAsync(ct);
        if(!resp.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text,Json) ?? throw new InvalidOperationException("Invalid server JSON");
    }
}
