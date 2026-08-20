# Protocol v1

All timestamps are Unix seconds UTC. All random identifiers use at least 128 bits of randomness unless a transport-only identifier is explicitly described otherwise.

## Canonical signed online approval

UTF-8 bytes of:

```text
faceunlock-v1|<session_id>|<challenge_b64url>|<pc_id>|<expires_at>
```

Signature algorithm: ECDSA P-256 + SHA-256. Signature wire format: ASN.1 DER encoded ECDSA signature, Base64 encoded.

## Pairing QR

JSON:

```json
{
  "type": "faceunlock-pair-v1",
  "server": "https://example.com/faceunlock",
  "pair_id": "...",
  "pair_code": "123456",
  "pc_id": "...",
  "pc_name": "DESKTOP-PC",
  "pc_public_key_pem": "-----BEGIN PUBLIC KEY-----..."
}
```

The iPhone pins `pc_id` and `pc_public_key_pem` from this QR and rejects a pair-complete response if the server returns a different PC identity.

## Offline QR

```json
{
  "type": "faceunlock-offline-v1",
  "session_id": "...",
  "pc_id": "...",
  "pc_name": "DESKTOP-PC",
  "challenge": "base64url",
  "expires_at": 0,
  "pc_signature": "base64-der",
  "logical_request_id": "windows-ipc-request-id",
  "online_session_id": "optional-prior-online-session-id"
}
```

For current Service requests the PC signature covers:

```text
faceunlock-offline-request-v2|session_id|challenge|pc_id|expires_at|logical_request_id|online_session_id
```

The iPhone verifies `pc_signature` with the paired PC public key before showing Face ID.
It may reuse one successful biometric ceremony only when the signed logical or
online-session alias matches. Fresh BLE session/challenge values still require a
fresh transport signature. Legacy v1 payloads remain accepted without cross-session
biometric reuse.

## BLE service

Service UUID: `7A6AF110-8D20-4C5F-BB31-6CECF28F0110`

Characteristics:

- Request write: `7A6AF111-8D20-4C5F-BB31-6CECF28F0110`
- Response notify/read: `7A6AF112-8D20-4C5F-BB31-6CECF28F0110`
- Device ID read: `7A6AF113-8D20-4C5F-BB31-6CECF28F0110`

Windows must read the Device ID characteristic and require an exact match with the selected paired iPhone before sending an offline request. If the characteristic is missing or unreadable, the candidate is rejected.

### BLE framing v1

Offline request/response JSON is carried in an application-level framing layer instead of assuming that one GATT write or notification can hold the whole JSON document.

Frame layout, network byte order:

```text
byte 0..1   magic: 0x46 0x55 ("FU")
byte 2      high nibble = version (1)
            low nibble  = kind (1=request, 2=response)
byte 3..4   message_id UInt16
byte 5..6   chunk_index UInt16, zero based
byte 7..8   chunk_count UInt16
byte 9..N   payload bytes
```

Rules:

- Maximum reassembled message size: 16 KiB.
- Incomplete assemblies expire after 15 seconds.
- Windows request frames default to 20 bytes total so they fit the minimum ATT MTU.
- iOS response frames may be larger, up to the connected central's `maximumUpdateValueLength` (capped by the implementation).
- Chunks may be received more than once; duplicate indexes are ignored.
- A change in `kind` or `chunk_count` for the same `message_id` invalidates that assembly.
- iOS queues response notifications and resumes from `peripheralManagerIsReady(toUpdateSubscribers:)` when CoreBluetooth applies backpressure.
- Receivers still accept the old unframed single-message payload for backward compatibility. A framed request receives a framed response; an unframed request receives an unframed response.

BLE transport is not trusted by itself; both sides still verify the signed offline request/response.
