# Architecture

## Online approval path

```text
Windows Agent
    -> HTTPS POST /v1/unlock/request (with selected device_id)
Shared Hosting (PHP/MySQL)
    -> creates one-time challenge/session bound to that device
    -> creates a 256-bit random approval token and stores only its SHA-256 hash
    -> Telegram Bot API sendMessage with a plain HTTPS URL in the text
iPhone Telegram app
    -> User taps https://domain/u/{opaque_token}
    -> Hosting accepts only a PENDING, unexpired token-bound session
    -> Redirects to faceunlock://session?id={session_id}
FaceUnlock iOS app
    -> Fetches device-bound session
    -> Prompts Face ID
    -> Signs canonical challenge with Secure Enclave / P-256 key
    -> HTTPS POST /v1/unlock/approve/{session_id}
Hosting
    -> verifies iPhone signature
    -> updates session to APPROVED
Windows Agent
    -> polls /v1/unlock/status/{session_id}
    -> verifies ECDSA signature again against the target device public key snapshot
    -> FaceUnlock approval succeeds
```

The Telegram payload has no inline keyboard or callback data. The opaque URL token is
bound to one device-bound session, expires with that session, and cannot approve a
completed, rejected, expired, or cancelled request. The hosting server never receives
or stores the Windows PIN/password.

**Scope note:** the flow above completes FaceUnlock biometric approval for the post-logon Shell Gate. The repository intentionally contains no Windows Credential Provider or custom authentication package and does not bypass Windows/LSA logon.

## Foreground app polling path

```text
FaceUnlock iOS app active
    -> periodically polls GET /v1/unlock/pending
    -> detects a PENDING session bound to this device token
    -> starts a separate approval task
    -> Face ID
    -> signs and approves session
```

The Face ID approval task is kept separate from the polling task so the temporary `.inactive` scene state caused by the biometric system UI cannot cancel an in-progress approval.

## Offline BLE path

Phase F.1 makes transport selection connectivity-aware. The Windows service monitors
the current Internet profile, starts directly with BLE while offline, and switches
from Online to BLE if connectivity is lost, the online request fails, or it expires.
Before BLE discovery it detects the Windows Bluetooth radio and requests that Windows
turn it on when access is allowed. Discovery uses a bounded scan/rest cadence rather
than a total wait timeout. Windows may deny radio control to a service; that result is surfaced and the
flow fails closed rather than pretending Bluetooth is available.

For a pending Shell/Service authentication, BLE waiting is intentionally unbounded:
it scans for 9 seconds, rests for 2.5 seconds, and repeats with exactly one active
scanner and one IPC request ID. Pending grants do not expire while this loop is
active; approval grants remain limited to 30 seconds. Explicit cancellation or
service shutdown cancels the active scan immediately.

Every transport belongs to one `LogicalUnlockAttempt` keyed by the Windows
`request_id`. Fresh BLE sessions and challenges remain mandatory for replay
resistance, but their signed payload also carries the logical request and the
prior online session ID. iOS caches one successful biometric ceremony for those
signed aliases, so retrying a BLE crypto exchange or switching Online -> BLE does
not prompt Face ID twice. The first valid transport approval wins; later results
cannot overwrite the grant.

Bluetooth activation is managed by request leases. If FaceUnlock itself changes
the radio from OFF to ON, the last active lease restores it to OFF after consume,
cancel, rejection, timeout, or expiry. A radio that was already ON is classified
as externally owned and is never disabled by FaceUnlock.
Windows radio state-change generations revoke that ownership conservatively when
the user or another component changes Bluetooth during the lease.

```text
Windows scans for FaceUnlock BLE service
    -> connects to a candidate
    -> reads Device ID characteristic
    -> requires exact match with the selected paired iPhone
    -> serializes signed offline request JSON
    -> splits request into BLE framing-v1 chunks
    -> writes chunks to request characteristic

iPhone
    -> reassembles request chunks
    -> verifies PC signature and expiry
    -> prompts Face ID
    -> signs response
    -> splits response into BLE framing-v1 notification chunks

Windows
    -> reassembles all response chunks
    -> verifies iPhone signature locally
    -> FaceUnlock offline approval succeeds
```

The framing layer is MTU-safe: Windows uses 20-byte request frames by default, while iOS can use the connected central's notification capacity for response frames. Reassembly has a 16 KiB message limit and a 15-second timeout.

If automatic discovery fails, Windows displays a QR containing the same signed offline request. Scanning the QR brings the iPhone app to the foreground, validates the PC signature, caches the Face ID approval, and restarts BLE advertising. Windows then reconnects to the exact paired Device ID.

## Components

### iOS
- `FaceAuth`: explicit Face ID prompt via LocalAuthentication.
- `DeviceKey`: P-256 signing key generated in Secure Enclave when available.
- `KeychainHelper`: Keychain storage for the device bearer token.
- `APIClient`: pairing, session fetch, approval/rejection, foreground pending polling.
- `BLEFrameCodec`: MTU-safe BLE framing and bounded reassembly.
- `BLEPeripheralManager`: advertises the offline service, exposes the current device ID, reassembles request chunks, and queues response notifications with CoreBluetooth backpressure handling.
- `QRScannerView`: scans pairing and offline fallback QR payloads.
- `UnlockCoordinator`: pins PC identity, validates signed requests, coordinates Face ID, signs canonical bytes, and returns approvals.

### Windows
- `FaceUnlock.Core`: protocol, DPAPI token persistence, ECDSA, REST client, `BLEFrameCodec`, and targeted BLE scanner.
- `FaceUnlock.Agent`: device pairing/selection/revocation, QR display, online approval, BLE fallback, status/log UI, and duplicate-operation guards.
- `FaceUnlock.Service`: background service and named-pipe broker. In Phase F.2 it owns conservative `LOCKED`/`UNLOCKED` state per interactive SID/session, atomically authorizes desktop release when a reserved Shell grant is consumed, restarts a missing Shell with WTS user tokens plus `CreateProcessAsUser`, and terminates pre-authorization Explorer processes only in the matching session.
- `FaceUnlock.Shell`: post-logon gate with a scoped low-level input guard. It cannot authorize itself; it launches Explorer only after the Service confirms consumption of the current request's bound grant.

### Hosting
- PDO/MySQL storage with indexes for PC, device, and unlock sessions.
- PC/device bearer tokens stored as SHA-256 hashes.
- Pairing code expiry and explicit device association.
- Device revocation.
- One-time unlock challenges and status transitions (`PENDING`, `APPROVED`, `REJECTED`, `EXPIRED`, `CANCELLED`).
- Plain Telegram HTTPS-link dispatch with hashed, opaque approval tokens; no Telegram callback is required.
- Server-side ECDSA verification before a session becomes `APPROVED`.

## Legacy upgrade migration

The old Credential Provider, Phase E AuthPackage, CompanionCDF reference, and their
harnesses have been removed. The installer retains only `Cleanup-PhaseE.ps1` as an
idempotent migration: it removes the exact historical FaceUnlock CLSID/package,
preserves `msv1_0` and unrelated providers, cleans obsolete files, and never reboots.
