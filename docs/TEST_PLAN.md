# Test plan

## A. Hosting Smoke Test
- `/health` returns 200 with `notification_provider = telegram`.
- Pair start returns pair ID, 6-digit code, and PC token.
- Expired pair code is rejected (403).
- Revoke device (`POST /v1/devices/{device_id}/revoke`) successfully marks `revoked_at = NOW()`.
- A revoked device token cannot use device-authenticated routes.

## B. iPhone Pairing & Pinning
- PC creates pair QR containing PC ID, PC Name, and PC Public Key PEM.
- iPhone scans QR and calls `POST /v1/pair/complete`.
- iOS client validates that server response matches scanned PC ID and Public Key PEM.
- Device API token is securely saved in Keychain and removed from legacy UserDefaults JSON during migration.
- Device public key appears on Windows Agent.
- Immediately after first pairing, the BLE Device ID characteristic reports the new `device_id` without requiring an app restart.

## C. Online Telegram Notification Unlock
- Windows Agent requests unlock for selected device (`POST /v1/unlock/request` with `device_id`).
- Telegram bot delivers notification message with inline "Mở FaceUnlock" button.
- Tapping button navigates through landing page redirect to `faceunlock://session?id=...`.
- Face ID prompt appears on iPhone.
- On biometric success, iPhone signs canonical challenge with P-256 key and calls `POST /v1/unlock/approve/{id}`.
- Hosting verifies signature and updates status to `APPROVED`.
- Windows polls status, verifies iPhone signature locally, and succeeds.

## D. Foreground App Polling
- iPhone FaceUnlock app is open in foreground.
- Windows sends Online Unlock request.
- Foreground polling (`GET /v1/unlock/pending`) detects session and prompts Face ID automatically without Telegram interaction.
- Successful Face ID produces exactly one approval and does not require a second manual Face ID scan.
- Canceling Face ID keeps manual retry available and does not spam prompts every polling tick.

## E. Multi-Device Management & Revocation
- Pair two separate iPhones (iPhone A and iPhone B).
- Select iPhone A in Windows UI: the unlock session is bound to iPhone A.
- Select iPhone B: the unlock session is bound to iPhone B.
- Revoke iPhone A from Windows: server marks device revoked, and subsequent requests to iPhone A are rejected.
- Windows verifies an approved session with the public key snapshot of the device that created that session, not whichever device is selected later.

## F. Offline BLE, Device Matching & Framing
- With Bluetooth off, verify Windows detects the disabled radio and attempts to turn it on. If Windows denies service radio access, verify the denial is logged and no hardware PASS is recorded.
- Verify BLE discovery uses sequential 9-second scans separated by a 2.5-second rest, preserves the IPC request ID, and refreshes the short-lived signed offline payload for each scan.
- Disconnect/reconnect Internet during an authentication request and verify the service logs the state transition and switches Online -> BLE without spawning a duplicate request.
- Repeat the same `request_id` with identical security bindings and verify the existing pending/terminal result is returned. Repeat it with a changed SID/session/client/PC binding and verify it is rejected.
- Leave the paired iPhone unavailable for 30+ minutes, then bring it into range and verify the existing request ID continues scanning without a second worker. Cancel/Recovery and service shutdown must stop the current scan promptly.
- Disable Internet on PC and iPhone while keeping Bluetooth active.
- Windows scans for FaceUnlock BLE service and verifies Device ID characteristic (`7A6AF113-8D20-4C5F-BB31-6CECF28F0110`).
- If another non-target FaceUnlock iPhone is nearby, Windows scanner skips it and continues scanning.
- If Device ID characteristic is missing or unreadable while a target ID is known, the candidate is rejected (fail closed).
- Windows splits the signed offline request into BLE framing-v1 chunks and writes every chunk with response.
- iPhone reassembles all chunks before JSON decoding and Face ID.
- iPhone splits the signed response into notification chunks.
- Windows waits until all response chunks are received before JSON decoding.
- Test a request larger than one ATT packet and verify the full request/response round trip.
- Duplicate one chunk: reassembly still succeeds.
- Drop one chunk: assembly expires and no approval result is accepted.
- Corrupt framing metadata (`chunk_count`, kind, or version): request/response is rejected.
- Ensure maximum message size (>16 KiB) is rejected.
- Rotate BLE session/challenge values across retries and verify the signed logical request stays constant and iOS biometric prompt count remains exactly one.
- Start Online, drop Internet, then approve over BLE. Verify the original Windows `request_id` is approved and the abandoned/late online result is ignored.
- With Bluetooth initially OFF, verify FaceUnlock enables it and restores OFF only after the final request lease releases. With Bluetooth initially ON, verify it remains ON.
- Run two concurrent owned BLE leases; completing the first must not disable Bluetooth while the second remains active.
- Reject/cancel an owned BLE request and verify Bluetooth is restored OFF. If auto-enable fails, verify no disable is attempted.

## G. CoreBluetooth Backpressure
- Force/observe `updateValue` returning `false` while multiple response chunks are queued.
- Verify iOS resumes transmission from `peripheralManagerIsReady(toUpdateSubscribers:)`.
- Verify Windows receives exactly one complete response after queue recovery.

## H. QR Fallback
- Trigger BLE discovery fallback (or Bluetooth temporarily out of range).
- Windows Agent displays signed offline QR code.
- iPhone scans QR code, verifies PC signature against pinned PC public key, prompts Face ID, and starts BLE advertising.
- Windows reconnects to the exact paired Device ID and receives the cached signed response without a second Face ID prompt.

## I. Security & Replay Protections
- Replaying an expired, rejected, or already approved session does not create a new approval.
- Tampered challenge or modified signature fails verification on both server and client.
- Plaintext PC bearer token is absent from Windows `config.json`.
- Plaintext device bearer token is absent from iOS UserDefaults after migration.
- Built-in Windows PIN/password fallback remains available if phone is offline or out of battery.

## J. Build / CI
- Windows `FaceUnlock.Core`, `FaceUnlock.Agent`, and `FaceUnlock.Service` build Release under .NET 8.
- Hosting PHP files pass `php -l`.
- iOS GitHub Actions build generates an IPA from current sources.
- Static checks confirm no runtime `PushManager`, `ApnsClient`, `aps-environment`, or `remote-notification` references remain.
- Static checks confirm BLE framing codec exists on both iOS and Windows.

## K. Phase F.2 Mandatory Shell Watchdog

- With an eligible session `LOCKED`, remove the modeled Shell process and verify the watchdog requests one restart with backoff and invalidates the old request ID.
- Verify the restarted Shell must register a new request; the old request/grant cannot authorize the desktop.
- Inject Explorer in the locked target session and verify termination is requested. Inject Explorer with another Session ID and verify it is untouched.
- Consume a reserved, approved grant with exact request ID, SID, session and canonical Shell PID; verify the Service changes only that session to `UNLOCKED` before returning `consumed`.
- Verify wrong SID/session/request, approved-but-not-reserved grants, old grants and replay cannot authorize Explorer.
- Verify an unlocked session neither restarts an exited Shell nor terminates Explorer.
- Verify duplicate Shell processes retain one canonical instance and Test Mode/disabled Shell Gate never touches the user's Explorer.
