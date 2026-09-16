# FaceUnlock Shared-Hosting Backend (PHP 8 + MySQL)

Lightweight REST API backend for **FaceUnlock**, managing pairing ceremonies, device tokens, and short-lived online unlock requests via Telegram Bot notifications.

---

## Core Features

- **Lean Authentication API**: Exclusively provides pairing (`/v1/pair/*`) and unlock approval (`/v1/unlock/*`) routes. Redundant remote controls and file hosting modules have been fully eliminated.
- **Telegram Bot Integration**: Generates single-use, short-lived HTTPS links (`base_url/u/<token>`) and dispatches them via standard Telegram Bot API.
- **Hardware-Enforced Security**: The server only verifies and logs cryptographic signatures; it never stores Windows PINs, passwords, or device private keys.
- **Zero Heavy Dependencies**: Runs on standard shared hosting environments with PHP 8.1+ and MySQL/MariaDB (requires `pdo_mysql`, `curl`, `openssl`).

---

## Installation & Deployment

1. **Document Root**: Configure web server (Apache/Nginx/aaPanel) with root pointed to `hosting/public/`.
2. **Database Setup**: Import `hosting/schema.sql` into your MySQL/MariaDB database.
3. **Configuration**: Copy `hosting/config.example.php` to `hosting/config.php` and configure:
   - Database credentials (`dsn`, `user`, `pass`)
   - `base_url`: Must match your publicly accessible domain or IP (e.g. `http://13.215.208.0:8084`)
   - Telegram credentials (`bot_token`, `chat_id`)
4. **Permissions**: Ensure the web server user (`www`) has read access to `src/` and read/write access to `storage/`.

---

## API Endpoints Overview

| Method | Endpoint | Description |
| :--- | :--- | :--- |
| `POST` | `/v1/pair/start` | Windows Agent initiates pairing; generates short-lived pairing code & QR payload. |
| `POST` | `/v1/pair/complete` | iPhone registers device public key with pairing code. |
| `POST` | `/v1/unlock/request` | Windows initiates an online unlock request for the paired device. |
| `GET` | `/v1/unlock/status/{session}` | Windows polls for biometric authorization state. |
| `POST` | `/v1/unlock/approve/{session}` | iPhone submits Face ID cryptographic signature to approve desktop unlock. |
| `GET` | `/u/{token}` | Short redirect endpoint mapping opaque Telegram link to the FaceUnlock app. |
