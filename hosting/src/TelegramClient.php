<?php
declare(strict_types=1);

final class TelegramClient {
    private string $botToken;
    private string $chatId;
    private string $baseUrl;

    public function __construct(array $cfg, string $baseUrl) {
        $this->botToken = trim((string)($cfg['bot_token'] ?? ''));
        $this->chatId = trim((string)($cfg['chat_id'] ?? ''));
        $this->baseUrl = rtrim(trim($baseUrl), '/');
    }

    public function sendUnlock(string $sessionId, string $pcName, int $expiresAt): array {
        if ($this->botToken === '' || $this->chatId === '') {
            throw new RuntimeException('Telegram bot_token/chat_id is not configured');
        }
        if ($this->baseUrl === '') {
            throw new RuntimeException('base_url is not configured');
        }

        $openUrl = $this->baseUrl . '/telegram/open/' . rawurlencode($sessionId);
        $remaining = max(0, $expiresAt - time());

        $text = "🔐 FaceUnlock\n\n"
              . "Máy tính: " . $pcName . "\n"
              . "đang yêu cầu xác thực Face ID.\n\n"
              . "Yêu cầu hết hạn sau khoảng " . $remaining . " giây.";

        $payload = [
            'chat_id' => $this->chatId,
            'text' => $text,
            'disable_web_page_preview' => true,
            'reply_markup' => [
                'inline_keyboard' => [
                    [
                        ['text' => '🔓 Mở FaceUnlock', 'url' => $openUrl]
                    ]
                ]
            ],
        ];

        $url = 'https://api.telegram.org/bot' . $this->botToken . '/sendMessage';
        $ch = curl_init($url);
        if ($ch === false) {
            throw new RuntimeException('Could not initialize cURL for Telegram');
        }

        curl_setopt_array($ch, [
            CURLOPT_POST => true,
            CURLOPT_POSTFIELDS => json_encode($payload, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES),
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_CONNECTTIMEOUT => 5,
            CURLOPT_TIMEOUT => 12,
            CURLOPT_HTTPHEADER => ['Content-Type: application/json'],
        ]);

        $body = curl_exec($ch);
        $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        $curlError = curl_error($ch);
        curl_close($ch);

        if ($body === false) {
            throw new RuntimeException('Telegram network error: ' . $curlError);
        }

        $decoded = json_decode($body, true);
        if ($status < 200 || $status >= 300 || !is_array($decoded) || empty($decoded['ok'])) {
            $description = is_array($decoded) ? (string)($decoded['description'] ?? 'Telegram API error') : 'Invalid Telegram response';
            throw new RuntimeException('Telegram HTTP ' . $status . ': ' . $description);
        }

        return [
            'ok' => true,
            'message_id' => $decoded['result']['message_id'] ?? null,
            'open_url' => $openUrl,
        ];
    }
}
