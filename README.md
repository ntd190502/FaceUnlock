# FaceUnlock

Biometric Face ID approval system for a Windows PC using an iPhone companion app.

## What is included

- `ios/`: Swift/SwiftUI companion app with Face ID, Secure Enclave-backed ECDSA P-256 signing, Keychain token storage, foreground pending-session polling, MTU-safe BLE framing, and QR pairing/fallback.
- `windows/`: .NET 8 Windows components including Agent, SYSTEM Service, Post-Logon Shell Gate, and the standalone on-demand `FaceUnlock.AlertUI.exe`. The Windows side provides explicit device selection/revocation, DPAPI token protection, online protocol, targeted BLE discovery/framing, QR generation, status/log UI, and interactive alert presentation.
- `hosting/`: PHP 8 + MySQL backend with plain Telegram HTTPS-link notifications, hashed one-time approval tokens, device-bound sessions, revocation, and server-side signature verification.
- `docs/`: architecture, protocol, security model, deployment/build instructions, limitations, and regression test plan.

## Windows components

| Component | Role | Background behavior |
| --- | --- | --- |
| `FaceUnlock.Service.exe` | SYSTEM service; owns privileged/background control, approval state, Shell Gate enforcement, and alert dispatch | Runs as the Windows service |
| `FaceUnlock.Agent.exe` | User-facing configuration, pairing, device selection, status and logs | User application; no longer owns warning popup presentation |
| `FaceUnlockShell.exe` | Post-Logon Shell Gate before Explorer | Runs only while the session gate is active and exits after authorization |
| `FaceUnlock.AlertUI.exe` | Standalone interactive warning popup | **Not kept running.** Launched only when a warning must be shown and exits when the popup is closed |

## Core flows

### 1. Online approval through Telegram

```text
Windows Agent
    -> POST /v1/unlock/request { device_id }
Hosting
    -> creates an opaque, short-lived approval URL
    -> Telegram Bot message containing the plain URL (no button/callback)
iPhone
    -> opens the HTTPS URL and hands off to FaceUnlock
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
- Online and BLE crypto sessions share one signed logical Windows request, so transport switching or BLE response retry does not require a second Face ID approval.
- Bluetooth is restored to OFF only when FaceUnlock changed it from OFF to ON; an initially enabled radio remains enabled.

See `docs/PROTOCOL.md` for the framing format.

### 4. On-demand Windows AlertUI

Warning presentation is separated from `FaceUnlock.Agent.exe`.

```text
FaceUnlock.Service.exe (SYSTEM)
    -> detects/receives a warning
    -> writes the current alert payload
    -> finds the active interactive Windows session
    -> obtains the user token with WTS APIs
    -> launches FaceUnlock.AlertUI.exe inside that user session
FaceUnlock.AlertUI.exe
    -> displays the warning on the user's desktop
    -> can refresh an already-open warning when the payload changes
    -> exits when the user closes the warning
```

This architecture intentionally does **not** keep an AlertUI helper running in the background. `FaceUnlock.Service.exe` remains responsible for background work and starts the UI only when interaction is required. The Agent is therefore no longer the owner of `AlertWindow`/alert popup presentation.

## FaceUnlock Architecture (Phase F: Post-Logon Shell Gate)

FaceUnlock implements a **Post-Logon Windows Shell Gate** (`FaceUnlockShell.exe`):
- Runs before `explorer.exe` (Windows desktop is not released until biometric authorization).
- Communicates strictly via local IPC with `FaceUnlock.Service.exe`.
- Automatically initiates **exactly one** Face ID authorization request upon session start.
- Single-use authorization grant is bound to:
  - Unique `request_id`
  - Current Windows User SID
  - Process Windows Session ID
  - Machine PC ID & Paired Device ID
- When Face ID is approved and the single-use grant is consumed, `FaceUnlockShell.exe` safely launches `%WINDIR%\explorer.exe` and exits cleanly.

### Security Boundary & Architecture Notice
- **Not a Windows Hello / LSA Passwordless Replacement**: This is a **Post-Logon Shell Gate**, not an LSA security boundary. The Windows session is established before the Shell Gate executes.
- **Mandatory per-session gate**: While locked, the Shell blocks common Win/Alt/Ctrl escape shortcuts and rejects window close requests. The SYSTEM service restarts a killed Shell and terminates Explorer started before a SID/session-bound grant is consumed. Ctrl+Alt+Del remains available, but Task Manager does not grant desktop authorization. WinRE and Safe Mode are unchanged.
- **Retired security stack**: The legacy Credential Provider and Phase E AuthPackage source/runtime were removed. `Cleanup-PhaseE.ps1` remains migration-only so upgrades can remove historical FaceUnlock registry entries and files without touching Windows providers or rebooting automatically.
- Built-in Windows PIN and password providers remain fully functional as safe recovery paths.

## Fast start

1. Deploy `hosting/` and import `hosting/schema.sql`.
2. Copy `hosting/config.example.php` to `hosting/config.php`; configure database and Telegram credentials.
3. Build Windows managed projects with `windows/scripts/build.ps1` or `.NET 8` commands. Windows release packaging includes `FaceUnlock.AlertUI.exe` alongside the other Windows components.
4. Generate the iOS project with XcodeGen and build on a physical iPhone.
5. Pair by scanning the Windows QR from `FaceUnlock.Agent.exe`.
6. Test Shell Gate safely:
   ```powershell
   FaceUnlockShell.exe --test
   ```
7. Check diagnostics and configuration:
   ```powershell
   powershell -ExecutionPolicy Bypass -File windows/scripts/Check-ShellGate.ps1
   powershell -ExecutionPolicy Bypass -File windows/scripts/Enable-ShellGate.ps1 -DryRun
   ```

## Security highlights

- Hosting never stores the Windows PIN/password.
- Device and PC bearer tokens are not persisted in plaintext application config.
- iPhone pins the PC identity/public key obtained from the pairing QR.
- Online and offline approvals are signed ECDSA P-256/SHA-256 using ASN.1 DER signatures.
- Sessions/challenges expire and are bound to the paired device, Windows user SID, and Session ID.
- Revoked devices cannot use device-authenticated server routes.
- BLE transport itself is not trusted; cryptographic signatures remain authoritative.
- Fail-closed design: Service unavailability, rejection, timeout, or missing grants never launch Explorer.
- Interactive warnings are launched into the active user session on demand; `FaceUnlock.AlertUI.exe` is not a permanent background process.
- Built-in emergency recovery script (`FaceUnlock-Shell-Recovery.ps1`) restores `explorer.exe` cleanly.
