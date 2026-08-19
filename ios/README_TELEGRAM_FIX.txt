FaceUnlock iOS Telegram/BLE status

The APNs -> Telegram migration is already integrated.

Current iOS completion state:
- Telegram/deep-link online approval
- foreground `/v1/unlock/pending` polling
- Face ID approval task survives temporary scene inactivity
- device bearer token stored in Keychain
- PC identity pinned from pairing QR
- BLE Device ID refreshed immediately after pairing
- BLE framing v1 reassembles multi-packet requests and sends chunked responses
- CoreBluetooth notification backpressure is queued and resumed

No PushManager/APNs runtime source is required.
