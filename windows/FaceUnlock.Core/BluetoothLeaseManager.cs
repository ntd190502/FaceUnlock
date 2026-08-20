namespace FaceUnlock.Core;

public sealed record BluetoothLeaseStatus(
    BluetoothState State,
    bool WasInitiallyEnabled,
    bool AutoEnabledByFaceUnlock,
    string? Message = null);

/// <summary>
/// Owns Bluetooth state for FaceUnlock requests. If FaceUnlock turns Bluetooth
/// on, that ownership is persisted so a Service restart during authentication
/// does not forget that the radio should be restored OFF after the next cleanup.
/// </summary>
public sealed class BluetoothLeaseManager
{
    private readonly IBluetoothRadioManager _radio;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly HashSet<string> _activeRequests = new(StringComparer.Ordinal);
    private readonly string? _ownershipMarkerPath;
    private bool _radioOwnedByFaceUnlock;
    private bool _persistedOwnershipPending;
    private long _ownedStateVersion;

    public BluetoothLeaseManager(IBluetoothRadioManager radio, Action<string>? log = null, string? ownershipMarkerPath = null)
    {
        _radio = radio;
        _log = log ?? (_ => { });
        _ownershipMarkerPath = ownershipMarkerPath ?? DefaultOwnershipMarkerPath();
        _persistedOwnershipPending = ReadOwnershipMarker();
        if (_persistedOwnershipPending)
            _log("[BLUETOOTH] recovered persisted FaceUnlock radio ownership");
    }

    public int ActiveLeaseCount
    {
        get { lock (_activeRequests) return _activeRequests.Count; }
    }

    public bool RadioOwnedByFaceUnlock => _radioOwnedByFaceUnlock || _persistedOwnershipPending;

    public async Task<BluetoothLeaseStatus> EnsureEnabledAsync(string requestId, CancellationToken ct = default)
    {
        await _sync.WaitAsync(ct);
        try
        {
            if (ContainsRequest(requestId))
            {
                var leasedState = await _radio.GetStateAsync(ct);
                if (leasedState.State == BluetoothState.Enabled)
                {
                    if (_radioOwnedByFaceUnlock && leasedState.StateVersion != _ownedStateVersion)
                    {
                        ClearOwnership();
                        _log($"[BLUETOOTH] request_id={requestId} ownership=External reason=state_changed");
                    }
                    return new(leasedState.State, !RadioOwnedByFaceUnlock, RadioOwnedByFaceUnlock, leasedState.Message);
                }

                RemoveRequest(requestId);
                if (ActiveLeaseCount == 0)
                    ClearOwnership();
            }

            var current = await _radio.GetStateAsync(ct);
            var initiallyEnabled = current.State == BluetoothState.Enabled;
            if (initiallyEnabled)
            {
                // A persisted marker means a previous Service process enabled the
                // radio and died/restarted before it could restore the user's OFF
                // state. Adopt that lease instead of incorrectly calling it external.
                if (_persistedOwnershipPending)
                {
                    _radioOwnedByFaceUnlock = true;
                    _persistedOwnershipPending = false;
                    _ownedStateVersion = current.StateVersion;
                    _log($"[BLUETOOTH] request_id={requestId} initial_state=Enabled ownership=FaceUnlock recovered=true");
                }
                AddRequest(requestId);
                var owner = _radioOwnedByFaceUnlock ? "FaceUnlock" : "External";
                _log($"[BLUETOOTH] request_id={requestId} initial_state=Enabled ownership={owner}");
                return new(current.State, !_radioOwnedByFaceUnlock, _radioOwnedByFaceUnlock, current.Message);
            }

            // If the radio is already OFF, any stale persisted ownership has
            // fulfilled its purpose and must not affect this new request.
            if (_persistedOwnershipPending)
                ClearOwnership();

            _log($"[BLUETOOTH] request_id={requestId} initial_state={current.State}");
            if (current.State is not BluetoothState.Disabled)
                return new(current.State, false, false, current.Message);

            var enabled = await _radio.SetEnabledAsync(true, ct);
            if (enabled.State != BluetoothState.Enabled)
            {
                _log($"[BLUETOOTH] request_id={requestId} auto_enable=FAILED state={enabled.State}");
                return new(enabled.State, false, false, enabled.Message);
            }

            _radioOwnedByFaceUnlock = true;
            _ownedStateVersion = enabled.StateVersion;
            WriteOwnershipMarker();
            AddRequest(requestId);
            _log($"[BLUETOOTH] request_id={requestId} auto_enable=SUCCESS ownership=FaceUnlock persisted=true");
            return new(BluetoothState.Enabled, false, true, enabled.Message);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task ReleaseAsync(string requestId, CancellationToken ct = default)
    {
        await _sync.WaitAsync(ct);
        try
        {
            if (!RemoveRequest(requestId))
                return;

            var remaining = ActiveLeaseCount;
            _log($"[BLUETOOTH] request_id={requestId} release lease remaining={remaining}");
            if (remaining != 0 || !_radioOwnedByFaceUnlock)
            {
                if (!_radioOwnedByFaceUnlock)
                    _log($"[BLUETOOTH] request_id={requestId} restore_off=SKIPPED ownership=External");
                return;
            }

            var current = await _radio.GetStateAsync(ct);
            if (current.StateVersion != _ownedStateVersion)
            {
                ClearOwnership();
                _log($"[BLUETOOTH] request_id={requestId} restore_off=SKIPPED ownership=External reason=state_changed");
                return;
            }
            if (current.State == BluetoothState.Disabled)
            {
                ClearOwnership();
                _log($"[BLUETOOTH] request_id={requestId} restore_off=SUCCESS already_off=true");
                return;
            }
            if (current.State != BluetoothState.Enabled)
            {
                ClearOwnership();
                _log($"[BLUETOOTH] request_id={requestId} restore_off=SKIPPED state={current.State}");
                return;
            }

            var disabled = await _radio.SetEnabledAsync(false, ct);
            if (disabled.State == BluetoothState.Disabled)
            {
                ClearOwnership();
                _log($"[BLUETOOTH] request_id={requestId} restore_off=SUCCESS");
            }
            else
            {
                // Keep the marker if Windows refused the restore. A later
                // FaceUnlock request can adopt the ownership and retry cleanup.
                _radioOwnedByFaceUnlock = false;
                _persistedOwnershipPending = true;
                _log($"[BLUETOOTH] request_id={requestId} restore_off=FAILED state={disabled.State} message={disabled.Message}");
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task ReleaseAllAsync(CancellationToken ct = default)
    {
        string[] requests;
        lock (_activeRequests) requests = _activeRequests.ToArray();
        foreach (var requestId in requests)
            await ReleaseAsync(requestId, ct);
    }

    public IBluetoothRadioManager ForRequest(string requestId) =>
        new RequestRadioManager(this, requestId);

    private void AddRequest(string requestId)
    {
        lock (_activeRequests) _activeRequests.Add(requestId);
    }

    private bool RemoveRequest(string requestId)
    {
        lock (_activeRequests) return _activeRequests.Remove(requestId);
    }

    private bool ContainsRequest(string requestId)
    {
        lock (_activeRequests) return _activeRequests.Contains(requestId);
    }

    private void ClearOwnership()
    {
        _radioOwnedByFaceUnlock = false;
        _persistedOwnershipPending = false;
        DeleteOwnershipMarker();
    }

    private bool ReadOwnershipMarker()
    {
        try { return !string.IsNullOrWhiteSpace(_ownershipMarkerPath) && File.Exists(_ownershipMarkerPath); }
        catch { return false; }
    }

    private void WriteOwnershipMarker()
    {
        if (string.IsNullOrWhiteSpace(_ownershipMarkerPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(_ownershipMarkerPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_ownershipMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex) { _log($"[BLUETOOTH] ownership persistence write failed error={ex.Message}"); }
    }

    private void DeleteOwnershipMarker()
    {
        if (string.IsNullOrWhiteSpace(_ownershipMarkerPath)) return;
        try { if (File.Exists(_ownershipMarkerPath)) File.Delete(_ownershipMarkerPath); }
        catch (Exception ex) { _log($"[BLUETOOTH] ownership persistence cleanup failed error={ex.Message}"); }
    }

    private static string? DefaultOwnershipMarkerPath()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, "FaceUnlock", "bluetooth-owned.marker");
        }
        catch { return null; }
    }

    private sealed class RequestRadioManager : IBluetoothRadioManager
    {
        private readonly BluetoothLeaseManager _owner;
        private readonly string _requestId;

        public RequestRadioManager(BluetoothLeaseManager owner, string requestId)
        {
            _owner = owner;
            _requestId = requestId;
        }

        public Task<BluetoothRadioStatus> GetStateAsync(CancellationToken ct = default) =>
            GetLeasedStatusAsync(ct);

        public Task<BluetoothRadioStatus> SetEnabledAsync(bool enabled, CancellationToken ct = default) =>
            enabled ? EnsureEnabledAsync(ct) : Task.FromResult(new BluetoothRadioStatus(BluetoothState.AccessDenied, false, "Lease clients cannot disable Bluetooth directly"));

        public async Task<BluetoothRadioStatus> EnsureEnabledAsync(CancellationToken ct = default)
        {
            var status = await _owner.EnsureEnabledAsync(_requestId, ct);
            return new BluetoothRadioStatus(status.State, status.AutoEnabledByFaceUnlock, status.Message);
        }

        private async Task<BluetoothRadioStatus> GetLeasedStatusAsync(CancellationToken ct)
        {
            var status = await _owner.EnsureEnabledAsync(_requestId, ct);
            return new BluetoothRadioStatus(status.State, status.AutoEnabledByFaceUnlock, status.Message);
        }
    }
}
