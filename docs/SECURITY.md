# Security model

1. **No Windows password on hosting or iPhone.**
2. **Short-lived challenges.** Unlock sessions expire quickly (default 90 seconds) and approval is tied to session ID, challenge, PC ID, and expiry.
3. **iPhone signing key.** ECDSA P-256 is generated in Secure Enclave when supported. On environments where Secure Enclave creation fails, the implementation falls back to a non-exported Keychain `SecKey` reference rather than pretending hardware protection exists.
4. **Face ID is explicit.** LocalAuthentication biometric verification runs before an online/offline approval signature is generated.
5. **PC identity pinning on iPhone.** The pairing QR contains PC ID and PC public key. iOS rejects pair completion if the server returns a different PC ID or PC public key and stores the key from the scanned QR.
6. **Protected bearer-token persistence.** Windows stores the PC bearer token through DPAPI; iOS stores the device bearer token in Keychain. Legacy plaintext token migration removes the old iOS UserDefaults field after it is copied into Keychain.
7. **Hosting token storage.** Server-side bearer tokens are random and only SHA-256 token hashes are persisted in MySQL.
8. **Explicit device targeting and revocation.** Online sessions are bound to a selected device ID. Device-authenticated routes reject revoked device tokens.
9. **Server + Windows signature verification.** Hosting verifies the iPhone approval before setting `APPROVED`; Windows verifies the returned signature again using the public key associated with that target device.
10. **Signed offline requests.** PC -> iPhone BLE/QR requests carry a PC P-256 signature; iOS verifies the pinned PC public key before Face ID.
11. **Targeted BLE discovery.** Windows reads the Device ID GATT characteristic and fails closed if the expected device identity is missing, unreadable, or different.
12. **Bounded BLE framing.** Multi-packet BLE messages use framing with a 16 KiB cap and 15-second incomplete-assembly timeout. Transport framing does not replace cryptographic verification.
13. **Recovery remains Windows-owned.** Built-in Windows PIN/password providers remain available.

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
- The current Credential Provider is a scaffold. Phone approval is not itself a generic Windows/LSA passwordless credential.
