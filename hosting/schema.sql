-- FaceUnlock Database Schema - MySQL/MariaDB
CREATE TABLE IF NOT EXISTS pcs (
  id VARCHAR(64) PRIMARY KEY,
  name VARCHAR(255) NOT NULL,
  public_key_pem TEXT NOT NULL,
  token_hash CHAR(64) NOT NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uq_pcs_token_hash (token_hash)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS devices (
  id VARCHAR(64) PRIMARY KEY,
  pc_id VARCHAR(64) NOT NULL,
  name VARCHAR(255) NOT NULL,
  public_key_pem TEXT NOT NULL,
  apns_token TEXT NULL,
  token_hash CHAR(64) NOT NULL,
  revoked_at TIMESTAMP NULL DEFAULT NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY idx_devices_pc (pc_id),
  KEY idx_devices_pc_revoked_created (pc_id, revoked_at, created_at),
  UNIQUE KEY uq_devices_token_hash (token_hash),
  CONSTRAINT fk_devices_pc FOREIGN KEY (pc_id) REFERENCES pcs(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS pairings (
  id VARCHAR(64) PRIMARY KEY,
  pc_id VARCHAR(64) NOT NULL,
  pair_code VARCHAR(16) NOT NULL,
  expires_at BIGINT NOT NULL,
  completed_device_id VARCHAR(64) NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY idx_pairings_pc (pc_id),
  CONSTRAINT fk_pairings_pc FOREIGN KEY (pc_id) REFERENCES pcs(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS unlock_sessions (
  id VARCHAR(64) PRIMARY KEY,
  pc_id VARCHAR(64) NOT NULL,
  device_id VARCHAR(64) NOT NULL,
  challenge VARCHAR(255) NOT NULL,
  expires_at BIGINT NOT NULL,
  status ENUM('PENDING','APPROVED','REJECTED','EXPIRED') NOT NULL DEFAULT 'PENDING',
  signature_b64 TEXT NULL,
  biometric VARCHAR(64) NULL,
  approved_at TIMESTAMP NULL DEFAULT NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY idx_sessions_pc (pc_id),
  KEY idx_sessions_device_status_exp (device_id, status, expires_at),
  KEY idx_sessions_created (created_at),
  CONSTRAINT fk_sessions_pc FOREIGN KEY (pc_id) REFERENCES pcs(id) ON DELETE CASCADE,
  CONSTRAINT fk_sessions_device FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
