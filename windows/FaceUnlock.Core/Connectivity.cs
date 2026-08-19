using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace FaceUnlock.Core;

public enum InternetState { Unknown, Offline, Online }
public enum BluetoothState { Unavailable, Disabled, Enabled, AccessDenied, Error }

public sealed record BluetoothRadioStatus(
    BluetoothState State,
    bool AutoEnableAttempted = false,
    string? Message = null);

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
    Task<BluetoothRadioStatus> EnsureEnabledAsync(CancellationToken ct = default);
}

public sealed class WindowsBluetoothRadioManager : IBluetoothRadioManager
{
    public async Task<BluetoothRadioStatus> EnsureEnabledAsync(CancellationToken ct = default)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
                return new(BluetoothState.AccessDenied, false, $"Bluetooth radio access: {access}");

            var radios = await Radio.GetRadiosAsync();
            var radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (radio is null)
                return new(BluetoothState.Unavailable, false, "No Bluetooth radio was detected");
            if (radio.State == RadioState.On)
                return new(BluetoothState.Enabled);

            ct.ThrowIfCancellationRequested();
            var setResult = await radio.SetStateAsync(RadioState.On);
            return setResult == RadioAccessStatus.Allowed && radio.State == RadioState.On
                ? new(BluetoothState.Enabled, true, "Bluetooth enabled automatically")
                : new(BluetoothState.Disabled, true, $"Windows refused Bluetooth auto-enable: {setResult}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(BluetoothState.Error, false, ex.Message); }
    }
}

public static class BleRetryPolicy
{
    public const int DefaultAttempts = 3;
    public static TimeSpan DelayForAttempt(int completedAttempt) =>
        completedAttempt switch
        {
            <= 0 => TimeSpan.Zero,
            1 => TimeSpan.FromMilliseconds(350),
            _ => TimeSpan.FromMilliseconds(900)
        };
}

public static class RequestIdentity
{
    public static string From(LocalAuthRequest request) => string.Join("|",
        request.request_id,
        request.user_sid ?? "",
        request.session_id?.ToString() ?? "",
        request.client_type ?? "",
        request.pc_id ?? "");
}
