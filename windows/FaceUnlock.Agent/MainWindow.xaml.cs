using FaceUnlock.Core;
using QRCoder;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FaceUnlock.Agent;

public partial class MainWindow : Window
{
    private readonly ConfigStore store = new();
    private LocalConfig cfg;
    private readonly KeyStore keys = new();
    private ApiClient api;
    private readonly BleScanner ble = new();
    private CancellationTokenSource? pairCts;
    private CancellationTokenSource? operationCts;

    public MainWindow()
    {
        InitializeComponent();
        cfg = store.Load();
        api = new ApiClient(cfg);
        RefreshSetupStatus();
    }

    bool Paired() => store.GetPairingState().IsPaired;
    static string InstallDir => AppContext.BaseDirectory.TrimEnd('\\');
    static string LogDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceUnlock", "logs");

    static bool ShellEnabled()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon");
            return (k?.GetValue("Shell") as string)?.Contains("FaceUnlockShell.exe", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch { return false; }
    }

    static FaceUnlockServiceHealth ProbeServiceHealth()
    {
        try
        {
            using var controller = new ServiceController("FaceUnlock Service");
            ServiceControllerStatus status;
            try { status = controller.Status; }
            catch (InvalidOperationException) { return FaceUnlockServiceHealth.Missing; }
            if (status != ServiceControllerStatus.Running) return FaceUnlockServiceHealth.Stopped;

            using var pipe = new NamedPipeClientStream(".", "FaceUnlock.Auth.v1", PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(750);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), true, 1024, true);
            var requestId = Guid.NewGuid().ToString("N");
            writer.WriteLine(JsonSerializer.Serialize(new { version = 1, command = "ping", request_id = requestId, client_type = "agent_health" }));
            var response = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(response)) return FaceUnlockServiceHealth.Unhealthy;
            using var json = JsonDocument.Parse(response);
            return json.RootElement.TryGetProperty("status", out var s) && s.GetString()?.Equals("ok", StringComparison.OrdinalIgnoreCase) == true
                ? FaceUnlockServiceHealth.Healthy : FaceUnlockServiceHealth.Unhealthy;
        }
        catch { return FaceUnlockServiceHealth.Unhealthy; }
    }

    void RefreshSetupStatus()
    {
        cfg = store.Load();
        api = new ApiClient(cfg);
        var state = store.GetPairingState();
        var paired = state.IsPaired;
        var service = ProbeServiceHealth();
        var shell = ShellEnabled();
        var readiness = SetupReadiness.Evaluate(paired, state.Reason, service, shell);

        Status.Text = $"PAIRING: {(paired ? "Paired" : "Not Paired")}   SERVICE: {readiness.ServiceLabel}   SHELL GATE: {(shell ? "Enabled" : "Disabled")}";
        SetupStatus.Text = readiness.Message;
        PairingInfo.Text = $"iPhone: {(cfg.DeviceId is null ? "Not paired" : cfg.DeviceId)}";
        ServiceInfo.Text = $"Service: {readiness.ServiceLabel}";
        ShellInfo.Text = $"Shell Gate: {(shell ? "Enabled" : "Disabled")}";
        EnableButton.Visibility = readiness.CanEnableShellGate ? Visibility.Visible : Visibility.Collapsed;
    }

    void Write(string s)
    {
        Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");
        Log.ScrollToEnd();
        LastActionInfo.Text = "Last action: " + s.Split('\n')[0];
    }

    void ShowQr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var img = new BitmapImage();
        using var ms = new MemoryStream(png);
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        QrImage.Source = img;
    }

    async void Pair_Click(object sender, RoutedEventArgs e)
    {
        pairCts?.Cancel();
        pairCts?.Dispose();
        pairCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        PairButton.IsEnabled = false;
        CancelPairButton.IsEnabled = true;
        try
        {
            var pub = keys.EnsurePublicKeyPem();
            var r = await api.StartPairAsync(pub);
            cfg.PcToken = r.pc_token;
            cfg.PairId = r.pair_id;
            store.Save(cfg);
            api = new ApiClient(cfg);
            var payload = new { type = "faceunlock-pair-v1", server = cfg.ServerUrl, pair_id = r.pair_id, pair_code = r.pair_code, pc_id = cfg.PcId, pc_name = cfg.PcName, pc_public_key_pem = pub };
            ShowQr(JsonSerializer.Serialize(payload));
            Write("Pair QR shown. Scan it in the iPhone app.");

            while (!pairCts.IsCancellationRequested)
            {
                await Task.Delay(2000, pairCts.Token);
                var s = await api.PairStatusAsync(r.pair_id);
                if (!s.paired || s.device is null) continue;
                cfg.DeviceId = s.device.id;
                cfg.DevicePublicKeyPem = s.device.public_key_pem;
                cfg.Devices.RemoveAll(d => d.id == s.device.id);
                cfg.Devices.Add(s.device);
                store.Save(cfg);
                Write("Pairing complete.");
                RefreshSetupStatus();
                return;
            }
        }
        catch (OperationCanceledException) { Write("Pairing cancelled or timed out."); }
        catch (Exception ex) { Write(ex.Message); }
        finally
        {
            PairButton.IsEnabled = true;
            CancelPairButton.IsEnabled = false;
        }
    }

    void CancelPair_Click(object sender, RoutedEventArgs e) => pairCts?.Cancel();

    async void Enable_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Paired()) throw new InvalidOperationException("Pair your iPhone first.");
            EnableButton.IsEnabled = false;
            var script = Path.Combine(InstallDir, "Enable-ShellGate.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("Enable-ShellGate.ps1 is missing", script);
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-ExecutionPolicy Bypass -File \"{script}\" -Force -CustomShellPath \"{Path.Combine(InstallDir, "FaceUnlockShell.exe")}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
            await p!.WaitForExitAsync();
            if (p.ExitCode != 0) throw new InvalidOperationException(await p.StandardError.ReadToEndAsync());
            Write("FaceUnlock enabled for next sign-in/restart.");
        }
        catch (Exception ex) { Write(ex.Message); }
        finally { EnableButton.IsEnabled = true; RefreshSetupStatus(); }
    }

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshSetupStatus();
        Write("Runtime status refreshed.");
    }

    async void RestartService_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var svc = new ServiceController("FaceUnlock Service");
            if (svc.Status != ServiceControllerStatus.Stopped)
            {
                svc.Stop();
                await Task.Run(() => svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)));
            }
            svc.Start();
            await Task.Run(() => svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10)));
            Write("FaceUnlock Service restarted.");
        }
        catch (Exception ex) { Write("Service restart failed: " + ex.Message); }
        finally { RefreshSetupStatus(); }
    }

    void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogDir}\"") { UseShellExecute = true });
            Write("Opened FaceUnlock logs.");
        }
        catch (Exception ex) { Write("Could not open logs: " + ex.Message); }
    }

    async void Online_Click(object sender, RoutedEventArgs e)
    {
        operationCts?.Cancel();
        operationCts = new CancellationTokenSource();
        OnlineButton.IsEnabled = false;
        try
        {
            if (cfg.DevicePublicKeyPem is null || cfg.DeviceId is null) throw new InvalidOperationException("Pair first");
            var selectedDeviceId = cfg.DeviceId;
            var selectedDevicePublicKeyPem = cfg.DevicePublicKeyPem;
            var r = await api.RequestUnlockAsync();
            Write($"Online session {r.session_id} created.");

            while (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < r.expires_at)
            {
                await Task.Delay(1000, operationCts.Token);
                var s = await api.GetUnlockStatusAsync(r.session_id);
                Status.Text = "Online: " + s.status;
                if (s.status == "APPROVED")
                {
                    var canonical = Protocol.Canonical(s.session_id, s.challenge, cfg.PcId, s.expires_at);
                    var winnerId = s.winning_device_id ?? s.device_id ?? selectedDeviceId;
                    var winnerPem = winnerId == selectedDeviceId ? selectedDevicePublicKeyPem : cfg.Devices.FirstOrDefault(d => d.id == winnerId)?.public_key_pem;
                    if (string.IsNullOrWhiteSpace(winnerPem)) throw new CryptographicException($"Unknown approved device {winnerId}");
                    if (s.signature is null || !KeyStore.VerifyPem(winnerPem, canonical, s.signature)) throw new CryptographicException("Invalid iPhone signature");
                    Write("Online Face ID approval verified.");
                    return;
                }
                if (s.status is "REJECTED" or "EXPIRED") { Write("Online test ended: " + s.status); return; }
            }
            Write("Online test timed out.");
        }
        catch (OperationCanceledException) { Write("Online test cancelled."); }
        catch (Exception ex) { Write(ex.Message); }
        finally { OnlineButton.IsEnabled = true; RefreshSetupStatus(); }
    }

    async void Ble_Click(object sender, RoutedEventArgs e)
    {
        operationCts?.Cancel();
        operationCts = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        BleButton.IsEnabled = false;
        try
        {
            if (cfg.DevicePublicKeyPem is null) throw new InvalidOperationException("Pair first");
            var session = Guid.NewGuid().ToString("N");
            var challenge = Protocol.RandomToken();
            var exp = DateTimeOffset.UtcNow.AddSeconds(90).ToUnixTimeSeconds();
            var msg = Protocol.OfflineRequestCanonical(session, challenge, cfg.PcId, exp);
            var payload = new OfflineUnlockPayload("faceunlock-offline-v1", session, cfg.PcId, cfg.PcName, challenge, exp, keys.SignBase64(msg));
            Write("Scanning iPhone BLE...");
            var result = await ble.DiscoverAndApproveAsync(payload, cfg.DeviceId, TimeSpan.FromSeconds(8), operationCts.Token);
            if (result is null)
            {
                ShowQr(JsonSerializer.Serialize(payload));
                Write("iPhone not found. QR fallback shown; continuing BLE scan.");
                while (result is null && !operationCts.IsCancellationRequested)
                    result = await ble.DiscoverAndApproveAsync(payload, cfg.DeviceId, TimeSpan.FromSeconds(5), operationCts.Token);
            }
            if (result?.ok == "true" && result.signature is not null)
            {
                var canonical = Protocol.Canonical(session, challenge, cfg.PcId, exp);
                if (!KeyStore.VerifyPem(cfg.DevicePublicKeyPem, canonical, result.signature)) throw new CryptographicException("Bad iPhone BLE signature");
                Write("Offline Face ID approval verified.");
            }
            else Write("BLE test ended without approval: " + (result?.error ?? "timeout"));
        }
        catch (OperationCanceledException) { Write("BLE test cancelled or timed out."); }
        catch (Exception ex) { Write(ex.Message); }
        finally { BleButton.IsEnabled = true; RefreshSetupStatus(); }
    }

    protected override void OnClosed(EventArgs e)
    {
        pairCts?.Cancel();
        operationCts?.Cancel();
        pairCts?.Dispose();
        operationCts?.Dispose();
        base.OnClosed(e);
    }
}
