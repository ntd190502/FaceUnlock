using FaceUnlock.Core;
using QRCoder;
using System.Text.Json;
using System.IO;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.IO.Pipes;
using System.Text;
using System.ServiceProcess;

namespace FaceUnlock.Agent;
public partial class MainWindow : Window
{
    readonly ConfigStore store=new(); LocalConfig cfg; KeyStore keys=new(); ApiClient api; BleScanner ble=new();
    public MainWindow(){InitializeComponent();cfg=store.Load();api=new ApiClient(cfg);RefreshSetupStatus();}
    bool Paired()=>store.GetPairingState().IsPaired;
    static string InstallDir=>AppContext.BaseDirectory.TrimEnd('\\');
    static bool ShellEnabled(){try{using var k=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon");return (k?.GetValue("Shell") as string)?.Contains("FaceUnlockShell.exe",StringComparison.OrdinalIgnoreCase)==true;}catch{return false;}}
    static FaceUnlockServiceHealth ProbeServiceHealth()
    {
        try
        {
            using var controller=new ServiceController("FaceUnlock Service");
            ServiceControllerStatus controllerStatus;
            try{controllerStatus=controller.Status;}
            catch(InvalidOperationException){return FaceUnlockServiceHealth.Missing;}
            if(controllerStatus!=ServiceControllerStatus.Running)return FaceUnlockServiceHealth.Stopped;

            using var pipe=new NamedPipeClientStream(".","FaceUnlock.Auth.v1",PipeDirection.InOut,PipeOptions.None);
            pipe.Connect(750);
            using var writer=new StreamWriter(pipe,new UTF8Encoding(false),1024,true){AutoFlush=true};
            using var reader=new StreamReader(pipe,new UTF8Encoding(false),false,1024,true);
            var requestId=Guid.NewGuid().ToString("N");
            writer.WriteLine(JsonSerializer.Serialize(new{version=1,command="ping",request_id=requestId,client_type="agent_health"}));
            var response=reader.ReadLine();
            if(string.IsNullOrWhiteSpace(response))return FaceUnlockServiceHealth.Unhealthy;
            using var json=JsonDocument.Parse(response);
            return json.RootElement.TryGetProperty("status",out var status)&&status.GetString()?.Equals("ok",StringComparison.OrdinalIgnoreCase)==true
                ? FaceUnlockServiceHealth.Healthy : FaceUnlockServiceHealth.Unhealthy;
        }
        catch(System.TimeoutException){return FaceUnlockServiceHealth.Unhealthy;}
        catch{return FaceUnlockServiceHealth.Unhealthy;}
    }
    void RefreshSetupStatus(){var state=store.GetPairingState();var paired=state.IsPaired;var service=ProbeServiceHealth();var shell=ShellEnabled();var readiness=SetupReadiness.Evaluate(paired,state.Reason,service,shell);Status.Text=$"PAIRING: {(paired?"Paired":"Not Paired")}   SERVICE: {readiness.ServiceLabel}   SHELL GATE: {(shell?"Enabled":"Disabled")}";SetupStatus.Text=readiness.Message;EnableButton.Visibility=readiness.CanEnableShellGate?Visibility.Visible:Visibility.Collapsed;}
    void Write(string s){Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");Log.ScrollToEnd();}
    void ShowQr(string text){using var gen=new QRCodeGenerator();using var data=gen.CreateQrCode(text,QRCodeGenerator.ECCLevel.Q);var png=new PngByteQRCode(data).GetGraphic(8);var img=new BitmapImage();using var ms=new MemoryStream(png);img.BeginInit();img.CacheOption=BitmapCacheOption.OnLoad;img.StreamSource=ms;img.EndInit();QrImage.Source=img;}

    async void Pair_Click(object sender,RoutedEventArgs e){try{var pub=keys.EnsurePublicKeyPem();var r=await api.StartPairAsync(pub);cfg.PcToken=r.pc_token;cfg.PairId=r.pair_id;store.Save(cfg);api=new ApiClient(cfg);var payload=new{type="faceunlock-pair-v1",server=cfg.ServerUrl,pair_id=r.pair_id,pair_code=r.pair_code,pc_id=cfg.PcId,pc_name=cfg.PcName,pc_public_key_pem=pub};ShowQr(JsonSerializer.Serialize(payload));Write("Pair QR shown. Scan it in the iPhone app.");for(int i=0;i<60;i++){await Task.Delay(2000);var s=await api.PairStatusAsync(r.pair_id);if(s.paired&&s.device is not null){cfg.DeviceId=s.device.id;cfg.DevicePublicKeyPem=s.device.public_key_pem;store.Save(cfg);RefreshSetupStatus();Write("Pairing complete. Enable FaceUnlock is now available.");break;}}}catch(Exception ex){Write(ex.ToString());}}

    async void Enable_Click(object sender,RoutedEventArgs e){try{if(!Paired())throw new InvalidOperationException("Pair your iPhone first.");EnableButton.IsEnabled=false;var script=Path.Combine(InstallDir,"Enable-ShellGate.ps1");if(!File.Exists(script))throw new FileNotFoundException("Enable-ShellGate.ps1 is missing",script);using var p=Process.Start(new ProcessStartInfo("powershell.exe",$"-ExecutionPolicy Bypass -File \"{script}\" -Force -CustomShellPath \"{Path.Combine(InstallDir,"FaceUnlockShell.exe")}\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true});await p!.WaitForExitAsync();if(p.ExitCode!=0)throw new InvalidOperationException(await p.StandardError.ReadToEndAsync());RefreshSetupStatus();Write("FaceUnlock is ready and will be active on next sign-in/restart.");}catch(Exception ex){Write(ex.Message);RefreshSetupStatus();}finally{EnableButton.IsEnabled=true;}}

    async void Online_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if (cfg.DevicePublicKeyPem is null || cfg.DeviceId is null)
                throw new InvalidOperationException("Pair first");

            var selectedDeviceId = cfg.DeviceId;
            var selectedDevicePublicKeyPem = cfg.DevicePublicKeyPem;
            var r = await api.RequestUnlockAsync(selectedDeviceId);
            Write($"Session {r.session_id}; push={r.push_sent}; {r.push_error}");

            while (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < r.expires_at)
            {
                await Task.Delay(1000);
                var s = await api.GetUnlockStatusAsync(r.session_id);
                Status.Text = "Online: " + s.status;

                if (s.status == "APPROVED")
                {
                    var canonical = Protocol.Canonical(s.session_id, s.challenge, cfg.PcId, s.expires_at);
                    var canonicalBytes = System.Text.Encoding.UTF8.GetBytes(canonical);
                    var canonicalHex = Convert.ToHexString(canonicalBytes).ToLowerInvariant();
                    var pubFp = KeyStore.ComputeFingerprint(selectedDevicePublicKeyPem);

                    Write($"""
                    WINDOWS VERIFY:
                    session_id={s.session_id}
                    challenge={s.challenge}
                    pc_id={cfg.PcId}
                    expires_at={s.expires_at}
                    canonical UTF8={canonical}
                    canonical UTF8 hex={canonicalHex}
                    signature base64={s.signature}
                    public key fingerprint={pubFp}
                    selected device_id={selectedDeviceId}
                    response device_id={s.device_id ?? r.device_id}
                    """);

                    if (!string.IsNullOrWhiteSpace(s.device_public_key_pem))
                    {
                        var respFp = KeyStore.ComputeFingerprint(s.device_public_key_pem);
                        if (respFp != pubFp)
                            throw new CryptographicException($"Device public key mismatch: server returned key fingerprint {respFp}, expected {pubFp}");
                    }

                    if (s.signature is null || !KeyStore.VerifyPem(selectedDevicePublicKeyPem, canonical, s.signature))
                        throw new CryptographicException("Server says APPROVED but iPhone signature is invalid");

                    Write("Face ID approval verified locally. Integration layer may proceed.");
                    return;
                }
                if (s.status is "REJECTED" or "EXPIRED") return;
            }
        }
        catch (Exception ex)
        {
            Write(ex.ToString());
        }
    }

    async void Ble_Click(object sender,RoutedEventArgs e){try{if(cfg.DevicePublicKeyPem is null)throw new InvalidOperationException("Pair first");var session=Guid.NewGuid().ToString("N");var challenge=Protocol.RandomToken();var exp=DateTimeOffset.UtcNow.AddSeconds(90).ToUnixTimeSeconds();var msg=Protocol.OfflineRequestCanonical(session,challenge,cfg.PcId,exp);var payload=new OfflineUnlockPayload("faceunlock-offline-v1",session,cfg.PcId,cfg.PcName,challenge,exp,keys.SignBase64(msg));Write("Scanning iPhone BLE for 8 seconds...");var result=await ble.DiscoverAndApproveAsync(payload,TimeSpan.FromSeconds(8),CancellationToken.None);if(result is null){ShowQr(JsonSerializer.Serialize(payload));Write("iPhone not found. Offline QR shown; open FaceUnlock on iPhone and scan it. Windows will keep scanning for 60 seconds.");var until=DateTime.UtcNow.AddSeconds(60);while(result is null && DateTime.UtcNow<until){result=await ble.DiscoverAndApproveAsync(payload,TimeSpan.FromSeconds(5),CancellationToken.None);}if(result is null){Write("QR fallback timed out. Use PIN/password or retry.");return;}}if(result.ok=="true"&&result.signature is not null){var canonical=Protocol.Canonical(session,challenge,cfg.PcId,exp);if(!KeyStore.VerifyPem(cfg.DevicePublicKeyPem,canonical,result.signature))throw new CryptographicException("Bad iPhone BLE signature");Write("Offline Face ID approval verified.");}else Write("BLE rejected: "+result.error);}catch(Exception ex){Write(ex.ToString());}}
}
