<?php
return [
    'db' => [
        'dsn' => 'mysql:host=127.0.0.1;dbname=face;charset=utf8mb4',
        'user' => 'face',
        'pass' => 'your_database_password',
    ],
    'base_url' => 'https://your-domain.example.com',
    'pair_ttl' => 600,
    'unlock_ttl' => 90,
    // Production URL must be HTTPS; approval URLs are opaque locators, not authorization.
    'require_https' => true,
    'rate_limits' => [
        'pair_start' => ['limit'=>10, 'window'=>300], 'pair_complete' => ['limit'=>10, 'window'=>300],
        'unlock_request' => ['limit'=>30, 'window'=>60], 'approval' => ['limit'=>20, 'window'=>60],
        'approval_link' => ['limit'=>60, 'window'=>60], 'revoke' => ['limit'=>20, 'window'=>300],
    ],
    'cleanup' => ['rate_limit_retention_seconds'=>86400, 'audit_retention_days'=>90],
    // Set in production; never commit a real value. Admin routes remain disabled without it.
    'admin' => ['token_hash' => ''],
    'telegram' => [
        'bot_token' => 'YOUR_TELEGRAM_BOT_TOKEN',
        'chat_id' => 'YOUR_TELEGRAM_CHAT_ID',
    ],
];
