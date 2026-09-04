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
    DateTime _lastTempAlert = DateTime.MinValue, _lastRamAlert = DateTime.MinValue, _lastCpuAlert = DateTime.MinValue;
    DateTime? _cpuHighSince;
    int _serverFailures;
    bool _serverAlerted;
    static readonly string AlertPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceUnlock", "Bridge", "alert.json");

    public RemoteControlWorker(ILogger<RemoteControlWorker> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.WhenAll(RemoteLoop(stop), MonitorLoop(stop));
    }

    async Task RemoteLoop(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var cfg=_store.Load();
                if(!string.IsNullOrWhiteSpace(cfg.PcToken))
                {
                    var api=new ApiClient(cfg);
                    var pending=await api.GetRemoteCommandAsync(stop);
                    ServerOk();
                    if(pending.pending&&pending.command!=null) await Handle(api,pending.command,stop);
                }
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested){break;}
            catch(Exception ex){ServerFailed(ex);}
            await Task.Delay(400,stop);
        }
    }

    async Task MonitorLoop(CancellationToken stop)
    {
        while(!stop.IsCancellationRequested)
        {
            try{await CheckAlerts(_store.Load(),stop);}catch(OperationCanceledException) when(stop.IsCancellationRequested){break;}catch(Exception ex){_log.LogWarning(ex,"[MONITOR FAILED] {Message}",ex.Message);}
            await Task.Delay(2000,stop);
        }
    }

    async Task Handle(ApiClient api,RemoteCommand command,CancellationToken ct)
    {
        try
        {
            object result;
            if(command.type=="status")
            {
                await Task.Delay(1000,ct);
                result=ReadStatus();
            }
            else if(command.type=="fetch_file")
            {
                result=await FetchFile(api,command,ct);
            }
            else result=command.type switch
            {
                "signout"=>Power("/l"),
                "restart"=>Power("/r /t 1"),
                "shutdown"=>Power("/s /t 1"),
                _=>throw new InvalidOperationException("Unsupported command")
            };
            await api.CompleteRemoteCommandAsync(command.id,"DONE",result,ct);
        }
        catch(Exception ex)
        {
            _log.LogError(ex,"[REMOTE EXECUTE FAILED] id={CommandId} type={CommandType}",command.id,command.type);
            try{await api.CompleteRemoteCommandAsync(command.id,"ERROR",new{error=ex.Message},ct);}catch{}
        }
    }

    async Task<object> FetchFile(ApiClient api,RemoteCommand command,CancellationToken ct)
    {
        try
        {
            string? fileId=null;
            string? fileName=null;
            if(command.payload!=null)
            {
                if(command.payload.TryGetValue("file_id",out var fidObj))
                    fileId=fidObj is System.Text.Json.JsonElement jeFid?jeFid.GetString():fidObj?.ToString();
                if(command.payload.TryGetValue("name",out var fnObj))
                    fileName=fnObj is System.Text.Json.JsonElement jeFn?jeFn.GetString():fnObj?.ToString();
            }

            HostedTransferFile? file=null;
            if(!string.IsNullOrWhiteSpace(fileId)&&!string.IsNullOrWhiteSpace(fileName))
            {
                file=new HostedTransferFile(fileId,fileName,0,null);
            }
            else
            {
                var pending=await api.GetPendingHostedFileAsync(ct);
                if(pending.pending&&pending.file!=null) file=pending.file;
            }

            if(file==null) throw new InvalidOperationException("No file available for transfer");

            var root=UserDownloadsFaceUnlock();
            Directory.CreateDirectory(root);
            var name=Path.GetFileName(file.name);
            var path=UniquePath(root,name);
            await api.DownloadHostedFileAsync(file,path,ct);
            _log.LogInformation("[HOSTED FILE RECEIVED] id={Id} path={Path}",file.id,path);
            return new{downloaded=true,path};
        }
        catch(Exception ex)
        {
            _log.LogWarning(ex,"[HOSTED FILE FAILED] {Message}",ex.Message);
            WriteAlert("file","File transfer failed",ex.Message);
            throw;
        }
    }

    static string UserDownloadsFaceUnlock(){try{using var s=new ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem");var user=s.Get().Cast<ManagementObject>().Select(x=>x["UserName"]?.ToString()).FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x));var shortName=user?.Split('\\').LastOrDefault();if(!string.IsNullOrWhiteSpace(shortName))return Path.Combine("C:\\Users",shortName,"Downloads","FaceUnlock");}catch{}return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"FaceUnlock","Incoming");}
    static string UniquePath(string dir,string name){var path=Path.Combine(dir,name);if(!File.Exists(path))return path;var stem=Path.GetFileNameWithoutExtension(name);var ext=Path.GetExtension(name);for(var i=1;;i++){path=Path.Combine(dir,$"{stem} ({i}){ext}");if(!File.Exists(path))return path;}}

    static object ReadStatus()=>new{cpu_percent=Math.Round(TotalCpuPercent(),1),ram_percent=Math.Round(RamPercent(),1),temperature_c=CpuTemperature()};
    static double TotalCpuPercent(){try{using var s=new ManagementObjectSearcher("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");var row=s.Get().Cast<ManagementObject>().FirstOrDefault();if(row?["PercentProcessorTime"] is not null){var value=Convert.ToDouble(row["PercentProcessorTime"]);if(value>=0&&value<=100)return value;}}catch{}try{using var s=new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor");var values=s.Get().Cast<ManagementObject>().Select(x=>Convert.ToDouble(x["LoadPercentage"])).ToArray();if(values.Length>0)return Math.Clamp(values.Average(),0,100);}catch{}return 0;}
    static double RamPercent(){try{using var s=new ManagementObjectSearcher("SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem");var r=s.Get().Cast<ManagementObject>().First();var t=Convert.ToDouble(r["TotalVisibleMemorySize"]);var f=Convert.ToDouble(r["FreePhysicalMemory"]);return t>0?(t-f)*100/t:0;}catch{return 0;}}
    static double? CpuTemperature(){try{using var s=new ManagementObjectSearcher(@"root\WMI","SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");var v=s.Get().Cast<ManagementObject>().Select(x=>(Convert.ToDouble(x["CurrentTemperature"])/10)-273.15).Where(x=>x>0&&x<150).ToArray();return v.Length>0?Math.Round(v.Max(),1):null;}catch{return null;}}
    static object Power(string args){Process.Start(new ProcessStartInfo("shutdown.exe",args){UseShellExecute=false,CreateNoWindow=true});return new{accepted=true};}

    async Task CheckAlerts(LocalConfig c,CancellationToken ct)
    {
        var now=DateTime.UtcNow;var cpu=TotalCpuPercent();var ram=RamPercent();var temp=CpuTemperature();
        if(c.TemperatureAlertEnabled&&temp.HasValue&&temp.Value>=c.TemperatureAlertCelsius&&(now-_lastTempAlert).TotalSeconds>=c.AlertCooldownSeconds){_lastTempAlert=now;var m=$"CPU temperature {temp:F1}°C (limit {c.TemperatureAlertCelsius:F0}°C)";WriteAlert("temperature","CPU temperature is too high",m);await Telegram(c,$"🔥 FaceUnlock temperature alert\nPC: {c.PcName}\n{m}",ct);}
        if(c.RamAlertEnabled&&ram>=c.RamAlertPercent&&(now-_lastRamAlert).TotalSeconds>=c.AlertCooldownSeconds){_lastRamAlert=now;var m=$"RAM {ram:F1}% (limit {c.RamAlertPercent:F0}%)";WriteAlert("ram","RAM usage is too high",m);await Telegram(c,$"⚠️ FaceUnlock RAM alert\nPC: {c.PcName}\n{m}",ct);}
        if(c.CpuLoadAlertEnabled&&cpu>=c.CpuLoadAlertPercent){_cpuHighSince??=now;if((now-_cpuHighSince.Value).TotalSeconds>=c.CpuLoadAlertDurationSeconds&&(now-_lastCpuAlert).TotalSeconds>=c.AlertCooldownSeconds){_lastCpuAlert=now;var m=$"Total CPU {cpu:F1}% (limit {c.CpuLoadAlertPercent:F0}%, sustained {c.CpuLoadAlertDurationSeconds}s)";WriteAlert("cpu","Total CPU load is too high",m);await Telegram(c,$"⚙️ FaceUnlock CPU alert\nPC: {c.PcName}\n{m}",ct);}}else if(cpu<=Math.Max(0,c.CpuLoadAlertPercent-5))_cpuHighSince=null;
    }

    void ServerFailed(Exception ex)
    {
        _serverFailures++;
        _log.LogWarning(ex,"[REMOTE POLL FAILED] {Message}",ex.Message);
        if(IsWorkstationLocked())
        {
            // A locked PC can legitimately lose Wi-Fi/network access. Connectivity
            // alerts are intentionally suppressed until an interactive desktop is back.
            _serverFailures=0;
            _serverAlerted=false;
            return;
        }
        if(_serverFailures>=15&&!_serverAlerted)
        {
            _serverAlerted=true;
            WriteAlert("server","FaceUnlock Server connection lost","Remote control has failed repeatedly. FaceUnlock will keep retrying automatically.");
        }
    }
    void ServerOk(){_serverFailures=0;_serverAlerted=false;}

    static bool IsWorkstationLocked()
    {
        try
        {
            var sessionId=WTSGetActiveConsoleSessionId();
            if(sessionId==0xFFFFFFFF)return true;
            IntPtr buffer=IntPtr.Zero;
            uint bytes=0;
            try
            {
                if(!WTSQuerySessionInformation(IntPtr.Zero,sessionId,WTS_INFO_CLASS.WTSConnectState,out buffer,out bytes)||buffer==IntPtr.Zero)return false;
                var state=(WTS_CONNECTSTATE_CLASS)Marshal.ReadInt32(buffer);
                return state!=WTS_CONNECTSTATE_CLASS.WTSActive;
            }
            finally{if(buffer!=IntPtr.Zero)WTSFreeMemory(buffer);}
        }
        catch{return false;}
    }

    void WriteAlert(string key,string title,string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AlertPath)!);
            var tmp=AlertPath+".tmp";
            File.WriteAllText(tmp,JsonSerializer.Serialize(new{key,title,message,created_at=DateTimeOffset.UtcNow.ToUnixTimeSeconds()}));
            File.Move(tmp,AlertPath,true);
            var exe=Path.Combine(AppContext.BaseDirectory,"FaceUnlock.AlertUI.exe");
            if(UserSessionProcess.StartInActiveSession(exe,out var error)) _log.LogInformation("[ALERT UI] requested key={Key}",key);
            else _log.LogWarning("[ALERT UI] could not start key={Key}: {Error}",key,error);
        }
        catch(Exception ex){_log.LogWarning(ex,"[ALERT UI] request failed key={Key}",key);}
    }
    static async Task Telegram(LocalConfig c,string text,CancellationToken ct){if(string.IsNullOrWhiteSpace(c.TelegramBotToken)||string.IsNullOrWhiteSpace(c.TelegramChatId))return;using var client=new HttpClient();await client.PostAsJsonAsync($"https://api.telegram.org/bot{c.TelegramBotToken}/sendMessage",new{chat_id=c.TelegramChatId,text},ct);}

    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("Wtsapi32.dll",SetLastError=true)]
    static extern bool WTSQuerySessionInformation(IntPtr hServer,uint sessionId,WTS_INFO_CLASS infoClass,out IntPtr ppBuffer,out uint pBytesReturned);
    [DllImport("Wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);
    enum WTS_INFO_CLASS{WTSConnectState=8}
    enum WTS_CONNECTSTATE_CLASS{WTSActive,WTSConnected,WTSConnectQuery,WTSShadow,WTSDisconnected,WTSIdle,WTSListen,WTSReset,WTSDown,WTSInit}
}
