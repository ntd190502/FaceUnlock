-- FaceUnlock Hosting V2. Fresh installations use this complete MySQL/MariaDB schema.
CREATE TABLE IF NOT EXISTS schema_migrations (version VARCHAR(64) PRIMARY KEY, applied_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS pcs (id VARCHAR(64) PRIMARY KEY, name VARCHAR(255) NOT NULL, public_key_pem TEXT NOT NULL, token_hash CHAR(64) NOT NULL, created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, last_seen_at TIMESTAMP NULL, UNIQUE KEY uq_pcs_token_hash (token_hash)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS devices (id VARCHAR(64) PRIMARY KEY, stable_identity CHAR(64) NOT NULL, name VARCHAR(255) NOT NULL, public_key_pem TEXT NOT NULL, apns_token TEXT NULL, token_hash CHAR(64) NOT NULL, revoked_globally_at TIMESTAMP NULL, created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, last_seen_at TIMESTAMP NULL, UNIQUE KEY uq_devices_stable_identity (stable_identity), UNIQUE KEY uq_devices_token_hash (token_hash)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS pc_device_pairings (id VARCHAR(64) PRIMARY KEY, pc_id VARCHAR(64) NOT NULL, device_id VARCHAR(64) NOT NULL, status ENUM('ACTIVE','REVOKED') NOT NULL DEFAULT 'ACTIVE', nickname VARCHAR(255) NULL, paired_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, revoked_at TIMESTAMP NULL, last_used_at TIMESTAMP NULL, is_default BOOLEAN NOT NULL DEFAULT FALSE, created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP, UNIQUE KEY uq_pc_device_pairing(pc_id,device_id), KEY idx_pairing_pc_status(pc_id,status), KEY idx_pairing_device_status(device_id,status), CONSTRAINT fk_pairing_pc FOREIGN KEY(pc_id) REFERENCES pcs(id) ON DELETE RESTRICT, CONSTRAINT fk_pairing_device FOREIGN KEY(device_id) REFERENCES devices(id) ON DELETE RESTRICT) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS pairings (id VARCHAR(64) PRIMARY KEY, pc_id VARCHAR(64) NOT NULL, pair_code VARCHAR(16) NOT NULL, expires_at BIGINT NOT NULL, completed_device_id VARCHAR(64) NULL, created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, KEY idx_pairings_pc(pc_id), CONSTRAINT fk_pairings_pc FOREIGN KEY(pc_id) REFERENCES pcs(id) ON DELETE CASCADE) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS unlock_requests (id VARCHAR(64) PRIMARY KEY, pc_id VARCHAR(64) NOT NULL, challenge VARCHAR(255) NOT NULL, approval_token_hash CHAR(64) NULL, status ENUM('PENDING','APPROVED','REJECTED','EXPIRED','CANCELLED') NOT NULL DEFAULT 'PENDING', winning_device_id VARCHAR(64) NULL, winning_transport VARCHAR(32) NULL, signature_b64 TEXT NULL, biometric VARCHAR(64) NULL, created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, expires_at BIGINT NOT NULL, approved_at TIMESTAMP NULL, completed_at TIMESTAMP NULL, UNIQUE KEY uq_requests_approval_token_hash(approval_token_hash), KEY idx_requests_pc_status_exp(pc_id,status,expires_at), CONSTRAINT fk_requests_pc FOREIGN KEY(pc_id) REFERENCES pcs(id) ON DELETE RESTRICT, CONSTRAINT fk_requests_winner FOREIGN KEY(winning_device_id) REFERENCES devices(id) ON DELETE RESTRICT) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS unlock_request_candidates (request_id VARCHAR(64) NOT NULL, device_id VARCHAR(64) NOT NULL, state ENUM('PENDING','APPROVED','REJECTED','EXPIRED','LATE') NOT NULL DEFAULT 'PENDING', notified_at TIMESTAMP NULL, seen_at TIMESTAMP NULL, responded_at TIMESTAMP NULL, PRIMARY KEY(request_id,device_id), KEY idx_candidates_device_state(device_id,state), CONSTRAINT fk_candidates_request FOREIGN KEY(request_id) REFERENCES unlock_requests(id) ON DELETE CASCADE, CONSTRAINT fk_candidates_device FOREIGN KEY(device_id) REFERENCES devices(id) ON DELETE RESTRICT) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS rate_limit_events (bucket_hash CHAR(64) NOT NULL, bucket VARCHAR(64) NOT NULL, created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, KEY idx_rate_limit_bucket_time(bucket_hash,bucket,created_at)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS security_audit_log (id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY, occurred_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, event VARCHAR(64) NOT NULL, result VARCHAR(32) NOT NULL, pc_id VARCHAR(64) NULL, device_id VARCHAR(64) NULL, request_id VARCHAR(64) NULL, ip_hash CHAR(64) NULL, metadata_json JSON NULL, KEY idx_audit_time(occurred_at), KEY idx_audit_pc_time(pc_id,occurred_at), KEY idx_audit_device_time(device_id,occurred_at)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE IF NOT EXISTS remote_commands (
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
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
INSERT IGNORE INTO schema_migrations(version) VALUES ('001_initial_baseline'),('002_many_to_many_pairing'),('003_logical_unlock_requests'),('004_audit_security'),('005_remote_control');
