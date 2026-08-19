<?php
final class Crypto {
    public static function verify(string $publicKeyPem, string $message, string $signatureB64): bool {
        $sig = base64_decode($signatureB64, true); if ($sig === false) return false;
        $key = openssl_pkey_get_public($publicKeyPem); if (!$key) return false;
        return openssl_verify($message, $sig, $key, OPENSSL_ALGO_SHA256) === 1;
    }
}
