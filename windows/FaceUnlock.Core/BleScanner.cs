using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;

namespace FaceUnlock.Core;

public sealed class BleScanner
{
    private static readonly SemaphoreSlim PhysicalBleTransaction = new(1, 1);
    private static readonly object DiagnosticSync = new();
    private const long MaxDiagnosticBytes = 512 * 1024;
    private readonly IBluetoothRadioManager _radioManager;
    public BleScanner(IBluetoothRadioManager? radioManager = null) => _radioManager = radioManager ?? new WindowsBluetoothRadioManager();

    public static readonly Guid ServiceUuid = Guid.Parse("7A6AF110-8D20-4C5F-BB31-6CECF28F0110");
    public static readonly Guid RequestUuid = Guid.Parse("7A6AF111-8D20-4C5F-BB31-6CECF28F0110");
    public static readonly Guid ResponseUuid = Guid.Parse("7A6AF112-8D20-4C5F-BB31-6CECF28F0110");
    public static readonly Guid DeviceUuid = Guid.Parse("7A6AF113-8D20-4C5F-BB31-6CECF28F0110");

    public Task<OfflineBleResponse?> DiscoverAndApproveAsync(OfflineUnlockPayload payload, TimeSpan timeout, CancellationToken ct = default) => DiscoverAndApproveAsync(payload, null, timeout, ct);

    public async Task<OfflineBleResponse?> DiscoverAndApproveAsync(OfflineUnlockPayload payload, string? expectedDeviceId = null, TimeSpan timeout = default, CancellationToken ct = default)
    {
        if (timeout == default) timeout = TimeSpan.FromSeconds(8);
        await PhysicalBleTransaction.WaitAsync(ct);
        try { return await DiscoverAndApproveExclusiveAsync(payload, expectedDeviceId, timeout, ct); }
        finally { PhysicalBleTransaction.Release(); }
    }

    private async Task<OfflineBleResponse?> DiscoverAndApproveExclusiveAsync(OfflineUnlockPayload payload, string? expectedDeviceId, TimeSpan timeout, CancellationToken ct)
    {
        var radio = await _radioManager.EnsureEnabledAsync(ct);
        BleDiag(payload, $"RADIO state={radio.State} auto_enabled={radio.AutoEnableAttempted}");
        if (radio.State != BluetoothState.Enabled) throw new InvalidOperationException(radio.Message ?? $"Bluetooth is {radio.State}");
        if (radio.AutoEnableAttempted)
        {
            BleDiag(payload, "RADIO_STABILIZE begin delay_ms=1000");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            BleDiag(payload, "RADIO_STABILIZE complete");
        }

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
                if (seenAddresses.Add(e.BluetoothAddress))
                {
                    addressQueue.Enqueue(e.BluetoothAddress);
                    BleDiag(payload, $"ADVERTISEMENT_FOUND candidate={seenAddresses.Count}");
                    signal.Release();
                }
            }
        };
        BleDiag(payload, $"SCAN_START budget_ms={(int)timeout.TotalMilliseconds}");
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
                    var result = await TryConnectAndApproveAsync(address, payload, expectedDeviceId, remaining, ct);
                    if (result != null) { BleDiag(payload, "RESPONSE_COMPLETE"); return result; }
                }
            }
            BleDiag(payload, $"SCAN_END result=no_response candidates={seenAddresses.Count}");
            return null;
        }
        finally { watcher.Stop(); }
    }

    private static async Task<OfflineBleResponse?> TryConnectAndApproveAsync(ulong address, OfflineUnlockPayload payload, string? expectedDeviceId, TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero) return null;
        var operationDeadline = DateTime.UtcNow + timeout;
        try
        {
            BleDiag(payload, "GATT_CONNECT begin");
            using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device is null || DateTime.UtcNow >= operationDeadline) { BleDiag(payload, "GATT_CONNECT fail"); return null; }
            BleDiag(payload, "GATT_CONNECT ok");
            var services = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0 || DateTime.UtcNow >= operationDeadline) { BleDiag(payload, $"SERVICE_FOUND fail status={services.Status} count={services.Services.Count}"); return null; }
            using var service = services.Services[0];
            BleDiag(payload, "SERVICE_FOUND ok");

            if (!string.IsNullOrWhiteSpace(expectedDeviceId))
            {
                var devChars = await service.GetCharacteristicsForUuidAsync(DeviceUuid, BluetoothCacheMode.Uncached);
                if (devChars.Status != GattCommunicationStatus.Success || devChars.Characteristics.Count == 0 || DateTime.UtcNow >= operationDeadline) { BleDiag(payload, $"DEVICE_ID fail stage=characteristic status={devChars.Status}"); return null; }
                var read = await devChars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
                if (read.Status != GattCommunicationStatus.Success || read.Value is null) { BleDiag(payload, $"DEVICE_ID fail stage=read status={read.Status}"); return null; }
                var reader = Windows.Storage.Streams.DataReader.FromBuffer(read.Value);
                var idBytes = new byte[read.Value.Length];
                reader.ReadBytes(idBytes);
                if (!string.Equals(Encoding.UTF8.GetString(idBytes).Trim(), expectedDeviceId, StringComparison.Ordinal)) { BleDiag(payload, "DEVICE_ID mismatch"); return null; }
                BleDiag(payload, "DEVICE_ID ok");
            }

            if (DateTime.UtcNow >= operationDeadline) return null;
            var reqResult = await service.GetCharacteristicsForUuidAsync(RequestUuid, BluetoothCacheMode.Uncached);
            var resResult = await service.GetCharacteristicsForUuidAsync(ResponseUuid, BluetoothCacheMode.Uncached);
            if (reqResult.Status != GattCommunicationStatus.Success || resResult.Status != GattCommunicationStatus.Success || reqResult.Characteristics.Count == 0 || resResult.Characteristics.Count == 0 || DateTime.UtcNow >= operationDeadline) { BleDiag(payload, $"CHARACTERISTICS fail request={reqResult.Status} response={resResult.Status}"); return null; }
            BleDiag(payload, "CHARACTERISTICS ok");

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
                    if (status == BleAssemblyStatus.Complete && complete != null) { BleDiag(payload, $"RESPONSE_RECEIVED bytes={complete.Length}"); responseTcs.TrySetResult(complete); }
                    else if (status == BleAssemblyStatus.Invalid) { BleDiag(payload, $"RESPONSE_INVALID error={Sanitize(error)}"); responseTcs.TrySetException(new InvalidDataException(error ?? "Invalid BLE response frame.")); }
                }
                catch (Exception ex) { responseTcs.TrySetException(ex); }
            };
            responseChar.ValueChanged += handler;
            try
            {
                var cccdStatus = await responseChar.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (cccdStatus != GattCommunicationStatus.Success || DateTime.UtcNow >= operationDeadline) { BleDiag(payload, $"NOTIFY_SUBSCRIBED fail status={cccdStatus}"); return null; }
                BleDiag(payload, "NOTIFY_SUBSCRIBED ok");
                var frames = BleFrameCodec.Encode(JsonSerializer.SerializeToUtf8Bytes(payload), BleFrameKind.Request, BleFrameCodec.MinimumFrameSize);
                var frameNo = 0;
                foreach (var frame in frames)
                {
                    ct.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow >= operationDeadline) return null;
                    using var writer = new Windows.Storage.Streams.DataWriter();
                    writer.WriteBytes(frame);
                    var write = await requestChar.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse);
                    frameNo++;
                    if (write.Status != GattCommunicationStatus.Success) { BleDiag(payload, $"REQUEST_WRITE fail frame={frameNo} status={write.Status}"); return null; }
                }
                BleDiag(payload, $"REQUEST_WRITTEN frames={frameNo}");
                var remaining = operationDeadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return null;
                var done = await Task.WhenAny(responseTcs.Task, Task.Delay(remaining, ct));
                if (done != responseTcs.Task) { BleDiag(payload, "RESPONSE_TIMEOUT"); return null; }
                return JsonSerializer.Deserialize<OfflineBleResponse>(await responseTcs.Task, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            finally { responseChar.ValueChanged -= handler; }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { BleDiag(payload, $"GATT_EXCEPTION type={ex.GetType().Name} message={Sanitize(ex.Message)}"); return null; }
    }

    private static void BleDiag(OfflineUnlockPayload payload, string message)
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(root)) return;
            var dir = Path.Combine(root, "FaceUnlock", "logs");
            var path = Path.Combine(dir, "ble.log");
            lock (DiagnosticSync)
            {
                Directory.CreateDirectory(dir);
                if (File.Exists(path) && new FileInfo(path).Length >= MaxDiagnosticBytes) File.WriteAllText(path, string.Empty);
                File.AppendAllText(path, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ}] request={Short(payload.logical_request_id)} session={Short(payload.session_id)} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }

    private static string Short(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value[..Math.Min(8, value.Length)];
    private static string Sanitize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Replace('\r', ' ').Replace('\n', ' ');
}
