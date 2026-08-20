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
