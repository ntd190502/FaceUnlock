# Security model

1. **No Windows password on hosting or iPhone.**
2. **Short-lived challenges.** Unlock sessions expire quickly (default 90 seconds) and approval is tied to session ID, challenge, PC ID, and expiry.
3. **iPhone signing key.** ECDSA P-256 is generated in Secure Enclave when supported. On environments where Secure Enclave creation fails, the implementation falls back to a non-exported Keychain `SecKey` reference rather than pretending hardware protection exists.
4. **Face ID is explicit.** LocalAuthentication biometric verification runs before an online/offline approval signature is generated.
5. **PC identity pinning on iPhone.** The pairing QR contains PC ID and PC public key. iOS rejects pair completion if the server returns a different PC ID or PC public key and stores the key from the scanned QR.
6. **Protected bearer-token persistence.** Windows stores the PC bearer token through DPAPI; iOS stores the device bearer token in Keychain. Legacy plaintext token migration removes the old iOS UserDefaults field after it is copied into Keychain.
7. **Hosting token storage.** Server-side bearer and short-lived approval tokens are random; only their SHA-256 hashes are persisted in MySQL. Approval URLs carry no PC/device bearer token and require HTTPS.
8. **Explicit device targeting and revocation.** Online sessions are bound to a selected device ID. Device-authenticated routes reject revoked device tokens.
9. **Server + Windows signature verification.** Hosting verifies the iPhone approval before setting `APPROVED`; Windows verifies the returned signature again using the public key associated with that target device.
10. **Signed offline requests.** PC -> iPhone BLE/QR requests carry a PC P-256 signature; iOS verifies the pinned PC public key before Face ID.
11. **Targeted BLE discovery.** Windows reads the Device ID GATT characteristic and fails closed if the expected device identity is missing, unreadable, or different.
12. **Bounded BLE framing.** Multi-packet BLE messages use framing with a 16 KiB cap and 15-second incomplete-assembly timeout. Transport framing does not replace cryptographic verification.
13. **Recovery remains Windows-owned.** Built-in Windows PIN/password providers remain available.
14. **Shell input guard is scoped and user-mode.** In Phase F Shell Mode, a low-level keyboard hook blocks common user-mode escape shortcuts only while the gate is locked. It is removed after an approved grant is consumed and during shutdown. Hook installation/removal failures do not release Explorer.
15. **Service-owned per-session gate.** Phase F.2 defaults every eligible interactive SID/session to `LOCKED`. Only consumption of the current Shell request's reserved grant establishes `UNLOCKED`; wrong SID, session, request, process, missing reservation, and replay are rejected.
16. **Mandatory Shell watchdog.** For paired machines with Shell Gate enabled, the SYSTEM service uses the interactive session token to restart a missing Shell and terminates unauthorized Explorer only in that session. Duplicate Shell processes are reduced to one canonical instance with restart backoff.

## Phase F Shell Gate boundary

FaceUnlock Phase F is a post-logon Shell Gate, not an LSA/Winlogon security
boundary. Ctrl+Alt+Del is the Windows Secure Attention Sequence and cannot be
blocked by a user-mode application. Returning from the Secure Attention screen
leaves the Service-owned gate state unchanged. While the Service is active and a
session is `LOCKED`, killing FaceUnlockShell causes a restart and launching
`explorer.exe` from Task Manager causes that Explorer process to be terminated.
FaceUnlock does not patch Winlogon/LSA, disable LSA protection or Windows security
UI, or set a global policy that disables Windows recovery. WinRE, Safe Mode and
offline administrative recovery remain Windows-owned and unchanged.

## Important trust boundary during initial pairing

The iPhone authenticates the PC public key out-of-band because that key is scanned directly from the Windows QR.

The current Windows Agent, however, receives the iPhone public key through the hosting pair-status response. Therefore a hosting service that is already malicious **during initial pairing** could substitute the iPhone public key that Windows records. The statement “hosting can never forge an iPhone approval” is only true after Windows has pinned the correct iPhone key.

For higher-assurance deployments, add an independent local comparison of the iPhone public-key fingerprint during pairing (for example, a second QR/manual fingerprint or an authenticated local channel).

## Threats not solved by this phase

- Malware already running as SYSTEM on the Windows PC.
- A compromised/untrusted jailbroken iPhone.
- Compromise of the hosting service during initial Windows-to-iPhone trust establishment, as described above.
- Telegram account/bot compromise can expose metadata and trigger/open session links, but the link alone cannot create a valid iPhone signature.
- Denial of service or metadata observation by the hosting provider.
- iOS background BLE scheduling/termination is controlled by iOS; QR/foreground fallback remains necessary.
- FaceUnlock deliberately ships no Credential Provider or custom authentication package. Phone approval authorizes only the post-logon Shell Gate, not generic Windows/LSA passwordless logon.

## Retired stack migration

`Cleanup-PhaseE.ps1` is migration-only. It removes only the historical FaceUnlock
CLSID and `FaceUnlockAuthPackage` values, verifies `msv1_0` remains registered,
preserves unrelated authentication/security packages and pairing state, schedules
locked legacy DLL deletion for the next reboot, and never requests a reboot itself.
