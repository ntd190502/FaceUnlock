using FaceUnlock.Core;
using QRCoder;
using System.Text.Json;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FaceUnlock.Agent;
public partial class MainWindow : Window
{
    readonly ConfigStore store=new(); LocalConfig cfg; KeyStore keys=new(); ApiClient api; BleScanner ble=new();
    public MainWindow(){InitializeComponent();cfg=store.Load();api=new ApiClient(cfg);Status.Text=$"PC: {cfg.PcName} | {(cfg.DeviceId is null?"Not paired":"Paired")}";}
    void Write(string s){Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");Log.ScrollToEnd();}
    void ShowQr(string text){using var gen=new QRCodeGenerator();using var data=gen.CreateQrCode(text,QRCodeGenerator.ECCLevel.Q);var png=new PngByteQRCode(data).GetGraphic(8);var img=new BitmapImage();using var ms=new MemoryStream(png);img.BeginInit();img.CacheOption=BitmapCacheOption.OnLoad;img.StreamSource=ms;img.EndInit();QrImage.Source=img;}

    async void Pair_Click(object sender,RoutedEventArgs e){try{var pub=keys.EnsurePublicKeyPem();var r=await api.StartPairAsync(pub);cfg.PcToken=r.pc_token;cfg.PairId=r.pair_id;store.Save(cfg);api=new ApiClient(cfg);var payload=new{type="faceunlock-pair-v1",server=cfg.ServerUrl,pair_id=r.pair_id,pair_code=r.pair_code,pc_id=cfg.PcId,pc_name=cfg.PcName,pc_public_key_pem=pub};ShowQr(JsonSerializer.Serialize(payload));Write("Pair QR shown. Scan it in the iPhone app.");for(int i=0;i<60;i++){await Task.Delay(2000);var s=await api.PairStatusAsync(r.pair_id);if(s.paired&&s.device is not null){cfg.DeviceId=s.device.id;cfg.DevicePublicKeyPem=s.device.public_key_pem;store.Save(cfg);Status.Text=$"PC: {cfg.PcName} | Paired: {s.device.name}";Write("Pairing complete.");break;}}}catch(Exception ex){Write(ex.ToString());}}

    async void Online_Click(object sender,RoutedEventArgs e){try{if(cfg.DevicePublicKeyPem is null)throw new InvalidOperationException("Pair first");var r=await api.RequestUnlockAsync();Write($"Session {r.session_id}; push={r.push_sent}; {r.push_error}");while(DateTimeOffset.UtcNow.ToUnixTimeSeconds()<r.expires_at){await Task.Delay(1000);var s=await api.GetUnlockStatusAsync(r.session_id);Status.Text="Online: "+s.status;if(s.status=="APPROVED"){var canonical=Protocol.Canonical(s.session_id,s.challenge,cfg.PcId,s.expires_at);if(s.signature is null||!KeyStore.VerifyPem(cfg.DevicePublicKeyPem,canonical,s.signature))throw new CryptographicException("Server says APPROVED but iPhone signature is invalid");Write("Face ID approval verified locally. Integration layer may proceed.");return;}if(s.status is "REJECTED" or "EXPIRED")return;}}catch(Exception ex){Write(ex.ToString());}}

    async void Ble_Click(object sender,RoutedEventArgs e){try{if(cfg.DevicePublicKeyPem is null)throw new InvalidOperationException("Pair first");var session=Guid.NewGuid().ToString("N");var challenge=Protocol.RandomToken();var exp=DateTimeOffset.UtcNow.AddSeconds(90).ToUnixTimeSeconds();var msg=Protocol.OfflineRequestCanonical(session,challenge,cfg.PcId,exp);var payload=new OfflineUnlockPayload("faceunlock-offline-v1",session,cfg.PcId,cfg.PcName,challenge,exp,keys.SignBase64(msg));Write("Scanning iPhone BLE for 8 seconds...");var result=await ble.DiscoverAndApproveAsync(payload,TimeSpan.FromSeconds(8),CancellationToken.None);if(result is null){ShowQr(JsonSerializer.Serialize(payload));Write("iPhone not found. Offline QR shown; open FaceUnlock on iPhone and scan it. Windows will keep scanning for 60 seconds.");var until=DateTime.UtcNow.AddSeconds(60);while(result is null && DateTime.UtcNow<until){result=await ble.DiscoverAndApproveAsync(payload,TimeSpan.FromSeconds(5),CancellationToken.None);}if(result is null){Write("QR fallback timed out. Use PIN/password or retry.");return;}}if(result.ok=="true"&&result.signature is not null){var canonical=Protocol.Canonical(session,challenge,cfg.PcId,exp);if(!KeyStore.VerifyPem(cfg.DevicePublicKeyPem,canonical,result.signature))throw new CryptographicException("Bad iPhone BLE signature");Write("Offline Face ID approval verified.");}else Write("BLE rejected: "+result.error);}catch(Exception ex){Write(ex.ToString());}}
}
