<?php
declare(strict_types=1);

final class TelegramClient {
    private string $botToken;
    private string $chatId;

    public function __construct(array $cfg) {
        $this->botToken = trim((string)($cfg['bot_token'] ?? ''));
        $this->chatId = trim((string)($cfg['chat_id'] ?? ''));
    }

    public function buildUnlockNotification(string $pcName, string $approvalUrl, int $expiresAt): array {
        $scheme = strtolower((string)parse_url($approvalUrl, PHP_URL_SCHEME));
        if (filter_var($approvalUrl, FILTER_VALIDATE_URL) === false || !in_array($scheme, ['http', 'https'], true)) {
            throw new InvalidArgumentException('Telegram approval URL must use HTTP or HTTPS');
        }

        $remaining = max(0, $expiresAt - time());
        $text = "🔐 FaceUnlock\n\n"
              . "Yêu cầu mở khóa:\n"
              . "PC: " . $pcName . "\n\n"
              . "Xác nhận:\n"
              . $approvalUrl . "\n\n"
              . "Hết hạn: " . $remaining . " giây";

        return [
            'chat_id' => $this->chatId,
            'text' => $text,
            'disable_web_page_preview' => true,
        ];
    }

    public function sendUnlockNotification(string $pcName, string $approvalUrl, int $expiresAt): array {
        if ($this->botToken === '' || $this->chatId === '') {
            throw new RuntimeException('Telegram bot_token/chat_id is not configured');
        }
        $payload = $this->buildUnlockNotification($pcName, $approvalUrl, $expiresAt);

        $url = 'https://api.telegram.org/bot' . $this->botToken . '/sendMessage';
        $ch = curl_init($url);
        if ($ch === false) {
            throw new RuntimeException('Could not initialize cURL for Telegram');
        }

        curl_setopt_array($ch, [
            CURLOPT_POST => true,
            CURLOPT_POSTFIELDS => json_encode($payload, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES),
            CURLOPT_HTTPHEADER => ['Content-Type: application/json; charset=utf-8'],
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_CONNECTTIMEOUT => 5,
            CURLOPT_TIMEOUT => 10,
        ]);

        $res = curl_exec($ch);
        $err = curl_error($ch);
        $code = (int)curl_getinfo($ch, CURLINFO_HTTP_CODE);
        curl_close($ch);

        if ($res === false) {
            throw new RuntimeException('Telegram cURL error: ' . $err);
        }
        $data = json_decode((string)$res, true);
        if ($code !== 200 || !is_array($data) || empty($data['ok'])) {
            throw new RuntimeException('Telegram API error (' . $code . '): ' . (string)$res);
        }

        return $data;
    }

    public function sendAdminTest(): array {
        if ($this->botToken === '' || $this->chatId === '') {
            throw new RuntimeException('Telegram bot_token/chat_id is not configured');
        }
        $url = 'https://api.telegram.org/bot' . $this->botToken . '/sendMessage';
        $ch = curl_init($url);
        if ($ch === false) throw new RuntimeException('Could not initialize cURL');
        $payload = ['chat_id' => $this->chatId, 'text' => "✅ FaceUnlock: Telegram notification test successful.", 'disable_web_page_preview' => true];
        curl_setopt_array($ch, [
            CURLOPT_POST => true,
            CURLOPT_POSTFIELDS => json_encode($payload, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES),
            CURLOPT_HTTPHEADER => ['Content-Type: application/json; charset=utf-8'],
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_CONNECTTIMEOUT => 5,
            CURLOPT_TIMEOUT => 10,
        ]);
        $res = curl_exec($ch);
        curl_close($ch);
        return json_decode((string)$res, true) ?: [];
    }
}
