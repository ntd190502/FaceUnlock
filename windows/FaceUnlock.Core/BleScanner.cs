using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;

namespace FaceUnlock.Core;

public sealed class BleScanner
{
    private readonly IBluetoothRadioManager _radioManager;
    public BleScanner(IBluetoothRadioManager? radioManager = null) => _radioManager = radioManager ?? new WindowsBluetoothRadioManager();

    public static readonly Guid ServiceUuid = Guid.Parse("7A6AF110-8D20-4C5F-BB31-6CECF28F0110");
    public static readonly Guid RequestUuid = Guid.Parse("7A6AF111-8D20-4C5F-BB31-6CECF28F0110");
    public static readonly Guid ResponseUuid = Guid.Parse("7A6AF112-8D20-4C5F-BB31-6CECF28F0110");
    public static readonly Guid DeviceUuid = Guid.Parse("7A6AF113-8D20-4C5F-BB31-6CECF28F0110");

    public Task<OfflineBleResponse?> DiscoverAndApproveAsync(OfflineUnlockPayload payload, TimeSpan timeout, CancellationToken ct = default) =>
        DiscoverAndApproveAsync(payload, null, timeout, ct);

    public async Task<OfflineBleResponse?> DiscoverAndApproveAsync(OfflineUnlockPayload payload, string? expectedDeviceId = null, TimeSpan timeout = default, CancellationToken ct = default)
    {
        if (timeout == default) timeout = TimeSpan.FromSeconds(8);
        var radio = await _radioManager.EnsureEnabledAsync(ct);
        if (radio.State != BluetoothState.Enabled) throw new InvalidOperationException(radio.Message ?? $"Bluetooth is {radio.State}");

        var deadline = DateTime.UtcNow + timeout;
        var addressQueue = new System.Collections.Concurrent.ConcurrentQueue<ulong>();
        var seenAddresses = new HashSet<ulong>();
        using var signal = new SemaphoreSlim(0);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceUuid);
        watcher.Received += (_, e) =>
        {
            lock (seenAddresses)
            {
                if (seenAddresses.Add(e.BluetoothAddress)) { addressQueue.Enqueue(e.BluetoothAddress); signal.Release(); }
            }
        };
        watcher.Start();

        try
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                if (!await signal.WaitAsync(remaining, ct)) break;

                while (addressQueue.TryDequeue(out var address))
                {
                    if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline) break;
                    remaining = deadline - DateTime.UtcNow;
                    // A random nearby BLE device must not consume the whole scan
                    // window. Give each candidate a bounded slice and continue.
                    var deviceTimeout = remaining < TimeSpan.FromSeconds(3) ? remaining : TimeSpan.FromSeconds(3);
                    var result = await TryConnectAndApproveAsync(address, payload, expectedDeviceId, deviceTimeout, ct);
                    if (result != null) return result;
                }
            }
            return null;
        }
        finally { watcher.Stop(); }
    }

    private static async Task<OfflineBleResponse?> TryConnectAndApproveAsync(ulong address, OfflineUnlockPayload payload, string? expectedDeviceId, TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero) return null;
        var operationDeadline = DateTime.UtcNow + timeout;
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null || DateTime.UtcNow >= operationDeadline) return null;

        var services = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0 || DateTime.UtcNow >= operationDeadline) return null;
        using var service = services.Services[0];

        if (!string.IsNullOrWhiteSpace(expectedDeviceId))
        {
            var devChars = await service.GetCharacteristicsForUuidAsync(DeviceUuid, BluetoothCacheMode.Uncached);
            if (devChars.Status != GattCommunicationStatus.Success || devChars.Characteristics.Count == 0 || DateTime.UtcNow >= operationDeadline) return null;
            var read = await devChars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status != GattCommunicationStatus.Success || read.Value is null) return null;
            var reader = Windows.Storage.Streams.DataReader.FromBuffer(read.Value);
            var idBytes = new byte[read.Value.Length];
            reader.ReadBytes(idBytes);
            if (!string.Equals(Encoding.UTF8.GetString(idBytes).Trim(), expectedDeviceId, StringComparison.Ordinal)) return null;
        }

        if (DateTime.UtcNow >= operationDeadline) return null;
        var reqResult = await service.GetCharacteristicsForUuidAsync(RequestUuid, BluetoothCacheMode.Uncached);
        var resResult = await service.GetCharacteristicsForUuidAsync(ResponseUuid, BluetoothCacheMode.Uncached);
        if (reqResult.Status != GattCommunicationStatus.Success || resResult.Status != GattCommunicationStatus.Success ||
            reqResult.Characteristics.Count == 0 || resResult.Characteristics.Count == 0 || DateTime.UtcNow >= operationDeadline) return null;

        var requestChar = reqResult.Characteristics[0];
        var responseChar = resResult.Characteristics[0];
        var responseTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseAssembler = new BleFrameAssembler();
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler = (_, e) =>
        {
            try
            {
                var reader = Windows.Storage.Streams.DataReader.FromBuffer(e.CharacteristicValue);
                var bytes = new byte[e.CharacteristicValue.Length];
                reader.ReadBytes(bytes);
                var status = responseAssembler.Ingest(bytes, BleFrameKind.Response, out var complete, out bool _, out var error);
                if (status == BleAssemblyStatus.Complete && complete != null) responseTcs.TrySetResult(complete);
                else if (status == BleAssemblyStatus.Invalid) responseTcs.TrySetException(new InvalidDataException(error ?? "Invalid BLE response frame."));
            }
            catch (Exception ex) { responseTcs.TrySetException(ex); }
        };
        responseChar.ValueChanged += handler;

        try
        {
            var cccdStatus = await responseChar.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (cccdStatus != GattCommunicationStatus.Success || DateTime.UtcNow >= operationDeadline) return null;
            var frames = BleFrameCodec.Encode(JsonSerializer.SerializeToUtf8Bytes(payload), BleFrameKind.Request, BleFrameCodec.MinimumFrameSize);
            foreach (var frame in frames)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= operationDeadline) return null;
                using var writer = new Windows.Storage.Streams.DataWriter();
                writer.WriteBytes(frame);
                var write = await requestChar.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse);
                if (write.Status != GattCommunicationStatus.Success) return null;
            }

            var remaining = operationDeadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            var done = await Task.WhenAny(responseTcs.Task, Task.Delay(remaining, ct));
            if (done != responseTcs.Task) return null;
            return JsonSerializer.Deserialize<OfflineBleResponse>(await responseTcs.Task, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { responseChar.ValueChanged -= handler; }
    }
}
