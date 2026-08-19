# Architecture

## Online approval path

```text
Windows Agent
    -> HTTPS POST /v1/unlock/request (with selected device_id)
Shared Hosting (PHP/MySQL)
    -> creates one-time challenge/session bound to that device
    -> Telegram Bot API sendMessage (inline button with HTTPS landing URL)
iPhone Telegram app
    -> User taps "Mở FaceUnlock"
    -> Opens https://domain/telegram/open/{session_id}
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

The hosting server never receives or stores the Windows PIN/password.

**Scope note:** the flow above completes FaceUnlock biometric approval. The current `CredentialProvider` remains a scaffold; a successful phone approval does not by itself bypass Windows/LSA or automatically unlock the Windows logon session.

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
- `FaceUnlock.Service`: background service/named-pipe base. Credential Provider integration is intentionally outside this phase.

### Hosting
- PDO/MySQL storage with indexes for PC, device, and unlock sessions.
- PC/device bearer tokens stored as SHA-256 hashes.
- Pairing code expiry and explicit device association.
- Device revocation.
- One-time unlock challenges and status transitions (`PENDING`, `APPROVED`, `REJECTED`, `EXPIRED`).
- Telegram notification dispatch and HTTPS landing-page deep link.
- Server-side ECDSA verification before a session becomes `APPROVED`.
