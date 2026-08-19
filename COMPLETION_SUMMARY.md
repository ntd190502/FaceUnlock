# FaceUnlock phase 1–6 completion overlay

Baseline inspected: `a18d27409dadf4b53226b1352b8c2a606ee9a108`.

This overlay completes the remaining work without modifying `windows/CredentialProvider/` or `windows/CompanionCDF/`.

## Added/fixed

- BLE framing v1 in Swift + C# with 20-byte minimum-MTU-safe request frames.
- Two-way multi-frame reassembly, 16 KiB cap, 15-second assembly expiry and duplicate-frame tolerance.
- iOS BLE response queuing honors CoreBluetooth backpressure (`peripheralManagerIsReady`).
- Windows BLE matching fails closed if the expected Device ID characteristic is absent/unreadable/mismatched.
- iOS refreshes the Device ID characteristic when advertising after pairing, fixing the stale `unpaired` identity case.
- Pre-framing BLE messages remain accepted as a compatibility path.
- Legacy iOS `deviceAPIToken` migration now immediately rewrites sanitized UserDefaults JSON after moving the token to Keychain.
- CI static checks now assert the BLE codecs/security primitives exist and obsolete APNs runtime references do not return.
- Protocol, architecture, security, test plan, limitations, manifest and release notes are synchronized with the current Telegram/foreground/BLE design.

## Scope deliberately not changed

- Windows Credential Provider / actual OS lock-screen credential serialization.
- CompanionCDF reference.
- Telegram bot secrets, live hosting config or DB credentials.

## Local validation performed

- Swift framing codec typecheck: PASS.
- Swift framing round-trip/reassembly self-test: PASS (1024-byte payload, 94 frames, out-of-order + duplicate-frame coverage).
- Swift overlay syntax parse: PASS.
- Package static checks / manifest stale-path checks: PASS when generated.
- C# Windows build cannot be performed in this Linux container; a dependency-free C# BLE framing self-test is included and wired into GitHub Windows CI after applying/pushing.
