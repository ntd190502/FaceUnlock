# Windows components

- `FaceUnlock.Core`: API, crypto, config, BLE.
- `FaceUnlock.Agent`: WPF pairing/test UI.
- `FaceUnlock.Service`: Windows Service/named-pipe base.
- `CredentialProvider`: safe COM scaffold; not a passwordless unlock bypass.
- `CompanionCDF`: restricted Microsoft API reference.

Start with the Agent. Do not install the Credential Provider until online/BLE approval tests pass.
