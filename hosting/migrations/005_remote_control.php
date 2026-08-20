<?php
declare(strict_types=1);
return static function(PDO $pdo): void {
    $pdo->exec("CREATE TABLE IF NOT EXISTS remote_commands (
      id VARCHAR(64) PRIMARY KEY,
      pc_id VARCHAR(64) NOT NULL,
      device_id VARCHAR(64) NOT NULL,
      command_type VARCHAR(40) NOT NULL,
      payload MEDIUMTEXT NULL,
      result MEDIUMTEXT NULL,
      status ENUM('PENDING','RUNNING','DONE','ERROR','EXPIRED') NOT NULL DEFAULT 'PENDING',
      created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
      claimed_at TIMESTAMP NULL,
      completed_at TIMESTAMP NULL,
      expires_at BIGINT NOT NULL,
      INDEX idx_remote_pc_status (pc_id,status,created_at),
      INDEX idx_remote_device (device_id,created_at),
      CONSTRAINT fk_remote_pc FOREIGN KEY (pc_id) REFERENCES pcs(id) ON DELETE CASCADE,
      CONSTRAINT fk_remote_device FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
};
