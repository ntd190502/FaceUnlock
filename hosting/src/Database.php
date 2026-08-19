<?php
final class Database {
    public PDO $pdo;
    public function __construct(array $cfg) {
        $this->pdo = new PDO($cfg['dsn'], $cfg['user'], $cfg['pass'], [
            PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
            PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
            PDO::ATTR_EMULATE_PREPARES => false,
        ]);
    }
    public function one(string $sql, array $params=[]): ?array {
        $s=$this->pdo->prepare($sql); $s->execute($params); $r=$s->fetch(); return $r ?: null;
    }
    public function exec(string $sql, array $params=[]): void { $s=$this->pdo->prepare($sql); $s->execute($params); }
}
