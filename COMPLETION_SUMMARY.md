# FaceUnlock source completion summary

Baseline inspected: `a18d27409dadf4b53226b1352b8c2a606ee9a108`.

The current source includes Phase F/F.1/F.2 Shell Gate, connectivity-aware Online/BLE
transport, Telegram plain approval URLs, safe installer orchestration, and all tracked
Windows/iOS/hosting build inputs. The retired Credential Provider, Phase E AuthPackage,
CompanionCDF, their harnesses, dead IPC, and runtime secret store were removed.

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
- Upgrade migration removes exact legacy registry entries/files, preserves `msv1_0`, unrelated providers, pairing and Shell Gate state, and never auto-reboots.

## Scope deliberately preserved

- Phase F/F.1/F.2 Shell Gate, Service watchdog, Explorer/input guards and one-time grant consumption.
- Online/BLE logical-request behavior, Telegram plain URL flow, DPAPI PC token storage and Bluetooth leases.
- Telegram bot secrets, live hosting config or DB credentials.

## Local validation performed

- Swift framing codec typecheck: PASS.
- Swift framing round-trip/reassembly self-test: PASS (1024-byte payload, 94 frames, out-of-order + duplicate-frame coverage).
- Swift overlay syntax parse: PASS.
- Package static checks / manifest stale-path checks: PASS when generated.
- Windows build/tests, migration simulation, PHP/Swift checks and Setup compilation are enforced by GitHub Actions.
