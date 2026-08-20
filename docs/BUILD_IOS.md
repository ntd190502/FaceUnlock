# Build iOS app

Requirements: macOS, current Xcode, physical iPhone with Face ID, XcodeGen.

```bash
cd ios
xcodegen generate
open FaceUnlock.xcodeproj
```

In Xcode:

1. Select your Team if using normal development signing.
2. Change `PRODUCT_BUNDLE_IDENTIFIER` if needed.
3. Keep Background Modes -> Uses Bluetooth LE accessories.
4. Build and deploy to the physical iPhone.

GitHub Actions compiles and runs `ios/scripts/ble_frame_selftest.swift` against
`BLEFrameCodec.swift`, plus `ios/scripts/logical_unlock_selftest.swift` against
the logical biometric cache. These catch framing/reassembly regressions and
duplicate Face ID prompts independently from the app build.

The runtime online trigger is Telegram/foreground polling; APNs Push capability is not required by the current source.
