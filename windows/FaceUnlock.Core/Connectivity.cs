using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace FaceUnlock.Core;

public enum InternetState { Unknown, Offline, Online }
public enum BluetoothState { Unavailable, Disabled, Enabled, AccessDenied, Error }

public sealed record BluetoothRadioStatus(
    BluetoothState State,
    bool AutoEnableAttempted = false,
    string? Message = null,
    long StateVersion = 0);

public interface IInternetMonitor
{
    InternetState Current { get; }
    event EventHandler<InternetState>? StateChanged;
}

public sealed class WindowsInternetMonitor : IInternetMonitor, IDisposable
{
    private InternetState _current;
    public InternetState Current => _current;
    public event EventHandler<InternetState>? StateChanged;

    public WindowsInternetMonitor()
    {
        _current = ReadState();
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
    }

    private void OnNetworkStatusChanged(object sender)
    {
        var next = ReadState();
        if (next == _current) return;
        _current = next;
        StateChanged?.Invoke(this, next);
    }

    private static InternetState ReadState()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            return profile?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess
                ? InternetState.Online
                : InternetState.Offline;
        }
        catch { return InternetState.Unknown; }
    }

    public void Dispose() => NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
}

public interface IBluetoothRadioManager
{
    Task<BluetoothRadioStatus> GetStateAsync(CancellationToken ct = default);
    Task<BluetoothRadioStatus> SetEnabledAsync(bool enabled, CancellationToken ct = default);
    Task<BluetoothRadioStatus> EnsureEnabledAsync(CancellationToken ct = default);
}

public sealed class WindowsBluetoothRadioManager : IBluetoothRadioManager
{
    private readonly SemaphoreSlim _accessSync = new(1, 1);
    private readonly object _stateSync = new();
    private Radio? _radio;
    private RadioState? _expectedFaceUnlockState;
    private DateTimeOffset _expectedStateDeadline;
    private long _externalStateVersion;

    public Task<BluetoothRadioStatus> GetStateAsync(CancellationToken ct = default) =>
        AccessRadioAsync(null, ct);

    public Task<BluetoothRadioStatus> SetEnabledAsync(bool enabled, CancellationToken ct = default) =>
        AccessRadioAsync(enabled, ct);

    public Task<BluetoothRadioStatus> EnsureEnabledAsync(CancellationToken ct = default) =>
        AccessRadioAsync(true, ct);

    private async Task<BluetoothRadioStatus> AccessRadioAsync(bool? enabled, CancellationToken ct)
    {
        await _accessSync.WaitAsync(ct);
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
                return new(BluetoothState.AccessDenied, false, $"Bluetooth radio access: {access}");

            var radios = await Radio.GetRadiosAsync();
            var radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (radio is null)
                return new(BluetoothState.Unavailable, false, "No Bluetooth radio was detected");
            TrackRadio(radio);

            var current = radio.State == RadioState.On ? BluetoothState.Enabled : BluetoothState.Disabled;
            if (!enabled.HasValue || (enabled.Value && current == BluetoothState.Enabled)
                || (!enabled.Value && current == BluetoothState.Disabled))
                return new(current, StateVersion: ReadStateVersion());

            ct.ThrowIfCancellationRequested();
            var target = enabled.Value ? RadioState.On : RadioState.Off;
            lock (_stateSync)
            {
                _expectedFaceUnlockState = target;
                _expectedStateDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
            }
            var setResult = await radio.SetStateAsync(target);
            var state = radio.State == RadioState.On ? BluetoothState.Enabled : BluetoothState.Disabled;
            var succeeded = setResult == RadioAccessStatus.Allowed && radio.State == target;
            if (!succeeded)
            {
                lock (_stateSync) _expectedFaceUnlockState = null;
            }
            if (enabled.Value)
            {
                return succeeded
                    ? new(BluetoothState.Enabled, true, "Bluetooth enabled automatically", ReadStateVersion())
                    : new(state, true, $"Windows refused Bluetooth auto-enable: {setResult}", ReadStateVersion());
            }
            return succeeded
                ? new(BluetoothState.Disabled, false, "Bluetooth restored to off", ReadStateVersion())
                : new(state, false, $"Windows refused Bluetooth disable: {setResult}", ReadStateVersion());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(BluetoothState.Error, false, ex.Message); }
        finally { _accessSync.Release(); }
    }

    private void TrackRadio(Radio radio)
    {
        lock (_stateSync)
        {
            if (ReferenceEquals(_radio, radio))
                return;
            if (_radio != null)
                _radio.StateChanged -= OnRadioStateChanged;
            _radio = radio;
            _radio.StateChanged += OnRadioStateChanged;
        }
    }

    private void OnRadioStateChanged(Radio sender, object args)
    {
        lock (_stateSync)
        {
            if (_expectedFaceUnlockState.HasValue
                && sender.State == _expectedFaceUnlockState.Value
                && DateTimeOffset.UtcNow <= _expectedStateDeadline)
            {
                _expectedFaceUnlockState = null;
                return;
            }
            _expectedFaceUnlockState = null;
            _externalStateVersion++;
        }
    }

    private long ReadStateVersion()
    {
        lock (_stateSync) return _externalStateVersion;
    }
}

public enum BleWaitState { WaitingConnectivity, Scanning, Resting }
public enum BleWaitOutcome { ResponseReceived, InternetRestored }
public sealed record BleWaitResult<T>(BleWaitOutcome Outcome, T? Response, long ScanAttempts);

/// <summary>Runs one sequential BLE scan loop with no overall timeout.</summary>
public sealed class BleConnectivityWaitLoop
{
    private readonly IBluetoothRadioManager _radioManager;
    private readonly IInternetMonitor _internetMonitor;
    private readonly TimeSpan _restInterval;

    public BleConnectivityWaitLoop(IBluetoothRadioManager radioManager, IInternetMonitor internetMonitor, TimeSpan? restInterval = null)
    {
        _radioManager = radioManager;
        _internetMonitor = internetMonitor;
        _restInterval = restInterval ?? TimeSpan.FromSeconds(2.5);
    }

    public async Task<BleWaitResult<T>> WaitAsync<T>(Func<CancellationToken, Task<T?>> scanOnceAsync,
        bool switchToOnlineWhenInternetRestored, Action<BleWaitState, long, BluetoothRadioStatus?>? onState,
        CancellationToken ct = default) where T : class
    {
        long attempts = 0;
        using var internetReturned = new CancellationTokenSource();
        EventHandler<InternetState>? handler = (_, state) =>
        {
            if (switchToOnlineWhenInternetRestored && state == InternetState.Online) internetReturned.Cancel();
        };
        _internetMonitor.StateChanged += handler;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (switchToOnlineWhenInternetRestored && _internetMonitor.Current == InternetState.Online)
                    return new(BleWaitOutcome.InternetRestored, null, attempts);
                var radio = await _radioManager.EnsureEnabledAsync(ct);
                if (radio.State != BluetoothState.Enabled)
                {
                    onState?.Invoke(BleWaitState.WaitingConnectivity, attempts, radio);
                    await RestAsync(ct, internetReturned.Token);
                    if (internetReturned.IsCancellationRequested) return new(BleWaitOutcome.InternetRestored, null, attempts);
                    continue;
                }
                attempts++;
                onState?.Invoke(BleWaitState.Scanning, attempts, radio);
                using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct, internetReturned.Token);
                try
                {
                    var response = await scanOnceAsync(scanCts.Token);
                    if (response is not null) return new(BleWaitOutcome.ResponseReceived, response, attempts);
                }
                catch (OperationCanceledException) when (internetReturned.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    return new(BleWaitOutcome.InternetRestored, null, attempts);
                }
                onState?.Invoke(BleWaitState.Resting, attempts, radio);
                await RestAsync(ct, internetReturned.Token);
                if (internetReturned.IsCancellationRequested) return new(BleWaitOutcome.InternetRestored, null, attempts);
            }
        }
        finally { _internetMonitor.StateChanged -= handler; }
    }

    private async Task RestAsync(CancellationToken ct, CancellationToken internetCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, internetCt);
        try { await Task.Delay(_restInterval, linked.Token); }
        catch (OperationCanceledException) when (internetCt.IsCancellationRequested && !ct.IsCancellationRequested) { }
    }
}

public static class RequestIdentity
{
    public static string From(LocalAuthRequest request) => string.Join("|",
        request.request_id,
        request.user_sid ?? "",
        request.session_id?.ToString() ?? "",
        request.client_type ?? "",
        request.pc_id ?? "",
        request.process_id?.ToString() ?? "");
}
