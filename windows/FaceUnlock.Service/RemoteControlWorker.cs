using System.Diagnostics;
using System.Management;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using FaceUnlock.Core;

namespace FaceUnlock.Service;

public sealed class RemoteControlWorker : BackgroundService
{
    readonly ILogger<RemoteControlWorker> _log;
    readonly ConfigStore _store = new();
    DateTime _lastTempAlert = DateTime.MinValue;
    DateTime _lastRamAlert = DateTime.MinValue;

    static readonly string BridgeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceUnlock", "Bridge");
    static string Bridge(string name) { Directory.CreateDirectory(BridgeDir); return Path.Combine(BridgeDir, name); }

    sealed class BridgeApp { public int id { get; set; } public string name { get; set; } = ""; public string title { get; set; } = ""; }
    sealed class AppsBridgeResponse { public string request_id { get; set; } = ""; public BridgeApp[] apps { get; set; } = Array.Empty<BridgeApp>(); }
    sealed class CloseBridgeResponse { public string request_id { get; set; } = ""; public bool ok { get; set; } public int pid { get; set; } public string? error { get; set; } }

    public RemoteControlWorker(ILogger<RemoteControlWorker> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var cfg = _store.Load();
                if (!string.IsNullOrWhiteSpace(cfg.PcToken))
                {
                    await CheckAlerts(cfg, stop);
                    var api = new ApiClient(cfg);
                    var pending = await api.GetRemoteCommandAsync(stop);
                    if (pending.pending && pending.command != null)
                    {
                        _log.LogInformation("[REMOTE CLAIM] id={CommandId} type={CommandType}", pending.command.id, pending.command.type);
                        await Handle(api, pending.command, cfg, stop);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "[REMOTE POLL FAILED] {Message}", ex.Message); }
            await Task.Delay(3000, stop);
        }
    }

    async Task Handle(ApiClient api, RemoteCommand command, LocalConfig cfg, CancellationToken ct)
    {
        try
        {
            _log.LogInformation("[REMOTE EXECUTE] id={CommandId} type={CommandType}", command.id, command.type);
            object result = command.type switch
            {
                "status" => ReadStatusLogged(command.id),
                "lock" => Lock(),
                "restart" => Power("/r /t 1"),
                "shutdown" => Power("/s /t 1"),
                "apps" => Apps(),
                "close_app" => CloseApp(command.payload),
                "clipboard_set" => ClipboardSet(command.payload),
                "clipboard_get" => ClipboardGet(),
                "file_upload" => FileUpload(command.payload),
                "clipboard_file_download" => ClipboardFile(),
                "screenshot" => Screenshot(),
                _ => throw new InvalidOperationException("Unsupported command")
            };
            _log.LogInformation("[REMOTE RESULT POST] id={CommandId} status=DONE", command.id);
            await api.CompleteRemoteCommandAsync(command.id, "DONE", result, ct);
            _log.LogInformation("[REMOTE DONE] id={CommandId} type={CommandType}", command.id, command.type);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[REMOTE EXECUTE/RESULT FAILED] id={CommandId} type={CommandType}: {Message}", command.id, command.type, ex.Message);
            try { await api.CompleteRemoteCommandAsync(command.id, "ERROR", new { error = ex.Message }, ct); }
            catch (Exception reportError) { _log.LogError(reportError, "[REMOTE RESULT POST FAILED] id={CommandId}: {Message}", command.id, reportError.Message); }
        }
    }

    object ReadStatusLogged(string id) { var result = ReadStatus(); _log.LogInformation("[REMOTE STATUS READY] id={CommandId} result={Result}", id, JsonSerializer.Serialize(result)); return result; }
    static object ReadStatus()
    {
        double cpu = 0;
        try { using var s = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor"); var v = s.Get().Cast<ManagementObject>().Select(x => Convert.ToDouble(x["LoadPercentage"])).ToArray(); if (v.Length > 0) cpu = v.Average(); } catch { }
        return new { cpu_percent = Math.Round(cpu, 1), ram_percent = Math.Round(RamPercent(), 1), temperature_c = CpuTemperature() };
    }
    static double RamPercent() { try { using var s = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem"); var r = s.Get().Cast<ManagementObject>().First(); var t = Convert.ToDouble(r["TotalVisibleMemorySize"]); var f = Convert.ToDouble(r["FreePhysicalMemory"]); return t > 0 ? (t-f)*100/t : 0; } catch { return 0; } }
    static double? CpuTemperature() { try { using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"); var v=s.Get().Cast<ManagementObject>().Select(x=>(Convert.ToDouble(x["CurrentTemperature"])/10)-273.15).Where(x=>x>0&&x<150).ToArray(); return v.Length>0?Math.Round(v.Max(),1):null; } catch { return null; } }

    static object Lock()
    {
        // Use the same workstation lock semantics as Win+L. This keeps the interactive
        // user signed in and leaves desktop applications running behind the lock screen.
        var p = Process.Start(new ProcessStartInfo("rundll32.exe", "user32.dll,LockWorkStation") { UseShellExecute = false, CreateNoWindow = true });
        if (p == null) throw new InvalidOperationException("Could not invoke Windows lock screen");
        return new { locked = true, mode = "workstation" };
    }

    static object Power(string args) { Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute=false, CreateNoWindow=true }); return new { accepted=true }; }

    static object Apps()
    {
        var id=Guid.NewGuid().ToString("N"); var result=Bridge("apps.result.json"); TryDelete(result); File.WriteAllText(Bridge("apps.request"),id);
        for(var i=0;i<40;i++){Thread.Sleep(100);if(!File.Exists(result))continue;try{var r=JsonSerializer.Deserialize<AppsBridgeResponse>(File.ReadAllText(result));if(r?.request_id==id)return new{apps=r.apps};}catch(IOException){}catch(JsonException){}}
        throw new InvalidOperationException("FaceUnlock Agent interactive bridge did not return the running application list");
    }
    static object CloseApp(Dictionary<string,object>? payload)
    {
        if(payload==null||!payload.TryGetValue("pid",out var raw)||!int.TryParse(raw?.ToString(),out var pid))throw new InvalidOperationException("pid required");
        var id=Guid.NewGuid().ToString("N");var result=Bridge("close-app.result.json");TryDelete(result);File.WriteAllText(Bridge("close-app.request.json"),JsonSerializer.Serialize(new{request_id=id,pid}));
        for(var i=0;i<40;i++){Thread.Sleep(100);if(!File.Exists(result))continue;try{var r=JsonSerializer.Deserialize<CloseBridgeResponse>(File.ReadAllText(result));if(r?.request_id!=id)continue;if(!r.ok)throw new InvalidOperationException(r.error??"Could not close application");return new{closed=true,pid};}catch(IOException){}catch(JsonException){}}
        throw new InvalidOperationException("FaceUnlock Agent interactive bridge did not respond to close application request");
    }
    static object ClipboardSet(Dictionary<string,object>? p){var t=p!=null&&p.TryGetValue("text",out var v)?v?.ToString()??"":"";File.WriteAllText(Bridge("clipboard-in.txt"),t);return new{queued=true};}
    static object ClipboardGet(){var f=Bridge("clipboard-out.txt");return new{text=File.Exists(f)?File.ReadAllText(f):"",available=File.Exists(f)};}
    static object FileUpload(Dictionary<string,object>? p){if(p==null||!p.TryGetValue("name",out var n)||!p.TryGetValue("base64",out var b))throw new InvalidOperationException("name/base64 required");var name=Path.GetFileName(n?.ToString()??"upload.bin");var d=Convert.FromBase64String(b?.ToString()??"");if(d.Length>8*1024*1024)throw new InvalidOperationException("File exceeds 8 MB remote relay limit");var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"FaceUnlock","Incoming");Directory.CreateDirectory(dir);var path=Path.Combine(dir,name);File.WriteAllBytes(path,d);File.WriteAllText(Bridge("incoming-file.path"),path);return new{saved=true,name,size=d.Length};}
    static object ClipboardFile(){var m=Bridge("clipboard-file.path");if(!File.Exists(m))return new{available=false};var path=File.ReadAllText(m).Trim();if(!File.Exists(path))return new{available=false};var d=File.ReadAllBytes(path);if(d.Length>8*1024*1024)throw new InvalidOperationException("File exceeds 8 MB remote relay limit");return new{available=true,name=Path.GetFileName(path),base64=Convert.ToBase64String(d),size=d.Length};}
    static object Screenshot(){var r=Bridge("screenshot.request");var f=Bridge("screenshot.jpg");TryDelete(f);File.WriteAllText(r,DateTime.UtcNow.Ticks.ToString());for(var i=0;i<30&&!File.Exists(f);i++)Thread.Sleep(100);if(!File.Exists(f))return new{available=false,error="FaceUnlock Agent interactive bridge is not running"};var d=File.ReadAllBytes(f);return new{available=true,mime="image/jpeg",base64=Convert.ToBase64String(d)};}
    static void TryDelete(string p){try{if(File.Exists(p))File.Delete(p);}catch{}}

    async Task CheckAlerts(LocalConfig c,CancellationToken ct){var now=DateTime.UtcNow;var ram=RamPercent();var temp=CpuTemperature();if(c.RamAlertEnabled&&ram>=c.RamAlertPercent&&(now-_lastRamAlert).TotalSeconds>=c.AlertCooldownSeconds){_lastRamAlert=now;await Telegram(c,$"⚠️ FaceUnlock RAM alert\nPC: {c.PcName}\nRAM: {ram:F1}% (limit {c.RamAlertPercent:F0}%)",ct);}if(c.TemperatureAlertEnabled&&temp.HasValue&&temp.Value>=c.TemperatureAlertCelsius&&(now-_lastTempAlert).TotalSeconds>=c.AlertCooldownSeconds){_lastTempAlert=now;await Telegram(c,$"🔥 FaceUnlock temperature alert\nPC: {c.PcName}\nCPU: {temp:F1}°C (limit {c.TemperatureAlertCelsius:F0}°C)",ct);}}
    static async Task Telegram(LocalConfig c,string text,CancellationToken ct){if(string.IsNullOrWhiteSpace(c.TelegramBotToken)||string.IsNullOrWhiteSpace(c.TelegramChatId))return;using var client=new HttpClient();await client.PostAsJsonAsync($"https://api.telegram.org/bot{c.TelegramBotToken}/sendMessage",new{chat_id=c.TelegramChatId,text},ct);}
}
