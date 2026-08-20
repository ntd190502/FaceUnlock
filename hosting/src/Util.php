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
    public static function error(string $code, int $status = 400, ?string $message=null): never { self::out(['ok'=>false,'error'=>['code'=>$code,'message'=>$message ?? $code]], $status); }
    public static function id(int $bytes = 18): string { return rtrim(strtr(base64_encode(random_bytes($bytes)), '+/', '-_'), '='); }
    public static function token(int $bytes = 32): string { return rtrim(strtr(base64_encode(random_bytes($bytes)), '+/', '-_'), '='); }
    public static function bearer(): ?string {
        $h = $_SERVER['HTTP_AUTHORIZATION'] ?? '';
        return preg_match('/^Bearer\s+(.+)$/i', $h, $m) ? trim($m[1]) : null;
    }
    public static function clientIpHash(): string { return hash('sha256', (string)($_SERVER['REMOTE_ADDR'] ?? 'unknown')); }
    public static function validId(string $id): bool { return (bool)preg_match('/^[A-Za-z0-9_-]{12,64}$/',$id); }
    public static function canonical(array $s): string {
        return 'faceunlock-v1|'.$s['id'].'|'.$s['challenge'].'|'.$s['pc_id'].'|'.$s['expires_at'];
    }
}
