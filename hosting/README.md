# FaceUnlock shared-hosting backend

See `../docs/INSTALL_HOSTING.md`.

Public document root should be `hosting/public`. The backend uses only standard PHP extensions commonly available on shared hosting: PDO MySQL, cURL, OpenSSL.

Telegram notifications contain a plain `base_url/u/<opaque-token>` HTTPS link. The
database stores only the token hash, and no Telegram inline keyboard or callback
webhook is required. Outbound HTTPS/443 access to the Telegram Bot API is required;
BLE fallback is unaffected if notification delivery fails.

For an existing database, apply each file in `migrations/` once in filename order.
