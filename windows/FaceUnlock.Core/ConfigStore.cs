using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace FaceUnlock.Core;

public sealed class ConfigStore {
    public string PathName { get; }
    private readonly string _tokenPath;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FaceUnlock-PC-Token-v1");

    public ConfigStore(string? path=null) {
        PathName=path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"FaceUnlock","config.json");
        var dir=Path.GetDirectoryName(PathName)!; Directory.CreateDirectory(dir);
        _tokenPath=Path.Combine(dir,"pctoken.dpapi");
    }

    public LocalConfig Load() {
        LocalConfig cfg=new();
        if(File.Exists(PathName)) try { cfg=JsonSerializer.Deserialize<LocalConfig>(File.ReadAllText(PathName)) ?? new(); } catch {}
        if(!string.IsNullOrWhiteSpace(cfg.PcToken)) {
            var token=cfg.PcToken; SaveToken(token); cfg.PcToken=null; Save(cfg); cfg.PcToken=token;
        } else cfg.PcToken=LoadToken();
        return cfg;
    }

    public PairingState GetPairingState() {
        var cfg=Load();
        if(string.IsNullOrWhiteSpace(cfg.DeviceId)) return new(false,"device_id_missing");
        if(string.IsNullOrWhiteSpace(cfg.DevicePublicKeyPem)) return new(false,"device_public_key_missing");
        if(string.IsNullOrWhiteSpace(cfg.PcToken)) return new(false,File.Exists(_tokenPath)?"pc_token_unreadable":"pc_token_missing");
        return new(true,"paired_secure_token");
    }

    public void Save(LocalConfig c) {
        if(!string.IsNullOrWhiteSpace(c.PcToken)) SaveToken(c.PcToken);
        var clone=new LocalConfig { ServerUrl=c.ServerUrl,PcId=c.PcId,PcName=c.PcName,PcToken=null,PairId=c.PairId,
            DeviceId=c.DeviceId,DevicePublicKeyPem=c.DevicePublicKeyPem,Devices=c.Devices };
        File.WriteAllText(PathName,JsonSerializer.Serialize(clone,new JsonSerializerOptions{WriteIndented=true}));
    }

    private void SaveToken(string token) {
        var raw=Encoding.UTF8.GetBytes(token);
        try { File.WriteAllBytes(_tokenPath,ProtectedData.Protect(raw,Entropy,DataProtectionScope.LocalMachine)); }
        catch { File.WriteAllBytes(_tokenPath,ProtectedData.Protect(raw,Entropy,DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    private string? LoadToken() {
        if(!File.Exists(_tokenPath)) return null;
        try {
            var enc=File.ReadAllBytes(_tokenPath); byte[] raw;
            try { raw=ProtectedData.Unprotect(enc,Entropy,DataProtectionScope.LocalMachine); }
            catch { raw=ProtectedData.Unprotect(enc,Entropy,DataProtectionScope.CurrentUser); }
            var token=Encoding.UTF8.GetString(raw); CryptographicOperations.ZeroMemory(raw); return token;
        } catch { return null; }
    }
}
public sealed record PairingState(bool IsPaired,string Reason);
