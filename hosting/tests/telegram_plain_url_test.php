<?php
declare(strict_types=1);

require dirname(__DIR__) . '/src/Util.php';
require dirname(__DIR__) . '/src/ApprovalLink.php';
require dirname(__DIR__) . '/src/TelegramClient.php';

function check(bool $condition, string $message): void {
    if (!$condition) {
        fwrite(STDERR, "FAIL: {$message}\n");
        exit(1);
    }
}

$token = ApprovalLink::createToken();
check(ApprovalLink::isValidToken($token), 'generated token has the required opaque format');
check($token !== ApprovalLink::createToken(), 'tokens are not reused');
check(strlen(ApprovalLink::hashToken($token)) === 64, 'only a SHA-256 token hash is stored');

$approvalUrl = ApprovalLink::buildUrl('https://example.com/faceunlock/', $token);
check($approvalUrl === 'https://example.com/faceunlock/u/' . $token, 'approval URL uses base_url and /u/<token>');

$httpRejected = false;
try {
    ApprovalLink::buildUrl('http://example.com', $token);
} catch (RuntimeException) {
    $httpRejected = true;
}
check($httpRejected, 'HTTP approval URL is rejected');

$telegram = new TelegramClient(['bot_token' => 'test', 'chat_id' => '123']);
$payload = $telegram->buildUnlockNotification('DESKTOP-PC', $approvalUrl, time() + 90);
check(($payload['chat_id'] ?? null) === '123', 'Telegram chat is selected');
check(str_contains((string)($payload['text'] ?? ''), $approvalUrl), 'plain URL is present in message text');
check(!array_key_exists('reply_markup', $payload), 'Telegram payload has no reply markup');
check(!array_key_exists('parse_mode', $payload), 'Telegram payload does not rely on Markdown parsing');

$now = time();
check(ApprovalLink::state(null, $now) === 'INVALID', 'unknown token is invalid');
check(ApprovalLink::state(['status' => 'PENDING', 'expires_at' => $now + 1], $now) === 'VALID', 'pending unexpired token is valid');
check(ApprovalLink::state(['status' => 'PENDING', 'expires_at' => $now - 1], $now) === 'EXPIRED', 'expired token is rejected');
check(ApprovalLink::state(['status' => 'APPROVED', 'expires_at' => $now + 1], $now) === 'COMPLETED', 'approved token cannot replay');
check(ApprovalLink::state(['status' => 'APPROVED', 'expires_at' => $now - 1], $now) === 'INVALID', 'completed token also expires');
foreach (['REJECTED', 'EXPIRED', 'CANCELLED'] as $status) {
    check(ApprovalLink::state(['status' => $status, 'expires_at' => $now + 1], $now) === 'INVALID', "{$status} token is rejected");
}

$runtime = file_get_contents(dirname(__DIR__) . '/src/TelegramClient.php') ?: '';
$runtime .= file_get_contents(dirname(__DIR__) . '/public/index.php') ?: '';
foreach (['reply_' . 'markup', 'inline_' . 'keyboard', 'callback_' . 'data', 'callback_' . 'query', 'answerCallback' . 'Query'] as $removedTerm) {
    check(!str_contains($runtime, $removedTerm), "runtime has no {$removedTerm}");
}
check(str_contains($runtime, 'approval_token_hash'), 'runtime resolves the token hash');
check(str_contains($runtime, "expires_at>=?"), 'approval update rechecks expiry atomically');
check(!str_contains($runtime, '/telegram/' . 'open/'), 'predictable legacy session URL is removed');

echo "Telegram plain approval URL tests PASS\n";
