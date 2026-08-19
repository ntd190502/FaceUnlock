using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;

namespace FaceUnlock.Core;

public sealed class BleScanner
{
    public static readonly Guid ServiceUuid = Guid.Parse("7A6AF110-8D20-4C5F-BB31-6CECF28F0110");
    public static readonly Guid RequestUuid = Guid.Parse("7A6AF111-8D20-4C5F-BB31-6CECF28F0110");
    public static readonly Guid ResponseUuid = Guid.Parse("7A6AF112-8D20-4C5F-BB31-6CECF28F0110");
    public static readonly Guid DeviceUuid = Guid.Parse("7A6AF113-8D20-4C5F-BB31-6CECF28F0110");

    public Task<OfflineBleResponse?> DiscoverAndApproveAsync(
        OfflineUnlockPayload payload,
        TimeSpan timeout,
        CancellationToken ct = default) =>
        DiscoverAndApproveAsync(payload, null, timeout, ct);

    public async Task<OfflineBleResponse?> DiscoverAndApproveAsync(
        OfflineUnlockPayload payload,
        string? expectedDeviceId = null,
        TimeSpan timeout = default,
        CancellationToken ct = default)
    {
        if (timeout == default) timeout = TimeSpan.FromSeconds(8);
        var deadline = DateTime.UtcNow + timeout;
        var addressQueue = new System.Collections.Concurrent.ConcurrentQueue<ulong>();
        var seenAddresses = new HashSet<ulong>();
        using var signal = new SemaphoreSlim(0);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceUuid);
        watcher.Received += (_, e) =>
        {
            lock (seenAddresses)
            {
                if (seenAddresses.Add(e.BluetoothAddress))
                {
                    addressQueue.Enqueue(e.BluetoothAddress);
                    signal.Release();
                }
            }
        };
        watcher.Start();

        try
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                var signaled = await signal.WaitAsync(remaining, ct);
                if (!signaled) break;

                while (addressQueue.TryDequeue(out var address))
                {
                    if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline) break;

                    var deviceTimeout = deadline - DateTime.UtcNow;
                    var result = await TryConnectAndApproveAsync(
                        address,
                        payload,
                        expectedDeviceId,
                        deviceTimeout,
                        ct
                    );
                    if (result != null) return result;
                }
            }

            return null;
        }
        finally
        {
            watcher.Stop();
        }
    }

    private static async Task<OfflineBleResponse?> TryConnectAndApproveAsync(
        ulong address,
        OfflineUnlockPayload payload,
        string? expectedDeviceId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero) return null;
        var operationDeadline = DateTime.UtcNow + timeout;

        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null) return null;

        var services = await device.GetGattServicesForUuidAsync(
            ServiceUuid,
            BluetoothCacheMode.Uncached
        );
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            return null;

        using var service = services.Services[0];

        // Fail closed when a target device is known. A missing/unreadable device
        // identity is not treated as a match.
        if (!string.IsNullOrWhiteSpace(expectedDeviceId))
        {
            var devChars = await service.GetCharacteristicsForUuidAsync(
                DeviceUuid,
                BluetoothCacheMode.Uncached
            );
            if (devChars.Status != GattCommunicationStatus.Success ||
                devChars.Characteristics.Count == 0)
                return null;

            var read = await devChars.Characteristics[0].ReadValueAsync(
                BluetoothCacheMode.Uncached
            );
            if (read.Status != GattCommunicationStatus.Success || read.Value is null)
                return null;

            var reader = Windows.Storage.Streams.DataReader.FromBuffer(read.Value);
            var idBytes = new byte[read.Value.Length];
            reader.ReadBytes(idBytes);
            var scannedDeviceId = Encoding.UTF8.GetString(idBytes).Trim();

            if (!string.Equals(
                    scannedDeviceId,
                    expectedDeviceId,
                    StringComparison.Ordinal))
                return null;
        }

        var reqResult = await service.GetCharacteristicsForUuidAsync(
            RequestUuid,
            BluetoothCacheMode.Uncached
        );
        var resResult = await service.GetCharacteristicsForUuidAsync(
            ResponseUuid,
            BluetoothCacheMode.Uncached
        );

        if (reqResult.Status != GattCommunicationStatus.Success ||
            resResult.Status != GattCommunicationStatus.Success ||
            reqResult.Characteristics.Count == 0 ||
            resResult.Characteristics.Count == 0)
            return null;

        var requestChar = reqResult.Characteristics[0];
        var responseChar = resResult.Characteristics[0];

        var responseTcs = new TaskCompletionSource<byte[]>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var responseAssembler = new BleFrameAssembler();

        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler = (_, e) =>
        {
            try
            {
                var reader = Windows.Storage.Streams.DataReader.FromBuffer(e.CharacteristicValue);
                var bytes = new byte[e.CharacteristicValue.Length];
                reader.ReadBytes(bytes);

                var status = responseAssembler.Ingest(
                    bytes,
                    BleFrameKind.Response,
                    out var complete,
                    out bool _,
                    out var error
                );

                if (status == BleAssemblyStatus.Complete && complete != null)
                    responseTcs.TrySetResult(complete);
                else if (status == BleAssemblyStatus.Invalid)
                    responseTcs.TrySetException(
                        new InvalidDataException(error ?? "Invalid BLE response frame.")
                    );
            }
            catch (Exception ex)
            {
                responseTcs.TrySetException(ex);
            }
        };

        responseChar.ValueChanged += handler;

        try
        {
            var cccdStatus =
                await responseChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify
                );
            if (cccdStatus != GattCommunicationStatus.Success) return null;

            var json = JsonSerializer.SerializeToUtf8Bytes(payload);
            var frames = BleFrameCodec.Encode(
                json,
                BleFrameKind.Request,
                BleFrameCodec.MinimumFrameSize
            );

            // Write each 20-byte frame with response. This deliberately favors
            // compatibility with the minimum ATT MTU over raw throughput.
            foreach (var frame in frames)
            {
                ct.ThrowIfCancellationRequested();

                using var writer = new Windows.Storage.Streams.DataWriter();
                writer.WriteBytes(frame);
                var write = await requestChar.WriteValueWithResultAsync(
                    writer.DetachBuffer(),
                    GattWriteOption.WriteWithResponse
                );
                if (write.Status != GattCommunicationStatus.Success)
                    return null;
            }

            var remaining = operationDeadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;

            var delayTask = Task.Delay(remaining, ct);
            var done = await Task.WhenAny(responseTcs.Task, delayTask);
            if (done != responseTcs.Task) return null;

            var rawResponse = await responseTcs.Task;
            return JsonSerializer.Deserialize<OfflineBleResponse>(
                rawResponse,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            responseChar.ValueChanged -= handler;
        }
    }
}
