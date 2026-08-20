<?php
declare(strict_types=1);

$configPath = dirname(__DIR__).'/config.php';
if (!is_file($configPath)) {
    http_response_code(500);
    die('Missing hosting/config.php');
}
$config = require $configPath;

foreach (['Util','Database','Auth','Crypto','ApprovalLink','TelegramClient'] as $f) {
    require dirname(__DIR__).'/src/'.$f.'.php';
}

$db = new Database($config['db']);
$auth = new Auth($db);
$telegram = new TelegramClient($config['telegram'] ?? []);

$method = $_SERVER['REQUEST_METHOD'];
$path = parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH) ?: '/';

// Support installation in a subdirectory by stripping everything before
// /v1, /health or the opaque approval-link route.
if (($p = strpos($path, '/v1/')) !== false) {
    $path = substr($path, $p);
} elseif (($p = strpos($path, '/health')) !== false) {
    $path = '/health';
} elseif (($p = strpos($path, '/u/')) !== false) {
    $path = substr($path, $p);
}

if ($method === 'GET' && $path === '/health') {
    Util::out([
        'ok' => true,
        'time' => time(),
        'notification_provider' => 'telegram',
    ]);
}

// Plain Telegram HTTPS link -> validated landing page -> FaceUnlock URL scheme.
if ($method === 'GET' && preg_match('#^/u/([^/]+)$#', $path, $m)) {
    $approvalToken = rawurldecode($m[1]);
    $s = null;
    if (ApprovalLink::isValidToken($approvalToken)) {
        $s = $db->one(
            'SELECT s.id,s.status,s.expires_at,p.name pc_name
         FROM unlock_sessions s
         JOIN pcs p ON p.id=s.pc_id
         WHERE s.approval_token_hash=?',
            [ApprovalLink::hashToken($approvalToken)]
        );
    }
    unset($approvalToken);

    header('Content-Type: text/html; charset=utf-8');
    header('Cache-Control: no-store, no-cache, must-revalidate, max-age=0');
    header('Referrer-Policy: no-referrer');
    header('X-Content-Type-Options: nosniff');

    $linkState = ApprovalLink::state($s, time());
    if ($linkState === 'INVALID') {
        http_response_code(404);
        echo '<!doctype html><meta charset="utf-8"><title>FaceUnlock</title>'
           . '<body style="font-family:-apple-system,BlinkMacSystemFont,Segoe UI,sans-serif;background:#0f172a;color:#fff;padding:32px">'
           . '<h2>FaceUnlock</h2><p>Invalid or expired FaceUnlock request.</p></body>';
        exit;
    }

    if ($linkState === 'EXPIRED') {
        $db->exec("UPDATE unlock_sessions SET status='EXPIRED' WHERE id=? AND status='PENDING'", [$s['id']]);
        http_response_code(410);
        echo '<!doctype html><meta charset="utf-8"><title>FaceUnlock</title>'
           . '<body style="font-family:-apple-system,BlinkMacSystemFont,Segoe UI,sans-serif;background:#0f172a;color:#fff;padding:32px">'
           . '<h2>FaceUnlock</h2><p>Invalid or expired FaceUnlock request.</p></body>';
        exit;
    }

    if ($linkState === 'COMPLETED') {
        http_response_code(410);
        echo '<!doctype html><meta charset="utf-8"><title>FaceUnlock</title>'
           . '<body style="font-family:-apple-system,BlinkMacSystemFont,Segoe UI,sans-serif;background:#0f172a;color:#fff;padding:32px">'
           . '<h2>FaceUnlock</h2><p>Already approved / request completed.</p></body>';
        exit;
    }

    $deepLink = 'faceunlock://session?id=' . rawurlencode((string)$s['id']);

    header('Content-Type: text/html; charset=utf-8');
    header('Cache-Control: no-store, no-cache, must-revalidate, max-age=0');

    $deepLinkJson = json_encode(
        $deepLink,
        JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES
    );
    $deepLinkHtml = htmlspecialchars(
        $deepLink,
        ENT_QUOTES | ENT_SUBSTITUTE,
        'UTF-8'
    );
    $pcNameHtml = htmlspecialchars(
        (string)$s['pc_name'],
        ENT_QUOTES | ENT_SUBSTITUTE,
        'UTF-8'
    );

    echo '<!doctype html>
<html lang="vi">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<title>FaceUnlock</title>
<style>
html,body{margin:0;background:#0f172a;color:#fff;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif}
.wrap{max-width:520px;margin:0 auto;padding:48px 22px;text-align:center}
a{display:block;margin-top:20px;padding:14px 18px;border-radius:12px;background:#2563eb;color:#fff;text-decoration:none;font-weight:700}
p{color:#cbd5e1}
</style>
</head>
<body>
<div class="wrap">
<h2>Đang mở FaceUnlock...</h2>
<p>Yêu cầu mở khóa: <strong>' . $pcNameHtml . '</strong></p>
<p>Nếu ứng dụng không tự mở, bấm nút bên dưới.</p>
<a id="openApp" href="' . $deepLinkHtml . '">Mở FaceUnlock</a>
</div>
<script>
(function () {
    var target = ' . $deepLinkJson . ';
    // Try immediately.
    window.location.replace(target);

    // Retry once for Telegram in-app browser / Safari hand-off.
    setTimeout(function () {
        window.location.href = target;
    }, 500);
})();
</script>
</body>
</html>';
    exit;
}

if ($method === 'POST' && $path === '/v1/pair/start') {
    $b = Util::jsonBody();
    foreach (['pc_id','pc_name','pc_public_key_pem'] as $k) {
        if (empty($b[$k])) Util::error('missing_'.$k);
    }

    $pcToken = Util::token();
    $pairId = Util::id();
    $code = str_pad((string)random_int(0,999999), 6, '0', STR_PAD_LEFT);
    $exp = time() + ($config['pair_ttl'] ?? 600);

    $db->exec(
        'INSERT INTO pcs(id,name,public_key_pem,token_hash)
         VALUES(?,?,?,?)
         ON DUPLICATE KEY UPDATE
           name=VALUES(name),
           public_key_pem=VALUES(public_key_pem),
           token_hash=VALUES(token_hash)',
        [$b['pc_id'],$b['pc_name'],$b['pc_public_key_pem'],hash('sha256',$pcToken)]
    );

    $db->exec(
        'INSERT INTO pairings(id,pc_id,pair_code,expires_at) VALUES(?,?,?,?)',
        [$pairId,$b['pc_id'],$code,$exp]
    );

    Util::out([
        'ok'=>true,
        'pair_id'=>$pairId,
        'pair_code'=>$code,
        'expires_at'=>$exp,
        'pc_token'=>$pcToken,
    ]);
}

if ($method === 'POST' && $path === '/v1/pair/complete') {
    $b = Util::jsonBody();
    foreach (['pair_id','pair_code','iphone_name','iphone_public_key_pem'] as $k) {
        if (!isset($b[$k])) Util::error('missing_'.$k);
    }

    $pair = $db->one(
        'SELECT p.*,pc.name pc_name,pc.public_key_pem pc_public_key_pem
         FROM pairings p
         JOIN pcs pc ON pc.id=p.pc_id
         WHERE p.id=?',
        [$b['pair_id']]
    );

    if (
        !$pair ||
        !hash_equals($pair['pair_code'], (string)$b['pair_code']) ||
        (int)$pair['expires_at'] < time() ||
        $pair['completed_device_id']
    ) {
        Util::error('invalid_or_expired_pair', 403);
    }

    $deviceId = Util::id();
    $deviceToken = Util::token();

    // Keep legacy DB column for compatibility; Telegram does not use APNs token.
    $legacyApnsToken = (string)($b['apns_token'] ?? '');

    $db->exec(
        'INSERT INTO devices(id,pc_id,name,public_key_pem,apns_token,token_hash)
         VALUES(?,?,?,?,?,?)',
        [
            $deviceId,
            $pair['pc_id'],
            $b['iphone_name'],
            $b['iphone_public_key_pem'],
            $legacyApnsToken,
            hash('sha256',$deviceToken)
        ]
    );

    $db->exec(
        'UPDATE pairings SET completed_device_id=? WHERE id=?',
        [$deviceId,$pair['id']]
    );

    Util::out([
        'ok'=>true,
        'device_id'=>$deviceId,
        'device_api_token'=>$deviceToken,
        'pc_id'=>$pair['pc_id'],
        'pc_name'=>$pair['pc_name'],
        'pc_public_key_pem'=>$pair['pc_public_key_pem'],
    ]);
}

if ($method === 'GET' && preg_match('#^/v1/pair/status/([^/]+)$#', $path, $m)) {
    $pc = $auth->pc();
    $pair = $db->one(
        'SELECT * FROM pairings WHERE id=? AND pc_id=?',
        [$m[1],$pc['id']]
    );
    if (!$pair) Util::error('not_found',404);

    if (!$pair['completed_device_id']) {
        Util::out(['ok'=>true,'paired'=>false]);
    }

    $d = $db->one(
        'SELECT id,name,public_key_pem FROM devices WHERE id=?',
        [$pair['completed_device_id']]
    );

    Util::out(['ok'=>true,'paired'=>true,'device'=>$d]);
}


if ($method === 'POST' && preg_match('#^/v1/devices/([^/]+)/revoke$#', $path, $m)) {
    $pc = $auth->pc();
    $deviceId = rawurldecode($m[1]);
    $device = $db->one('SELECT * FROM devices WHERE id=? AND pc_id=?', [$deviceId, $pc['id']]);
    if (!$device) Util::error('device_not_found', 404);
    $db->exec('UPDATE devices SET revoked_at=NOW() WHERE id=? AND pc_id=?', [$deviceId, $pc['id']]);
    Util::out(['ok'=>true,'revoked'=>true,'device_id'=>$deviceId]);
}

if ($method === 'POST' && $path === '/v1/unlock/request') {
    $pc = $auth->pc();
    $b = Util::jsonBody();

    if (!empty($b['device_id'])) {
        $device = $db->one(
            'SELECT * FROM devices WHERE id=? AND pc_id=? AND revoked_at IS NULL',
            [(string)$b['device_id'], $pc['id']]
        );
        if (!$device) Util::error('invalid_or_revoked_device',404);
    } else {
        $device = $db->one(
            'SELECT * FROM devices WHERE pc_id=? AND revoked_at IS NULL ORDER BY created_at DESC LIMIT 1',
            [$pc['id']]
        );
        if (!$device) Util::error('no_paired_device',409);
    }

    $id = Util::id();
    $challenge = Util::token(32);
    $approvalToken = ApprovalLink::createToken();
    $approvalTokenHash = ApprovalLink::hashToken($approvalToken);
    $exp = time() + ($config['unlock_ttl'] ?? 90);

    $db->exec(
        'INSERT INTO unlock_sessions(id,pc_id,device_id,challenge,approval_token_hash,expires_at)
         VALUES(?,?,?,?,?,?)',
        [$id,$pc['id'],$device['id'],$challenge,$approvalTokenHash,$exp]
    );

    try {
        $approvalUrl = ApprovalLink::buildUrl((string)($config['base_url'] ?? ''), $approvalToken);
        $telegram->sendUnlockNotification($pc['name'], $approvalUrl, $exp);
        $sent = true;
        $notifyError = null;
    } catch (Throwable $e) {
        $sent = false;
        $notifyError = $e->getMessage();
    }
    unset($approvalToken, $approvalTokenHash, $approvalUrl);

    // Keep old field names so the current Windows Agent remains compatible.
    Util::out([
        'ok'=>true,
        'session_id'=>$id,
        'challenge'=>$challenge,
        'expires_at'=>$exp,
        'device_id'=>$device['id'],
        'push_sent'=>$sent,
        'push_error'=>$notifyError,
        'notification_provider'=>'telegram',
        'notification_sent'=>$sent,
        'notification_error'=>$notifyError,
    ]);
}


if ($method === 'GET' && $path === '/v1/unlock/pending') {
    $dev = $auth->device();
    $s = $db->one(
        "SELECT id FROM unlock_sessions
         WHERE device_id=? AND status='PENDING' AND expires_at >= ?
         ORDER BY created_at DESC LIMIT 1",
        [$dev['id'], time()]
    );
    Util::out([
        'ok'=>true,
        'pending'=>(bool)$s,
        'session_id'=>$s['id'] ?? null,
    ]);
}

if ($method === 'GET' && preg_match('#^/v1/unlock/session/([^/]+)$#', $path, $m)) {
    $dev = $auth->device();

    $s = $db->one(
        'SELECT s.*,p.name pc_name
         FROM unlock_sessions s
         JOIN pcs p ON p.id=s.pc_id
         WHERE s.id=? AND s.device_id=?',
        [$m[1],$dev['id']]
    );
    if (!$s) Util::error('not_found',404);

    if ((int)$s['expires_at'] < time() && $s['status'] === 'PENDING') {
        $db->exec("UPDATE unlock_sessions SET status='EXPIRED' WHERE id=?",[$s['id']]);
        $s['status'] = 'EXPIRED';
    }

    Util::out([
        'session_id'=>$s['id'],
        'challenge'=>$s['challenge'],
        'pc_id'=>$s['pc_id'],
        'pc_name'=>$s['pc_name'],
        'expires_at'=>(int)$s['expires_at'],
        'status'=>$s['status'],
    ]);
}

if ($method === 'POST' && preg_match('#^/v1/unlock/approve/([^/]+)$#', $path, $m)) {
    $dev = $auth->device();
    $b = Util::jsonBody();

    $s = $db->one(
        'SELECT * FROM unlock_sessions WHERE id=? AND device_id=?',
        [$m[1],$dev['id']]
    );
    if (!$s) Util::error('not_found',404);

    if ($s['status'] !== 'PENDING' || (int)$s['expires_at'] < time()) {
        Util::error('session_not_pending',409);
    }

    if (
        empty($b['signature']) ||
        !Crypto::verify($dev['public_key_pem'], Util::canonical($s), $b['signature'])
    ) {
        Util::error('bad_signature',403);
    }

    $updated = $db->exec(
        "UPDATE unlock_sessions
         SET status='APPROVED',signature_b64=?,biometric=?,approved_at=NOW()
         WHERE id=? AND status='PENDING' AND expires_at>=?",
        [$b['signature'],$b['biometric']??'unknown',$s['id'],time()]
    );
    if ($updated !== 1) Util::error('session_not_pending',409);

    Util::out(['ok'=>true]);
}

if ($method === 'POST' && preg_match('#^/v1/unlock/reject/([^/]+)$#', $path, $m)) {
    $dev = $auth->device();
    $db->exec(
        "UPDATE unlock_sessions
         SET status='REJECTED'
         WHERE id=? AND device_id=? AND status='PENDING'",
        [$m[1],$dev['id']]
    );
    Util::out(['ok'=>true]);
}

if ($method === 'GET' && preg_match('#^/v1/unlock/status/([^/]+)$#', $path, $m)) {
    $pc = $auth->pc();

    $s = $db->one(
        'SELECT s.*,d.public_key_pem device_public_key_pem
         FROM unlock_sessions s
         JOIN devices d ON d.id=s.device_id
         WHERE s.id=? AND s.pc_id=?',
        [$m[1],$pc['id']]
    );
    if (!$s) Util::error('not_found',404);

    if ((int)$s['expires_at'] < time() && $s['status'] === 'PENDING') {
        $db->exec("UPDATE unlock_sessions SET status='EXPIRED' WHERE id=?",[$s['id']]);
        $s['status'] = 'EXPIRED';
    }

    Util::out([
        'ok'=>true,
        'session_id'=>$s['id'],
        'status'=>$s['status'],
        'challenge'=>$s['challenge'],
        'expires_at'=>(int)$s['expires_at'],
        'signature'=>$s['signature_b64'],
        'device_public_key_pem'=>$s['device_public_key_pem'],
    ]);
}

Util::error('route_not_found',404);
