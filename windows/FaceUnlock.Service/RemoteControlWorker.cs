using System.Diagnostics;
using System.Management;
using System.Net.Http.Json;
using System.Text.Json;
using FaceUnlock.Core;

namespace FaceUnlock.Service;

public sealed class RemoteControlWorker : BackgroundService
{
    readonly ILogger<RemoteControlWorker> _log;
    readonly ConfigStore _store = new();
    DateTime _lastTempAlert = DateTime.MinValue;
    DateTime _lastRamAlert = DateTime.MinValue;

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
                    await ReceiveHostedFile(api, stop);
                    var pending = await api.GetRemoteCommandAsync(stop);
                    if (pending.pending && pending.command != null)
                    {
                        _log.LogInformation("[REMOTE CLAIM] id={CommandId} type={CommandType}", pending.command.id, pending.command.type);
                        await Handle(api, pending.command, stop);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "[REMOTE POLL FAILED] {Message}", ex.Message); }
            await Task.Delay(3000, stop);
        }
    }

    async Task Handle(ApiClient api, RemoteCommand command, CancellationToken ct)
    {
        try
        {
            object result = command.type switch
            {
                "status" => ReadStatus(),
                "signout" => Power("/l"),
                "restart" => Power("/r /t 1"),
                "shutdown" => Power("/s /t 1"),
                _ => throw new InvalidOperationException("Unsupported command")
            };
            await api.CompleteRemoteCommandAsync(command.id, "DONE", result, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[REMOTE EXECUTE FAILED] id={CommandId} type={CommandType}", command.id, command.type);
            try { await api.CompleteRemoteCommandAsync(command.id, "ERROR", new { error = ex.Message }, ct); } catch { }
        }
    }

    async Task ReceiveHostedFile(ApiClient api, CancellationToken ct)
    {
        var pending = await api.GetPendingHostedFileAsync(ct);
        if (!pending.pending || pending.file == null) return;
        var root = UserDownloadsFaceUnlock(); Directory.CreateDirectory(root);
        var name = Path.GetFileName(pending.file.name); var path = UniquePath(root, name);
        await api.DownloadHostedFileAsync(pending.file, path, ct);
        _log.LogInformation("[HOSTED FILE RECEIVED] id={Id} path={Path}", pending.file.id, path);
    }

    static string UserDownloadsFaceUnlock()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem");
            var user = s.Get().Cast<ManagementObject>().Select(x => x["UserName"]?.ToString()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            var shortName = user?.Split('\\').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(shortName)) return Path.Combine("C:\\Users", shortName, "Downloads", "FaceUnlock");
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceUnlock", "Incoming");
    }

    static string UniquePath(string dir, string name)
    {
        var path=Path.Combine(dir,name); if(!File.Exists(path)) return path;
        var stem=Path.GetFileNameWithoutExtension(name);var ext=Path.GetExtension(name);
        for(var i=1;;i++){path=Path.Combine(dir,$"{stem} ({i}){ext}");if(!File.Exists(path))return path;}
    }

    static object ReadStatus()
    {
        return new { cpu_percent=Math.Round(TotalCpuPercent(),1), ram_percent=Math.Round(RamPercent(),1), temperature_c=CpuTemperature() };
    }

    static double TotalCpuPercent()
    {
        // Prefer the Windows performance counter provider's _Total row. This is total CPU usage
        // across all logical processors, matching Task Manager much more closely than Win32_Processor.LoadPercentage.
        try
        {
            using var s=new ManagementObjectSearcher("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");
            var row=s.Get().Cast<ManagementObject>().FirstOrDefault();
            if(row?["PercentProcessorTime"] is not null)
            {
                var value=Convert.ToDouble(row["PercentProcessorTime"]);
                if(value>=0&&value<=100)return value;
            }
        }
        catch { }

        // Fallback for systems where the formatted performance provider is unavailable.
        try
        {
            using var s=new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor");
            var values=s.Get().Cast<ManagementObject>().Select(x=>Convert.ToDouble(x["LoadPercentage"])).ToArray();
            if(values.Length>0)return Math.Clamp(values.Average(),0,100);
        }
        catch { }
        return 0;
    }

    static double RamPercent(){try{using var s=new ManagementObjectSearcher("SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem");var r=s.Get().Cast<ManagementObject>().First();var t=Convert.ToDouble(r["TotalVisibleMemorySize"]);var f=Convert.ToDouble(r["FreePhysicalMemory"]);return t>0?(t-f)*100/t:0;}catch{return 0;}}
    static double? CpuTemperature(){try{using var s=new ManagementObjectSearcher(@"root\WMI","SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");var v=s.Get().Cast<ManagementObject>().Select(x=>(Convert.ToDouble(x["CurrentTemperature"])/10)-273.15).Where(x=>x>0&&x<150).ToArray();return v.Length>0?Math.Round(v.Max(),1):null;}catch{return null;}}
    static object Power(string args){Process.Start(new ProcessStartInfo("shutdown.exe",args){UseShellExecute=false,CreateNoWindow=true});return new{accepted=true};}

    async Task CheckAlerts(LocalConfig c,CancellationToken ct)
    {
        var now=DateTime.UtcNow;var ram=RamPercent();var temp=CpuTemperature();
        if(c.RamAlertEnabled&&ram>=c.RamAlertPercent&&(now-_lastRamAlert).TotalSeconds>=c.AlertCooldownSeconds){_lastRamAlert=now;await Telegram(c,$"⚠️ FaceUnlock RAM alert\nPC: {c.PcName}\nRAM: {ram:F1}% (limit {c.RamAlertPercent:F0}%)",ct);}
        if(c.TemperatureAlertEnabled&&temp.HasValue&&temp.Value>=c.TemperatureAlertCelsius&&(now-_lastTempAlert).TotalSeconds>=c.AlertCooldownSeconds){_lastTempAlert=now;await Telegram(c,$"🔥 FaceUnlock temperature alert\nPC: {c.PcName}\nCPU: {temp:F1}°C (limit {c.TemperatureAlertCelsius:F0}°C)",ct);}
    }
    static async Task Telegram(LocalConfig c,string text,CancellationToken ct){if(string.IsNullOrWhiteSpace(c.TelegramBotToken)||string.IsNullOrWhiteSpace(c.TelegramChatId))return;using var client=new HttpClient();await client.PostAsJsonAsync($"https://api.telegram.org/bot{c.TelegramBotToken}/sendMessage",new{chat_id=c.TelegramChatId,text},ct);}
}
