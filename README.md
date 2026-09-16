# FaceUnlock

[![Build FaceUnlock Windows Release](https://github.com/ntd190502/FaceUnlock/actions/workflows/build-windows-release.yml/badge.svg)](https://github.com/ntd190502/FaceUnlock/actions/workflows/build-windows-release.yml)
[![Build FaceUnlock TrollStore IPA](https://github.com/ntd190502/FaceUnlock/actions/workflows/build-ios-ipa.yml/badge.svg)](https://github.com/ntd190502/FaceUnlock/actions/workflows/build-ios-ipa.yml)
[![CI - Windows & Hosting & Static Checks](https://github.com/ntd190502/FaceUnlock/actions/workflows/ci.yml/badge.svg)](https://github.com/ntd190502/FaceUnlock/actions/workflows/ci.yml)

**FaceUnlock** is a biometric Face ID authorization and workstation security system for Windows PCs using an iPhone companion app with hardware-backed Secure Enclave signing.

It replaces the post-logon shell with an unbreakable gate (`FaceUnlockShell.exe`), requiring biometric Face ID approval over local Bluetooth Low Energy (BLE) or Telegram online approval before desktop access (`explorer.exe`) is released.

---

## What is included

- `ios/`: Swift / SwiftUI companion app featuring:
  - Apple Secure Enclave ECDSA P-256 hardware key generation and challenge signing
  - Offline MTU-safe Bluetooth Low Energy (BLE GATT) framing protocol (v1)
  - QR-based device pairing and offline fallback
  - Keychain-backed hardware device tokens
- `windows/`: .NET 8 modern Windows solution featuring:
  - `FaceUnlock.Service.exe`: Privileged SYSTEM service managing session gates, BLE broker, and Shell enforcement
  - `FaceUnlock.Agent.exe`: Desktop pairing UI, status monitor, and QR code generator
  - `FaceUnlockShell.exe`: Post-Logon Shell Gate running before `explorer.exe`
  - `FaceUnlock.AlertUI.exe`: Standalone on-demand warning dialog
- `hosting/`: Lightweight PHP 8 + MySQL API backend for online Telegram notification approvals and device-bound session verification.
- `docs/`: In-depth architecture specifications, cryptographic protocols, threat modeling, and recovery manuals.

---

## Quick Download & Installation

### 1. Windows PC
1. Head over to [GitHub Actions Windows Release](https://github.com/ntd190502/FaceUnlock/actions/workflows/build-windows-release.yml).
2. Click on the latest run and scroll down to the **Artifacts** section at the bottom.
3. Download the **`FaceUnlock-Windows-Release-x64`** package.
4. Extract and run `FaceUnlock-Setup.exe` with Administrator privileges.
5. Launch `FaceUnlock.Agent.exe` to pair with your iPhone.

### 2. iPhone (iOS 14.0+)
1. Head over to [GitHub Actions iOS IPA](https://github.com/ntd190502/FaceUnlock/actions/workflows/build-ios-ipa.yml).
2. Click on the latest run and scroll down to the **Artifacts** section at the bottom.
3. Download the **`FaceUnlock-TrollStore`** package containing `FaceUnlock.ipa`.
4. Sideload the IPA onto your iPhone using **TrollStore**, **Sideloadly**, or **AltStore**.
5. Open FaceUnlock, grant Camera/Bluetooth permissions, and scan the QR displayed on your PC.

---

## Windows Components Architecture

| Component | Execution Context | Role & Behavior |
| :--- | :--- | :--- |
| `FaceUnlock.Service.exe` | **NT AUTHORITY\SYSTEM** | Background service; enforces Shell Gate security, manages BLE discovery leases, and processes unlock grants. |
| `FaceUnlockShell.exe` | **Interactive User Session** | Post-Logon Shell Gate before Explorer; locks the screen and intercepts escape shortcuts until biometric approval. |
| `FaceUnlock.Agent.exe` | **Interactive User Session** | Configuration, initial device pairing, QR generation, and real-time status diagnostics. |
| `FaceUnlock.AlertUI.exe` | **Interactive User Session** | Standalone on-demand interactive dialog; only launched by the Service when an alert is active. |

---

## Core Authentication Flows

### 1. Offline BLE Approval (No Internet Required)
```text
Windows (Locked)                iPhone (FaceUnlock)
  |                                     |
  |--- BLE Scan for Paired Device ID -->|
  |<-- Connected (GATT Server Ready) ---|
  |--- Send Challenge Frame (20B MTU) ->|
  |                                     | [Face ID Scan]
  |                                     | [Secure Enclave P-256 Sign]
  |<-- Return Signed DER Response ------|
  | [Verify iPhone P-256 Signature]     |
  | [Unlock Shell Gate -> Launch Explorer]
```

### 2. Online Telegram Approval
```text
Windows Agent
    -> POST /v1/unlock/request { device_id }
Hosting Backend
    -> Generates short-lived opaque approval token
    -> Sends Telegram Bot message containing direct HTTPS link
iPhone
    -> User taps link -> Opens FaceUnlock app
    -> Face ID verification -> P-256 hardware signature
    -> POST /v1/unlock/approve/{session}
Windows Service
    -> Polls approval grant -> Verifies signature locally -> Releases desktop
```

---

## Post-Logon Shell Gate Security Boundary

FaceUnlock implements a **Post-Logon Windows Shell Gate** (`FaceUnlockShell.exe`):
- **Runs prior to `explorer.exe`**: The Windows desktop, taskbar, and file manager are not initialized until cryptographic authorization succeeds.
- **IPC-Enforced Authority**: Session gate authority resides strictly inside `FaceUnlock.Service.exe` (SYSTEM). If a user terminates `FaceUnlockShell.exe`, the Service immediately restarts it and terminates any unauthorized `explorer.exe` instances.
- **Fail-Closed Guarantee**: Missing connectivity, timeout, process crash, or tampering results in the workstation remaining securely locked.
- **Emergency Safe Recovery**: An emergency recovery script (`FaceUnlock-Shell-Recovery.ps1`) is provided in the installation directory to restore default Explorer shell registration if needed via Safe Mode.

---

## Automated Verification & CI

The repository is continuously verified through GitHub Actions:
- **Build Complete Windows Release**: Publishes single-file trimmed .NET 8 executables and InnoSetup installers.
- **Build TrollStore IPA**: Builds standalone iOS unsigned/TrollStore IPAs using native Xcode toolchains.
- **Regression Suite**: Executes 19 transport & BLE lease unit tests, cross-platform framing codec verification, and static security checks.

---

## License & Security Research Notice

This project is developed for authorized technical research, personal workstation security hardening, and biometric companion authentication.
