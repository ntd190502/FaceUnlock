# FaceUnlock shared-hosting backend

See `../docs/INSTALL_HOSTING.md`.

Public document root should be `hosting/public`. The backend uses only standard PHP extensions commonly available on shared hosting: PDO MySQL, cURL, OpenSSL.

Telegram notifications contain a plain `base_url/u/<opaque-token>` HTTPS link. The
database stores only the token hash, and no Telegram inline keyboard or callback
webhook is required. Outbound HTTPS/443 access to the Telegram Bot API is required;
BLE fallback is unaffected if notification delivery fails.

## Hosting V2 upgrade

Back up the database first. Existing installations must run `php hosting/scripts/migrate.php`
from a trusted CLI account. The runner records ordered versions in `schema_migrations`,
migrates legacy `devices.pc_id` relations into `pc_device_pairings`, verifies the count,
and preserves old unlock history as logical requests. It fails rather than silently
continuing on a pairing-count mismatch.

New installations import `schema.sql`. V2 creates one logical `unlock_requests` row
per PC attempt and one candidate per active pairing; a conditional update makes the
first valid approval the only winner. The opaque Telegram URL is only a locator.

For shared hosting, core operation does not require cron. Run `php hosting/scripts/migrate.php`
on deploy and optionally invoke your cleanup command hourly; lazy expiration is also safe.
`GET /health` is intentionally minimal. `GET /admin` requires a bearer value whose SHA-256
is configured as `admin.token_hash`; it never exposes secrets. Browser users open
`/admin/login` and enter the raw token once; the server stores only a secure, expiring
session. Generate a hash with `php -r "echo hash('sha256','YOUR_LONG_RANDOM_TOKEN'), PHP_EOL;"`
or PowerShell: `[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('YOUR_LONG_RANDOM_TOKEN'))).ToLower()`.
