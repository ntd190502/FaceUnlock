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
        if (filter_var($approvalUrl, FILTER_VALIDATE_URL) === false || strtolower((string)parse_url($approvalUrl, PHP_URL_SCHEME)) !== 'https') {
            throw new InvalidArgumentException('Telegram approval URL must use HTTPS');
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
        ];
    }
    public function sendAdminTest(): array {
        if ($this->botToken === '' || $this->chatId === '') throw new RuntimeException('Telegram is not configured');
        $url='https://api.telegram.org/bot'.$this->botToken.'/sendMessage';$ch=curl_init($url);if($ch===false)throw new RuntimeException('Could not initialize cURL');
        curl_setopt_array($ch,[CURLOPT_POST=>true,CURLOPT_POSTFIELDS=>json_encode(['chat_id'=>$this->chatId,'text'=>'FaceUnlock Admin test notification']),CURLOPT_RETURNTRANSFER=>true,CURLOPT_TIMEOUT=>12,CURLOPT_HTTPHEADER=>['Content-Type: application/json']]);$body=curl_exec($ch);$status=(int)curl_getinfo($ch,CURLINFO_RESPONSE_CODE);curl_close($ch);if($body===false||$status<200||$status>=300)throw new RuntimeException('Telegram test failed');return ['ok'=>true];
    }
}
