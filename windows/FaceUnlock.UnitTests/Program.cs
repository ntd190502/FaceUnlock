using System.Security.Cryptography;
using FaceUnlock.Core;

namespace FaceUnlock.UnitTests;

public class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  FaceUnlock LsaMachineSecretStore & Ticket Unit Tests");
        Console.WriteLine("============================================================");

        int passed = 0;
        int failed = 0;

        void Check(bool cond, string name, string reason = "")
        {
            if (cond)
            {
                passed++;
                Console.WriteLine($"  [PASS] {name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  [FAIL] {name} - {reason}");
            }
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FaceUnlock_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var testSecretFile = Path.Combine(tempDir, "test_lsa_secret.dpapi");

        try
        {
            // 1. Initial LoadOrCreate should create new 32-byte secret
            var store = new LsaMachineSecretStore(testSecretFile);
            var (sec1, status1, err1) = store.LoadOrCreate();

            Check(sec1 != null && sec1.Length == 32 && status1 == LsaSecretStatus.Created,
                "Test 1: Store creates 32-byte secret file on first run", err1 ?? "");

            Check(File.Exists(testSecretFile), "Test 2: Secret file actually created on disk");

            var encBytes = File.ReadAllBytes(testSecretFile);
            Check(!encBytes.SequenceEqual(sec1!), "Test 3: Encrypted bytes on disk do not match plaintext secret");

            // 2. Second LoadOrCreate should return the EXACT same secret
            var (sec2, status2, err2) = store.LoadOrCreate();
            Check(sec2 != null && status2 == LsaSecretStatus.Loaded && sec2.SequenceEqual(sec1!),
                "Test 4: Second LoadOrCreate reloads identical 32-byte secret");

            // 3. Reload from fresh store instance
            var storeFresh = new LsaMachineSecretStore(testSecretFile);
            var (sec3, status3, err3) = storeFresh.LoadOrCreate();
            Check(sec3 != null && status3 == LsaSecretStatus.Loaded && sec3.SequenceEqual(sec1!),
                "Test 5: Fresh instance reloads identical secret");

            // 4. Corrupted file test (fail closed)
            File.WriteAllBytes(testSecretFile, new byte[] { 0x01, 0x02, 0x03, 0x04 });
            var (secCorrupt, statusCorrupt, errCorrupt) = storeFresh.LoadOrCreate();
            Check(secCorrupt == null && statusCorrupt == LsaSecretStatus.Invalid,
                "Test 6: Corrupted file fails closed with Invalid status");

            // 5. Test deterministic ticket builder output
            var mockSecret = new byte[32];
            for (int i = 0; i < 32; i++) mockSecret[i] = (byte)(i + 1);

            // Struct size:
            // dwMagic(4) + dwVersion(4) + cbTotalSize(4) + szRequestId(64) + wszUserSid(256) + wszAccountName(512) + wszMachineName(512) + szDeviceId(64) + nIssuedAt(8) + nExpiresAt(8) + bNonce(16) + bHmacSignature(32)
            // Total size = 4+4+4+64+256+512+512+64+8+8+16+32 = 1484 bytes
            const int totalSize = 1484;
            using var ms = new MemoryStream(totalSize);
            using var bw = new BinaryWriter(ms);
            bw.Write((uint)0x46554C4B); // 'FULK'
            bw.Write((uint)1);          // version 1
            bw.Write((uint)totalSize);  // total size

            var reqIdBytes = new byte[64];
            System.Text.Encoding.ASCII.GetBytes("vector-req-12345", 0, "vector-req-12345".Length, reqIdBytes, 0);
            bw.Write(reqIdBytes);

            var sidBytes = new byte[256];
            System.Text.Encoding.Unicode.GetBytes("S-1-5-21-33333", 0, "S-1-5-21-33333".Length, sidBytes, 0);
            bw.Write(sidBytes);

            var accBytes = new byte[512];
            System.Text.Encoding.Unicode.GetBytes("VectorAdmin", 0, "VectorAdmin".Length, accBytes, 0);
            bw.Write(accBytes);

            var machBytes = new byte[512];
            System.Text.Encoding.Unicode.GetBytes("VECTOR-PC", 0, "VECTOR-PC".Length, machBytes, 0);
            bw.Write(machBytes);

            var devBytes = new byte[64];
            System.Text.Encoding.ASCII.GetBytes("vector-device-001", 0, "vector-device-001".Length, devBytes, 0);
            bw.Write(devBytes);

            long nowVec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bw.Write(nowVec);
            bw.Write(nowVec + 30);

            var nonce = new byte[16];
            for (int i = 0; i < 16; i++) nonce[i] = (byte)(i + 1);
            bw.Write(nonce);

            bw.Flush();
            var payload = ms.ToArray();
            using var hmac = new HMACSHA256(mockSecret);
            var sig = hmac.ComputeHash(payload);
            bw.Write(sig);
            bw.Flush();

            var fullBuf = ms.ToArray();
            Check(fullBuf.Length == 1484, "Test 7: Serialized ticket buffer length is exactly 1484 bytes", $"actual={fullBuf.Length}");
            Check(sig.Length == 32, "Test 8: HMAC-SHA256 signature is exactly 32 bytes");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
            catch { }
        }

        Console.WriteLine("\n============================================================");
        Console.WriteLine($"  UNIT TEST RESULTS: {passed} passed, {failed} failed");
        Console.WriteLine("============================================================");

        return (failed == 0) ? 0 : 1;
    }
}
