<?php
declare(strict_types=1);
$config=require dirname(__DIR__).'/config.php';
require dirname(__DIR__).'/src/Database.php';
$db=new Database($config['db']);
$db->exec("CREATE TABLE IF NOT EXISTS transfer_files (id VARCHAR(64) PRIMARY KEY, pc_id VARCHAR(64) NOT NULL, device_id VARCHAR(64) NULL, direction ENUM('PC_TO_IPHONE','IPHONE_TO_PC') NOT NULL, original_name VARCHAR(255) NOT NULL, stored_name VARCHAR(255) NOT NULL, size_bytes BIGINT UNSIGNED NOT NULL, mime_type VARCHAR(255) NULL, status ENUM('READY','CLAIMED','DONE') NOT NULL DEFAULT 'READY', created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, completed_at TIMESTAMP NULL, KEY idx_transfer_pc_direction(pc_id,direction,status,created_at), CONSTRAINT fk_transfer_pc FOREIGN KEY(pc_id) REFERENCES pcs(id) ON DELETE CASCADE, CONSTRAINT fk_transfer_device FOREIGN KEY(device_id) REFERENCES devices(id) ON DELETE SET NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
$db->exec("INSERT IGNORE INTO schema_migrations(version) VALUES('006_hosted_file_transfer')");
echo "006_hosted_file_transfer applied\n";
