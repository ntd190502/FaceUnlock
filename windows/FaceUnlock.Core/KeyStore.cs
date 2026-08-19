using System.Security.Cryptography;
using System.Text;

namespace FaceUnlock.Core;

public sealed class KeyStore
{
    private readonly string _dir;
    private readonly string _privatePath;
    private readonly string _publicPath;
    public KeyStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceUnlock");
        Directory.CreateDirectory(_dir); _privatePath = Path.Combine(_dir, "pc-key.dpapi"); _publicPath = Path.Combine(_dir, "pc-public.pem");
    }
    public string EnsurePublicKeyPem()
    {
        if (File.Exists(_publicPath) && File.Exists(_privatePath)) return File.ReadAllText(_publicPath);
        using var e = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = e.ExportSubjectPublicKeyInfoPem();
        var pkcs8 = e.ExportPkcs8PrivateKey();
        var protectedBytes = ProtectedData.Protect(pkcs8, Encoding.UTF8.GetBytes("FaceUnlock-PC-Key-v1"), DataProtectionScope.LocalMachine);
        File.WriteAllBytes(_privatePath, protectedBytes); File.WriteAllText(_publicPath, pem); CryptographicOperations.ZeroMemory(pkcs8); return pem;
    }
    private ECDsa LoadPrivate()
    {
        EnsurePublicKeyPem(); var enc=File.ReadAllBytes(_privatePath); var raw=ProtectedData.Unprotect(enc, Encoding.UTF8.GetBytes("FaceUnlock-PC-Key-v1"), DataProtectionScope.LocalMachine);
        var e=ECDsa.Create(); e.ImportPkcs8PrivateKey(raw, out _); CryptographicOperations.ZeroMemory(raw); return e;
    }
    public string SignBase64(string message)
    {
        using var e = LoadPrivate();
        return Convert.ToBase64String(e.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    public static string ComputeFingerprint(string pem)
    {
        using var sha = SHA256.Create();
        var clean = pem.Replace("-----BEGIN PUBLIC KEY-----", "")
                       .Replace("-----END PUBLIC KEY-----", "")
                       .Replace("\r", "")
                       .Replace("\n", "")
                       .Trim();
        var bytes = Convert.FromBase64String(clean);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool VerifyPem(string pem, string message, string signatureB64)
    {
        try
        {
            using var e = ECDsa.Create();
            e.ImportFromPem(pem);
            var msgBytes = Encoding.UTF8.GetBytes(message);
            var sigBytes = Convert.FromBase64String(signatureB64);
            return e.VerifyData(msgBytes, sigBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch
        {
            return false;
        }
    }
}
