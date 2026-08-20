# Current platform limitations

## Windows passwordless unlock

The iPhone signature is an authentication/approval signal, but the current repository does **not** turn that signal into a complete Windows logon credential. It authorizes the post-logon Phase F/F.2 Shell Gate only; the retired Windows credential stack is not shipped.

A normal Credential Provider must serialize credentials accepted by Windows authentication infrastructure. Do not replace that boundary with an undocumented Winlogon/LSA bypass, and keep the built-in PIN/password providers enabled as the recovery path.

## Telegram & online delivery

Online requests use the Telegram Bot API to send a plain, short-lived HTTPS approval
URL. Telegram renders the URL as clickable text; no inline button or callback is used.
The validated landing page attempts to open `faceunlock://session?id=...`.

- Telegram delivery and the in-app browser are network/client dependent.
- If FaceUnlock is already foregrounded, `/v1/unlock/pending` polling can discover the request without using the Telegram notification.
- If the custom URL hand-off is blocked, the landing page retains a manual **Mở FaceUnlock** action.

## iOS background BLE

iOS controls background CoreBluetooth scheduling and advertising behavior. If the app was force-quit or the phone rebooted, BLE discovery can require foregrounding FaceUnlock again. QR fallback exists for this case.

The BLE transport now uses application-level framing/reassembly to avoid relying on a single GATT write/notification fitting the whole JSON payload. This improves MTU compatibility but does not make iOS background execution guaranteed.

## Initial pairing trust boundary

The iPhone pins the PC identity from the QR payload and rejects a server response whose PC ID/public key does not match. Windows, however, currently learns the iPhone public key through the hosting pair-status response. A hosting service compromised **during initial pairing** could therefore substitute the iPhone public key seen by Windows.

For a higher-assurance deployment, add an out-of-band/local verification of the iPhone public-key fingerprint before treating the pair as trusted.

## Telegram metadata

Telegram receives the PC display name and an HTTPS URL containing an opaque, short-lived
approval token. Only its SHA-256 hash is stored by hosting. Telegram does not receive a
PC/device bearer token, Windows PIN/password, raw session ID in the notification URL,
or private key material.
