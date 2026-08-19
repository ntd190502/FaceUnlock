# FaceUnlock Windows Credential Provider (Phase B)

This component provides the **FaceUnlock** tile on the Windows logon and lock screens (`CPUS_LOGON` and `CPUS_UNLOCK_WORKSTATION`).

## Architecture & Security Model

1. **Named Pipe IPC (`\\.\pipe\FaceUnlock.Auth.v1`)**:
   - The Credential Provider DLL (`FaceUnlockCredentialProvider.dll`) communicates with `FaceUnlock.Service` via a secure local named pipe.
   - The pipe ACL strictly permits `LOCAL_SYSTEM`, `BUILTIN_ADMINISTRATORS`, and `AUTHENTICATED_USERS` (LogonUI context).

2. **Decoupled Architecture**:
   - The Credential Provider DLL does **not** perform network requests or direct BLE communication.
   - All network calls (Online Unlock via server), Bluetooth LE communications (Offline Unlock), and cryptographic ECDSA verification are securely managed by `FaceUnlock.Service`.

3. **Phase B Boundary**:
   - When the user selects the FaceUnlock tile and clicks **Face ID**, the Credential Provider requests authentication from `FaceUnlock.Service`.
   - Upon receiving Face ID approval from the iPhone, `FaceUnlock.Service` verifies the ECDSA signature against the pinned device public key and grants a short-lived, one-time in-memory approval token.
   - The tile status is dynamically updated (`"Face ID approved"` / `"Face ID rejected"` / `"FaceUnlock request timed out"` / `"FaceUnlock Service is not running"`).
   - In Phase B, `GetSerialization()` returns `CPGSR_NO_CREDENTIAL_FINISHED` without performing real Windows credential serialization (which is scheduled for Phase C).

4. **Zero Impact on Default Sign-in Options**:
   - Standard Windows Password and PIN providers are preserved 100% untouched.

## Scripts

- **`Register.ps1`**: Registers the COM `InprocServer32` CLSID and activates the Credential Provider in Windows Logon UI.
- **`Unregister.ps1`**: Unregisters the Credential Provider and restores default provider views.
- **`Recovery.ps1`**: Emergency recovery script that instantly unregisters the Credential Provider and stops `FaceUnlock Service`.
