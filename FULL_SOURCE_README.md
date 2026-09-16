# FaceUnlock Complete Source Distribution

This repository is the complete, canonical source distribution of the **FaceUnlock** biometric workstation authorization project.

---

## What FaceUnlock Does

FaceUnlock transforms any iPhone equipped with Face ID into a physical security key for a Windows PC:
- **Post-Logon Shell Gate (`FaceUnlockShell.exe`)**: Blocks access to the Windows desktop (`explorer.exe`) until biometric approval is granted.
- **Hardware-Isolated Signing**: Private keys are generated and held exclusively within the Apple Secure Enclave (`ECDSA P-256`).
- **Dual-Transport Architecture**: Authenticates via Telegram online web notifications or completely offline via Bluetooth Low Energy (BLE GATT).
- **Streamlined Footprint**: Stripped clean of legacy bloatware (remote power switches, basic resource alerts, and file hosting), leaving a 100% focused workstation protection engine.

---

## Directory Index

- `ios/`: Native Swift / SwiftUI companion app with Secure Enclave cryptographic signing and custom BLE Framing Protocol v1.
- `windows/`: .NET 8 solution containing `FaceUnlock.Service` (privileged SYSTEM daemon), `FaceUnlock.Shell` (screen gatekeeper), `FaceUnlock.Agent` (user UI), and `FaceUnlock.Core`.
- `hosting/`: PHP 8.1+ REST API managing device pairing and Telegram approval dispatches.
- `docs/`: Formal architecture blueprints, cryptographic specifications, and disaster recovery procedures.

---

## Automated CI/CD Workflows

Every commit pushed to `main` is built and verified automatically via GitHub Actions:
- `build-windows-release.yml`: Publishes single-file trimmed .NET 8 executables and generates `FaceUnlock-Setup.exe`.
- `build-ios-ipa.yml`: Builds signed and unsigned TrollStore `.ipa` packages using native Xcode runners.
- `ci.yml`: Executes the full regression suite (19 unit tests, BLE framing codecs, and static security checks).
