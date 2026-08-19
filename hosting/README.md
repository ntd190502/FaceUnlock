# FaceUnlock shared-hosting backend

See `../docs/INSTALL_HOSTING.md`.

Public document root should be `hosting/public`. The backend uses only standard PHP extensions commonly available on shared hosting: PDO MySQL, cURL, OpenSSL.

APNs requires outbound HTTPS/443 and cURL HTTP/2. If a hosting provider blocks outbound connections or lacks HTTP/2 support, push delivery will not work; BLE fallback is unaffected.
