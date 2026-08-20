<?php
declare(strict_types=1);

final class ApprovalLink {
    private const TOKEN_BYTES = 32;

    public static function createToken(): string {
        return Util::token(self::TOKEN_BYTES);
    }

    public static function isValidToken(string $token): bool {
        return preg_match('/^[A-Za-z0-9_-]{43}$/D', $token) === 1;
    }

    public static function hashToken(string $token): string {
        return hash('sha256', $token);
    }

    public static function buildUrl(string $baseUrl, string $token): string {
        $baseUrl = rtrim(trim($baseUrl), '/');
        if (!self::isValidToken($token)) {
            throw new InvalidArgumentException('Invalid approval token');
        }

        $parts = parse_url($baseUrl);
        if (
            !is_array($parts) ||
            strtolower((string)($parts['scheme'] ?? '')) !== 'https' ||
            empty($parts['host']) ||
            isset($parts['user']) ||
            isset($parts['pass']) ||
            isset($parts['query']) ||
            isset($parts['fragment'])
        ) {
            throw new RuntimeException('base_url must be an HTTPS origin or HTTPS path without credentials, query, or fragment');
        }

        return $baseUrl . '/u/' . rawurlencode($token);
    }

    public static function state(?array $session, int $now): string {
        if ($session === null) {
            return 'INVALID';
        }

        $status = (string)($session['status'] ?? '');
        if ((int)($session['expires_at'] ?? 0) < $now) {
            return $status === 'PENDING' ? 'EXPIRED' : 'INVALID';
        }
        if ($status === 'APPROVED') {
            return 'COMPLETED';
        }
        if ($status !== 'PENDING') {
            return 'INVALID';
        }

        return 'VALID';
    }
}
