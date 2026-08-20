<?php
declare(strict_types=1);
final class Migrator {
    public function __construct(private Database $db) {}
    public function migrate(): void {
        $this->db->pdo->exec('CREATE TABLE IF NOT EXISTS schema_migrations(version VARCHAR(64) PRIMARY KEY, applied_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP) ENGINE=InnoDB');
        $done=array_column($this->db->pdo->query('SELECT version FROM schema_migrations')->fetchAll(),'version');
        foreach(glob(dirname(__DIR__).'/migrations/*.php') as $file){$v=basename($file,'.php');if(in_array($v,$done,true))continue;$this->db->transaction(function()use($file,$v){(require $file)($this->db);$this->db->exec('INSERT INTO schema_migrations(version) VALUES(?)',[$v]);});}
    }
}
