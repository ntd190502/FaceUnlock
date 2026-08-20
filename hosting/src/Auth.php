<?php
final class Auth {
    public function __construct(private Database $db) {}
    public function pc(): array {
        $token = Util::bearer(); if (!$token) Util::error('missing_bearer', 401);
        $hash = hash('sha256', $token);
        $row = $this->db->one('SELECT * FROM pcs WHERE token_hash=?', [$hash]);
        if (!$row) Util::error('invalid_token', 401); return $row;
    }
    public function device(): array {
        $token = Util::bearer(); if (!$token) Util::error('missing_bearer', 401);
        $hash = hash('sha256', $token);
        $row = $this->db->one('SELECT * FROM devices WHERE token_hash=? AND revoked_globally_at IS NULL', [$hash]);
        if (!$row) Util::error('invalid_token', 401); return $row;
    }
}
