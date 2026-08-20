# Windows components

- `FaceUnlock.Core`: API, crypto, config, BLE.
- `FaceUnlock.Agent`: WPF pairing/test UI.
- `FaceUnlock.Service`: Windows Service/named-pipe base.
- `CredentialProvider`: safe COM scaffold; not a passwordless unlock bypass.
- `CompanionCDF`: restricted Microsoft API reference.

Start with the Agent. Do not install the Credential Provider until online/BLE approval tests pass.

## Shell Gate security boundary

FaceUnlock Phase F is a post-logon Shell Gate. While approval is pending, a scoped
low-level keyboard guard blocks common user-mode escape shortcuts and the window
rejects close requests. The guard is removed after an approved grant is consumed,
before Explorer starts, and during application shutdown.

Ctrl+Alt+Del is the Windows Secure Attention Sequence and cannot be blocked by a
user-mode application. Phase F.2 keeps gate authority in the SYSTEM service: a
killed locked Shell is restarted, and Explorer started from Task Manager before a
bound grant is consumed is terminated in that same session. FaceUnlock does not
patch Winlogon/LSA, disable Windows security UI, or install persistent keyboard
mappings or recovery policies. WinRE and Safe Mode remain unchanged.
