# Install hosting

Requirements: PHP 8.1+, PDO MySQL, OpenSSL, cURL, HTTPS certificate, MySQL/MariaDB.

1. Create a MySQL database and import `hosting/schema.sql`.
2. Copy `hosting/config.example.php` to `hosting/config.php`.
3. Fill database credentials, an HTTPS `base_url`, and Telegram bot/chat credentials.
4. For an existing deployment, apply unapplied files in `hosting/migrations/` once in filename order.
5. Point the site/subdirectory document root to `hosting/public/`, or use the provided `.htaccess` layout.
6. Open `/health` and expect JSON `{"ok":true}`.

For development without Apache rewrite:

```bash
php -S 127.0.0.1:8080 hosting/public/router.php
```

Never commit `config.php` or the Telegram bot token.

## V2 database upgrade

Back up the production database, deploy the application, then run
`php hosting/scripts/migrate.php` once from the hosting CLI. The migration tracks
versions in `schema_migrations`; it preserves existing device IDs and validates the
number of migrated legacy pairings before dropping `devices.pc_id`. Do not manually
import `schema.sql` over an existing installation.

Configure an HTTPS `base_url`, rate-limit values, retention values, and an optional
SHA-256 admin bearer token in `config.php`. Schedule the documented cleanup command
if cron is available; request expiration remains safe without cron.
