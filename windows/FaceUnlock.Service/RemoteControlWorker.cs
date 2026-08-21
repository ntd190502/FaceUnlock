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

    static readonly string BridgeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FaceUnlock", "Bridge");

    static string Bridge(string name)
    {
        Directory.CreateDirectory(BridgeDir);
        return Path.Combine(BridgeDir, name);
    }

    sealed class BridgeApp
    {
        public int id { get; set; }
        public string name { get; set; } = "";
        public string title { get; set; } = "";
    }

    sealed class AppsBridgeResponse
    {
        public string request_id { get; set; } = "";
        public BridgeApp[] apps { get; set; } = Array.Empty<BridgeApp>();
    }

    sealed class CloseBridgeResponse
    {
        public string request_id { get; set; } = "";
        public bool ok { get; set; }
        public int pid { get; set; }
        public string? error { get; set; }
    }

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
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[REMOTE POLL FAILED] {Message}", ex.Message);
            }

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
            try
            {
                _log.LogInformation("[REMOTE RESULT POST] id={CommandId} status=ERROR", command.id);
                await api.CompleteRemoteCommandAsync(command.id, "ERROR", new { error = ex.Message }, ct);
                _log.LogInformation("[REMOTE ERROR REPORTED] id={CommandId}", command.id);
            }
            catch (Exception reportError)
            {
                _log.LogError(reportError, "[REMOTE RESULT POST FAILED] id={CommandId}: {Message}", command.id, reportError.Message);
            }
        }
    }

    object ReadStatusLogged(string id)
    {
        var result = ReadStatus();
        _log.LogInformation("[REMOTE STATUS READY] id={CommandId} result={Result}", id, JsonSerializer.Serialize(result));
        return result;
    }

    static object ReadStatus()
    {
        double cpu = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor");
            var values = searcher.Get().Cast<ManagementObject>()
                .Select(x => Convert.ToDouble(x["LoadPercentage"]))
                .ToArray();
            if (values.Length > 0) cpu = values.Average();
        }
        catch { }

        return new
        {
            cpu_percent = Math.Round(cpu, 1),
            ram_percent = Math.Round(RamPercent(), 1),
            temperature_c = CpuTemperature()
        };
    }

    static double RamPercent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem");
            var row = searcher.Get().Cast<ManagementObject>().First();
            var total = Convert.ToDouble(row["TotalVisibleMemorySize"]);
            var free = Convert.ToDouble(row["FreePhysicalMemory"]);
            return total > 0 ? (total - free) * 100 / total : 0;
        }
        catch { return 0; }
    }

    static double? CpuTemperature()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            var values = searcher.Get().Cast<ManagementObject>()
                .Select(x => (Convert.ToDouble(x["CurrentTemperature"]) / 10) - 273.15)
                .Where(x => x > 0 && x < 150)
                .ToArray();
            return values.Length > 0 ? Math.Round(values.Max(), 1) : null;
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSDisconnectSession(IntPtr hServer, uint sessionId, bool wait);

    static object Lock()
    {
        var id = WTSGetActiveConsoleSessionId();
        if (id == 0xFFFFFFFF || !WTSDisconnectSession(IntPtr.Zero, id, false))
            throw new InvalidOperationException("Could not lock active console session");
        return new { locked = true };
    }

    static object Power(string args)
    {
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });
        return new { accepted = true };
    }

    static object Apps()
    {
        var requestId = Guid.NewGuid().ToString("N");
        var resultFile = Bridge("apps.result.json");
        TryDelete(resultFile);
        File.WriteAllText(Bridge("apps.request"), requestId);

        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(100);
            if (!File.Exists(resultFile)) continue;
            try
            {
                var response = JsonSerializer.Deserialize<AppsBridgeResponse>(File.ReadAllText(resultFile));
                if (response?.request_id == requestId)
                    return new { apps = response.apps };
            }
            catch (IOException) { }
            catch (JsonException) { }
        }

        throw new InvalidOperationException("FaceUnlock Agent interactive bridge did not return the running application list");
    }

    static object CloseApp(Dictionary<string, object>? payload)
    {
        if (payload == null || !payload.TryGetValue("pid", out var raw) || !int.TryParse(raw?.ToString(), out var pid))
            throw new InvalidOperationException("pid required");

        var requestId = Guid.NewGuid().ToString("N");
        var resultFile = Bridge("close-app.result.json");
        TryDelete(resultFile);
        File.WriteAllText(Bridge("close-app.request.json"), JsonSerializer.Serialize(new { request_id = requestId, pid }));

        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(100);
            if (!File.Exists(resultFile)) continue;
            try
            {
                var response = JsonSerializer.Deserialize<CloseBridgeResponse>(File.ReadAllText(resultFile));
                if (response?.request_id != requestId) continue;
                if (!response.ok) throw new InvalidOperationException(response.error ?? "Could not close application");
                return new { closed = true, pid };
            }
            catch (IOException) { }
            catch (JsonException) { }
        }

        throw new InvalidOperationException("FaceUnlock Agent interactive bridge did not respond to close application request");
    }

    static object ClipboardSet(Dictionary<string, object>? payload)
    {
        var text = payload != null && payload.TryGetValue("text", out var value) ? value?.ToString() ?? "" : "";
        File.WriteAllText(Bridge("clipboard-in.txt"), text);
        return new { queued = true };
    }

    static object ClipboardGet()
    {
        var file = Bridge("clipboard-out.txt");
        return new { text = File.Exists(file) ? File.ReadAllText(file) : "", available = File.Exists(file) };
    }

    static object FileUpload(Dictionary<string, object>? payload)
    {
        if (payload == null || !payload.TryGetValue("name", out var nameValue) || !payload.TryGetValue("base64", out var base64Value))
            throw new InvalidOperationException("name/base64 required");

        var name = Path.GetFileName(nameValue?.ToString() ?? "upload.bin");
        var data = Convert.FromBase64String(base64Value?.ToString() ?? "");
        if (data.Length > 8 * 1024 * 1024)
            throw new InvalidOperationException("File exceeds 8 MB remote relay limit");

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceUnlock", "Incoming");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, data);
        File.WriteAllText(Bridge("incoming-file.path"), path);
        return new { saved = true, name, size = data.Length };
    }

    static object ClipboardFile()
    {
        var marker = Bridge("clipboard-file.path");
        if (!File.Exists(marker)) return new { available = false };

        var path = File.ReadAllText(marker).Trim();
        if (!File.Exists(path)) return new { available = false };

        var data = File.ReadAllBytes(path);
        if (data.Length > 8 * 1024 * 1024)
            throw new InvalidOperationException("File exceeds 8 MB remote relay limit");

        return new
        {
            available = true,
            name = Path.GetFileName(path),
            base64 = Convert.ToBase64String(data),
            size = data.Length
        };
    }

    static object Screenshot()
    {
        var request = Bridge("screenshot.request");
        var file = Bridge("screenshot.jpg");
        TryDelete(file);
        File.WriteAllText(request, DateTime.UtcNow.Ticks.ToString());

        for (var i = 0; i < 30 && !File.Exists(file); i++) Thread.Sleep(100);
        if (!File.Exists(file))
            return new { available = false, error = "FaceUnlock Agent interactive bridge is not running" };

        var data = File.ReadAllBytes(file);
        return new { available = true, mime = "image/jpeg", base64 = Convert.ToBase64String(data) };
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    async Task CheckAlerts(LocalConfig config, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ram = RamPercent();
        var temp = CpuTemperature();

        if (config.RamAlertEnabled && ram >= config.RamAlertPercent && (now - _lastRamAlert).TotalSeconds >= config.AlertCooldownSeconds)
        {
            _lastRamAlert = now;
            await Telegram(config, $"⚠️ FaceUnlock RAM alert\nPC: {config.PcName}\nRAM: {ram:F1}% (limit {config.RamAlertPercent:F0}%)", ct);
        }

        if (config.TemperatureAlertEnabled && temp.HasValue && temp.Value >= config.TemperatureAlertCelsius && (now - _lastTempAlert).TotalSeconds >= config.AlertCooldownSeconds)
        {
            _lastTempAlert = now;
            await Telegram(config, $"🔥 FaceUnlock temperature alert\nPC: {config.PcName}\nCPU: {temp:F1}°C (limit {config.TemperatureAlertCelsius:F0}°C)", ct);
        }
    }

    static async Task Telegram(LocalConfig config, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.TelegramBotToken) || string.IsNullOrWhiteSpace(config.TelegramChatId)) return;
        using var client = new HttpClient();
        await client.PostAsJsonAsync($"https://api.telegram.org/bot{config.TelegramBotToken}/sendMessage", new { chat_id = config.TelegramChatId, text }, ct);
    }
}
