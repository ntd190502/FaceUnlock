# Install hosting

Requirements: PHP 8.1+, PDO MySQL, OpenSSL, cURL with HTTP/2 support, HTTPS certificate, MySQL/MariaDB.

1. Create a MySQL database and import `hosting/schema.sql`.
2. Copy `hosting/config.example.php` to `hosting/config.php`.
3. Fill database credentials.
4. Create an APNs Auth Key (`.p8`) in the Apple Developer portal and set Team ID, Key ID and app bundle ID.
5. Place the `.p8` file outside the public web root when possible and set `APNS_P8_PATH` accordingly.
6. Point the site/subdirectory document root to `hosting/public/`, or use the provided `.htaccess` layout.
7. Open `/health` and expect JSON `{"ok":true}`.

For development without Apache rewrite:

```bash
php -S 127.0.0.1:8080 hosting/public/router.php
```

Never commit `config.php` or the APNs `.p8` key.
