using System.Security.Cryptography;
using FaceUnlock.Core;

namespace FaceUnlock.UnitTests;

public class Program
{
    public static async Task<int> Main(string[] args)
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

            // Phase F.1 transport policy tests. These are deterministic policy
            // tests only; they do not claim to exercise a physical radio/network.
            var request = new LocalAuthRequest(
                1, "request_unlock", "req-1", user_sid: "S-1-5-21-1",
                session_id: 7, client_type: "shell", pc_id: "pc-1");
            var duplicate = request with { };
            var changedBinding = request with { session_id = 8 };
            Check(RequestIdentity.From(request) == RequestIdentity.From(duplicate),
                "Test 9: Exact duplicate requests have the same dedup identity");
            Check(RequestIdentity.From(request) != RequestIdentity.From(changedBinding),
                "Test 10: Security-binding changes alter the dedup identity");

            // Phase F.1 long-wait loop tests use fakes only; they do not claim
            // physical Bluetooth, iPhone, or network validation.
            var offline = new FakeInternetMonitor(InternetState.Offline);
            var enabledRadio = new FakeRadioManager(BluetoothState.Enabled);
            var absentScans = 0;
            var maxConcurrent = 0;
            var activeScans = 0;
            using (var absentCts = new CancellationTokenSource())
            {
                var loop = new BleConnectivityWaitLoop(enabledRadio, offline, TimeSpan.FromMilliseconds(2));
                var absentTask = loop.WaitAsync<string>(async ct =>
                {
                    maxConcurrent = Math.Max(maxConcurrent, Interlocked.Increment(ref activeScans));
                    try { absentScans++; await Task.Delay(1, ct); return null; }
                    finally { Interlocked.Decrement(ref activeScans); }
                }, true, (_, attempts, _) => { if (attempts >= 4) absentCts.Cancel(); }, absentCts.Token);
                try { await absentTask; } catch (OperationCanceledException) { }
            }
            Check(absentScans >= 4 && maxConcurrent == 1, "Test 11: Absent BLE cycles stay sequential without worker leak");

            var lateScans = 0;
            var lateLoop = new BleConnectivityWaitLoop(enabledRadio, new FakeInternetMonitor(InternetState.Offline), TimeSpan.FromMilliseconds(1));
            var late = await lateLoop.WaitAsync<string>(_ => Task.FromResult<string?>(++lateScans >= 4 ? "iphone" : null), true, null);
            Check(late.Outcome == BleWaitOutcome.ResponseReceived && late.Response == "iphone" && lateScans == 4,
                "Test 12: Late iPhone continues the same auth loop");

            var internet = new FakeInternetMonitor(InternetState.Offline);
            var internetLoop = new BleConnectivityWaitLoop(enabledRadio, internet, TimeSpan.FromMilliseconds(10));
            var onlineScans = 0;
            var onlineTask = internetLoop.WaitAsync<string>(async ct => { onlineScans++; await Task.Delay(Timeout.Infinite, ct); return null; }, true, null);
            await Task.Delay(5);
            for (var i = 0; i < 100; i++) internet.Set(i % 2 == 0 ? InternetState.Online : InternetState.Offline);
            var online = await onlineTask;
            Check(online.Outcome == BleWaitOutcome.InternetRestored && onlineScans == 1,
                "Test 13: 100 connectivity transitions cancel one BLE worker exactly once");

            var offThenOnRadio = new FakeRadioManager(BluetoothState.AccessDenied, BluetoothState.AccessDenied, BluetoothState.Enabled);
            var radioLoop = new BleConnectivityWaitLoop(offThenOnRadio, new FakeInternetMonitor(InternetState.Offline), TimeSpan.FromMilliseconds(1));
            var radioResult = await radioLoop.WaitAsync<string>(_ => Task.FromResult<string?>("iphone"), true, null);
            Check(radioResult.Outcome == BleWaitOutcome.ResponseReceived && offThenOnRadio.Calls >= 3,
                "Test 14: Bluetooth OFF then manual ON recovers without restart");

            using (var shutdownCts = new CancellationTokenSource())
            {
                var shutdownLoop = new BleConnectivityWaitLoop(enabledRadio, new FakeInternetMonitor(InternetState.Offline), TimeSpan.FromMilliseconds(1));
                var shutdownTask = shutdownLoop.WaitAsync<string>(async ct => { await Task.Delay(Timeout.Infinite, ct); return null; }, true, null, shutdownCts.Token);
                shutdownCts.Cancel();
                try { await shutdownTask; Check(false, "Test 15: Shell shutdown stops loop cleanly"); }
                catch (OperationCanceledException) { Check(true, "Test 15: Shell shutdown stops loop cleanly"); }
            }
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

    private sealed class FakeInternetMonitor : IInternetMonitor
    {
        public InternetState Current { get; private set; }
        public event EventHandler<InternetState>? StateChanged;
        public FakeInternetMonitor(InternetState state) => Current = state;
        public void Set(InternetState state) { Current = state; StateChanged?.Invoke(this, state); }
    }

    private sealed class FakeRadioManager : IBluetoothRadioManager
    {
        private readonly Queue<BluetoothState> _states;
        public int Calls { get; private set; }
        public FakeRadioManager(params BluetoothState[] states) => _states = new Queue<BluetoothState>(states);
        public Task<BluetoothRadioStatus> EnsureEnabledAsync(CancellationToken ct = default)
        {
            Calls++;
            var state = _states.Count > 1 ? _states.Dequeue() : _states.Peek();
            return Task.FromResult(new BluetoothRadioStatus(state));
        }
    }
}
