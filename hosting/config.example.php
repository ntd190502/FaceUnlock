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
    'telegram' => [
        'bot_token' => 'YOUR_TELEGRAM_BOT_TOKEN',
        'chat_id' => 'YOUR_TELEGRAM_CHAT_ID',
    ],
];
