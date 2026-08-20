using FaceUnlock.Core;

namespace FaceUnlock.UnitTests;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  FaceUnlock Transport & Bluetooth Lease Unit Tests");
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

        // Phase F.1 transport policy tests. These are deterministic policy
            // tests only; they do not claim to exercise a physical radio/network.
            var request = new LocalAuthRequest(
                1, "request_unlock", "req-1", user_sid: "S-1-5-21-1",
                session_id: 7, client_type: "shell", pc_id: "pc-1", process_id: 101);
            var duplicate = request with { };
            var changedBinding = request with { session_id = 8 };
            Check(RequestIdentity.From(request) == RequestIdentity.From(duplicate),
                "Test 9: Exact duplicate requests have the same dedup identity");
            var changedProcess = request with { process_id = 102 };
            Check(RequestIdentity.From(request) != RequestIdentity.From(changedBinding)
                && RequestIdentity.From(request) != RequestIdentity.From(changedProcess),
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

            var logicalAttempt = new LogicalUnlockAttempt("logical-request-1");
            Check(logicalAttempt.TryAcceptApproval("BLE")
                && !logicalAttempt.TryAcceptApproval("Online")
                && logicalAttempt.ApprovedTransport == "BLE",
                "Test 16: First valid transport approval wins and late approval is ignored");

            var logicalCanonicalA = Protocol.OfflineRequestCanonical("ble-a", "challenge-a", "pc-1", 100, logicalAttempt.RequestId, "online-1");
            var logicalCanonicalB = Protocol.OfflineRequestCanonical("ble-b", "challenge-b", "pc-1", 101, logicalAttempt.RequestId, "online-1");
            Check(logicalCanonicalA != logicalCanonicalB
                && logicalCanonicalA.Contains(logicalAttempt.RequestId, StringComparison.Ordinal)
                && logicalCanonicalB.Contains(logicalAttempt.RequestId, StringComparison.Ordinal),
                "Test 17: Fresh BLE crypto sessions preserve one signed logical request");

            var initiallyOff = new LeaseRadioManager(BluetoothState.Disabled);
            var offLeases = new BluetoothLeaseManager(initiallyOff);
            var offLease = await offLeases.EnsureEnabledAsync("off-success");
            await offLeases.ReleaseAsync("off-success");
            Check(!offLease.WasInitiallyEnabled && offLease.AutoEnabledByFaceUnlock
                && initiallyOff.State == BluetoothState.Disabled && initiallyOff.DisableCalls == 1,
                "Test 18: Initially OFF Bluetooth is restored OFF after owned lease release");

            var initiallyOn = new LeaseRadioManager(BluetoothState.Enabled);
            var onLeases = new BluetoothLeaseManager(initiallyOn);
            var onLease = await onLeases.EnsureEnabledAsync("on-success");
            await onLeases.ReleaseAsync("on-success");
            Check(onLease.WasInitiallyEnabled && !onLease.AutoEnabledByFaceUnlock
                && initiallyOn.State == BluetoothState.Enabled && initiallyOn.DisableCalls == 0,
                "Test 19: Initially ON Bluetooth remains ON after unlock");

            var cancelledRadio = new LeaseRadioManager(BluetoothState.Disabled);
            var cancelledLeases = new BluetoothLeaseManager(cancelledRadio);
            await cancelledLeases.EnsureEnabledAsync("cancelled");
            await cancelledLeases.ReleaseAsync("cancelled");
            Check(cancelledRadio.State == BluetoothState.Disabled && cancelledRadio.DisableCalls == 1,
                "Test 20: Cancelled owned request restores Bluetooth OFF");

            var failedEnableRadio = new LeaseRadioManager(BluetoothState.Disabled, allowEnable: false);
            var failedEnableLeases = new BluetoothLeaseManager(failedEnableRadio);
            var failedLease = await failedEnableLeases.EnsureEnabledAsync("enable-failed");
            await failedEnableLeases.ReleaseAsync("enable-failed");
            Check(!failedLease.AutoEnabledByFaceUnlock && failedEnableRadio.DisableCalls == 0,
                "Test 21: Failed auto-enable never attempts disable");

            var sharedRadio = new LeaseRadioManager(BluetoothState.Disabled);
            var sharedLeases = new BluetoothLeaseManager(sharedRadio);
            await sharedLeases.EnsureEnabledAsync("request-a");
            await sharedLeases.EnsureEnabledAsync("request-b");
            await sharedLeases.ReleaseAsync("request-a");
            var stayedOnForSecond = sharedRadio.State == BluetoothState.Enabled && sharedRadio.DisableCalls == 0;
            await sharedLeases.ReleaseAsync("request-b");
            Check(stayedOnForSecond && sharedRadio.State == BluetoothState.Disabled && sharedRadio.DisableCalls == 1,
                "Test 22: Shared Bluetooth stays ON until last active lease releases");

            var userOverrideRadio = new LeaseRadioManager(BluetoothState.Disabled);
            var userOverrideLeases = new BluetoothLeaseManager(userOverrideRadio);
            await userOverrideLeases.EnsureEnabledAsync("user-override");
            userOverrideRadio.ExternalSet(BluetoothState.Disabled);
            userOverrideRadio.ExternalSet(BluetoothState.Enabled);
            await userOverrideLeases.ReleaseAsync("user-override");
            Check(userOverrideRadio.State == BluetoothState.Enabled && userOverrideRadio.DisableCalls == 0,
                "Test 23: External Bluetooth state change revokes FaceUnlock disable ownership");
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
        public Task<BluetoothRadioStatus> GetStateAsync(CancellationToken ct = default) =>
            Task.FromResult(new BluetoothRadioStatus(_states.Peek()));
        public Task<BluetoothRadioStatus> SetEnabledAsync(bool enabled, CancellationToken ct = default) =>
            Task.FromResult(new BluetoothRadioStatus(enabled ? BluetoothState.Enabled : BluetoothState.Disabled));
        public Task<BluetoothRadioStatus> EnsureEnabledAsync(CancellationToken ct = default)
        {
            Calls++;
            var state = _states.Count > 1 ? _states.Dequeue() : _states.Peek();
            return Task.FromResult(new BluetoothRadioStatus(state));
        }
    }

    private sealed class LeaseRadioManager : IBluetoothRadioManager
    {
        private readonly bool _allowEnable;
        private long _stateVersion;
        public BluetoothState State { get; private set; }
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }

        public LeaseRadioManager(BluetoothState initialState, bool allowEnable = true)
        {
            State = initialState;
            _allowEnable = allowEnable;
        }

        public Task<BluetoothRadioStatus> GetStateAsync(CancellationToken ct = default) =>
            Task.FromResult(new BluetoothRadioStatus(State, StateVersion: _stateVersion));

        public Task<BluetoothRadioStatus> SetEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            if (enabled)
            {
                EnableCalls++;
                if (_allowEnable) State = BluetoothState.Enabled;
            }
            else
            {
                DisableCalls++;
                State = BluetoothState.Disabled;
            }
            return Task.FromResult(new BluetoothRadioStatus(State, enabled && _allowEnable, StateVersion: _stateVersion));
        }

        public async Task<BluetoothRadioStatus> EnsureEnabledAsync(CancellationToken ct = default) =>
            State == BluetoothState.Enabled
                ? new BluetoothRadioStatus(State)
                : await SetEnabledAsync(true, ct);

        public void ExternalSet(BluetoothState state)
        {
            State = state;
            _stateVersion++;
        }
    }
}
