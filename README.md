# FaceUnlock

Biometric Face ID approval system for a Windows PC using an iPhone companion app.

## What is included

- `ios/`: Swift/SwiftUI companion app with Face ID, Secure Enclave-backed ECDSA P-256 signing, Keychain token storage, foreground pending-session polling, MTU-safe BLE framing, and QR pairing/fallback.
- `windows/`: .NET 8 Agent/Service with explicit device selection/revocation, DPAPI token protection, online protocol, targeted BLE discovery/framing, QR generation, and status/log UI.
- `hosting/`: PHP 8 + MySQL backend with Telegram Bot notification dispatch, device-bound sessions, revocation, landing-page deep linking, and server-side signature verification.
- `docs/`: architecture, protocol, security model, deployment/build instructions, limitations, and regression test plan.

## Core flows

### 1. Online approval through Telegram

```text
Windows Agent
    -> POST /v1/unlock/request { device_id }
Hosting
    -> Telegram Bot message
iPhone
    -> Mở FaceUnlock
    -> Face ID
    -> P-256 DER signature
    -> POST /v1/unlock/approve/{session}
Hosting
    -> APPROVED
Windows
    -> polls status
    -> verifies the iPhone signature locally
```

### 2. Foreground iPhone approval

If FaceUnlock is already active, it polls `/v1/unlock/pending`. A new device-bound session starts a separate Face ID approval task, so the temporary iOS `.inactive` state caused by the biometric prompt does not cancel the approval.

### 3. Offline BLE / QR fallback

- Windows scans the FaceUnlock BLE service and reads the Device ID characteristic.
- A candidate is accepted only if Device ID exactly matches the selected paired iPhone.
- Offline request/response JSON uses BLE framing v1 instead of assuming one GATT packet is large enough.
- Windows request frames default to 20 bytes (minimum-ATT-MTU safe).
- iOS reassembles the request, verifies the PC signature, runs Face ID, signs the response, and sends response chunks with CoreBluetooth backpressure handling.
- If automatic BLE discovery misses, Windows shows a signed QR payload. The iPhone validates it, caches one biometric approval, advertises again, and Windows reconnects to the same paired Device ID.

See `docs/PROTOCOL.md` for the framing format.

## Fast start

1. Deploy `hosting/` and import `hosting/schema.sql`.
2. Copy `hosting/config.example.php` to `hosting/config.php`; configure database and Telegram credentials.
3. Build Windows managed projects with `windows/scripts/build.ps1` or the `.NET 8` commands in `docs/BUILD_WINDOWS.md`.
4. Generate the iOS project with XcodeGen and build on a physical iPhone.
5. Pair by scanning the Windows QR.
6. Test foreground online approval, Telegram/deep-link approval, and offline BLE/QR fallback.

## Security highlights

- Hosting never stores the Windows PIN/password.
- Device and PC bearer tokens are not persisted in plaintext application config.
- iPhone pins the PC identity/public key obtained from the pairing QR.
- Online and offline approvals are signed ECDSA P-256/SHA-256 using ASN.1 DER signatures.
- Sessions/challenges expire and are bound to the paired device.
- Revoked devices cannot use device-authenticated server routes.
- BLE transport itself is not trusted; cryptographic signatures remain authoritative.

## Important Windows limitation

The current repository completes **FaceUnlock biometric approval**, not an undocumented Windows authentication bypass. A normal third-party Credential Provider cannot simply tell Winlogon/LSA to unlock because a phone signed a challenge. `windows/CredentialProvider/` remains a scaffold and is intentionally outside the phase 1–6 hardening work.

Keep built-in Windows PIN/password providers enabled as the recovery path.

See `docs/LIMITATIONS.md`.
