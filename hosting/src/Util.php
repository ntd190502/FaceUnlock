<?php
final class Util {
    public static function jsonBody(): array {
        $raw = file_get_contents('php://input') ?: '{}';
        $data = json_decode($raw, true);
        if (!is_array($data)) self::error('invalid_json', 400);
        return $data;
    }
    public static function out(array $data, int $status = 200): never {
        http_response_code($status); header('Content-Type: application/json; charset=utf-8');
        echo json_encode($data, JSON_UNESCAPED_SLASHES); exit;
    }
    public static function error(string $message, int $status = 400): never { self::out(['ok'=>false,'error'=>$message], $status); }
    public static function id(int $bytes = 18): string { return rtrim(strtr(base64_encode(random_bytes($bytes)), '+/', '-_'), '='); }
    public static function token(int $bytes = 32): string { return rtrim(strtr(base64_encode(random_bytes($bytes)), '+/', '-_'), '='); }
    public static function bearer(): ?string {
        $h = $_SERVER['HTTP_AUTHORIZATION'] ?? '';
        return preg_match('/^Bearer\s+(.+)$/i', $h, $m) ? trim($m[1]) : null;
    }
    public static function canonical(array $s): string {
        return 'faceunlock-v1|'.$s['id'].'|'.$s['challenge'].'|'.$s['pc_id'].'|'.$s['expires_at'];
    }
}
