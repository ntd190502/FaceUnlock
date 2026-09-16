# FaceUnlock iOS Companion App

Swift & SwiftUI companion application for **FaceUnlock**, enabling Apple hardware-backed biometric (Face ID) authorization for Windows PCs.

---

## Key Features

- **Apple Secure Enclave**: Generates hardware-isolated ECDSA P-256 key pairs. Private keys never leave the Secure Enclave and require biometric approval to sign challenges.
- **Offline BLE GATT Server**: Implements custom BLE Framing Protocol v1 (20-byte MTU safe chunks) with CoreBluetooth backpressure handling to authenticate PCs without Internet access.
- **Online Telegram Flow**: Supports opening deep-links (`faceunlock://...`) triggered from Telegram Bot notifications to approve unlock requests.
- **QR Pairing & Fallback**: Scans Windows setup QR codes to establish bidirectional cryptographic trust.
- **Zero Bloat**: Stripped of legacy PC power controls and file transfer utilities; purely dedicated to biometric authorization and cryptographic verification.

---

## Architecture & Project Structure

```text
ios/FaceUnlock/
├── BLE/
│   ├── BLEFrameCodec.swift       # MTU-safe frame packetizer & reassembler
│   └── BLEPeripheralManager.swift# CoreBluetooth peripheral advertising & GATT server
├── Security/
│   ├── DeviceKey.swift           # Secure Enclave P-256 key management & signing
│   ├── FaceAuth.swift            # LocalAuthentication biometry prompt (Face ID)
│   ├── KeychainHelper.swift      # Secure Keychain storage for device tokens
│   └── SignatureVerifier.swift   # Verification of PC signatures
├── Network/
│   └── APIClient.swift           # Communication with FaceUnlock hosting backend
├── QR/
│   └── QRScannerView.swift       # AVFoundation camera scanner for pairing QRs
├── Unlock/
│   ├── LogicalBiometricApprovalCache.swift # Deduplication & approval caching
│   └── UnlockCoordinator.swift   # State machine coordinating online & BLE flows
├── ContentView.swift             # Main SwiftUI interface
└── project.yml                   # XcodeGen project configuration
```

---

## Building & Sideloading

### 1. Build using GitHub Actions (Recommended)
This repository includes `.github/workflows/build-ios-ipa.yml` which builds TrollStore / unsigned `.ipa` packages on `macos-14` runners:
- Download `FaceUnlock.ipa` directly from the Actions artifact tab.
- Install using **TrollStore**, **Sideloadly**, or **AltStore**.

### 2. Build Locally with Xcode
Requires macOS with Xcode 15+ and [XcodeGen](https://github.com/yonaskolb/XcodeGen):

```bash
cd ios
xcodegen generate
open FaceUnlock.xcodeproj
```

Select your development team, attach your physical iPhone, and run.
