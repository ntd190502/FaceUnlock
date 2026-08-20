namespace FaceUnlock.Core;

public sealed record BluetoothLeaseStatus(
    BluetoothState State,
    bool WasInitiallyEnabled,
    bool AutoEnabledByFaceUnlock,
    string? Message = null);

/// <summary>
/// Owns Bluetooth state only for the current service lifetime. Multiple logical
/// requests share one radio lease, and the last request restores OFF only when
/// this manager has direct evidence that FaceUnlock enabled the radio.
/// </summary>
public sealed class BluetoothLeaseManager
{
    private readonly IBluetoothRadioManager _radio;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly HashSet<string> _activeRequests = new(StringComparer.Ordinal);
    private bool _radioOwnedByFaceUnlock;
    private long _ownedStateVersion;

    public BluetoothLeaseManager(IBluetoothRadioManager radio, Action<string>? log = null)
    {
        _radio = radio;
        _log = log ?? (_ => { });
    }

    public int ActiveLeaseCount
    {
        get { lock (_activeRequests) return _activeRequests.Count; }
    }

    public bool RadioOwnedByFaceUnlock => _radioOwnedByFaceUnlock;

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
                        _radioOwnedByFaceUnlock = false;
                        _log($"[BLUETOOTH] request_id={requestId} ownership=External reason=state_changed");
                    }
                    return new(leasedState.State, !_radioOwnedByFaceUnlock, _radioOwnedByFaceUnlock, leasedState.Message);
                }

                RemoveRequest(requestId);
                if (ActiveLeaseCount == 0)
                    _radioOwnedByFaceUnlock = false;
            }

            var current = await _radio.GetStateAsync(ct);
            var initiallyEnabled = current.State == BluetoothState.Enabled;
            if (initiallyEnabled)
            {
                AddRequest(requestId);
                var owner = _radioOwnedByFaceUnlock ? "FaceUnlock" : "External";
                _log($"[BLUETOOTH] request_id={requestId} initial_state=Enabled ownership={owner}");
                return new(current.State, true, _radioOwnedByFaceUnlock, current.Message);
            }

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
            AddRequest(requestId);
            _log($"[BLUETOOTH] request_id={requestId} auto_enable=SUCCESS ownership=FaceUnlock");
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
                _radioOwnedByFaceUnlock = false;
                _log($"[BLUETOOTH] request_id={requestId} restore_off=SKIPPED ownership=External reason=state_changed");
                return;
            }
            if (current.State == BluetoothState.Disabled)
            {
                _radioOwnedByFaceUnlock = false;
                _log($"[BLUETOOTH] request_id={requestId} restore_off=SUCCESS already_off=true");
                return;
            }
            if (current.State != BluetoothState.Enabled)
            {
                _radioOwnedByFaceUnlock = false;
                _log($"[BLUETOOTH] request_id={requestId} restore_off=SKIPPED state={current.State}");
                return;
            }

            var disabled = await _radio.SetEnabledAsync(false, ct);
            _radioOwnedByFaceUnlock = false;
            _log(disabled.State == BluetoothState.Disabled
                ? $"[BLUETOOTH] request_id={requestId} restore_off=SUCCESS"
                : $"[BLUETOOTH] request_id={requestId} restore_off=FAILED state={disabled.State} message={disabled.Message}");
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
