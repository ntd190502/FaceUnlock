-- Apply once when upgrading an existing FaceUnlock database.
ALTER TABLE unlock_sessions
  ADD COLUMN approval_token_hash CHAR(64) NULL AFTER challenge,
  ADD UNIQUE KEY uq_sessions_approval_token_hash (approval_token_hash),
  MODIFY COLUMN status ENUM('PENDING','APPROVED','REJECTED','EXPIRED','CANCELLED') NOT NULL DEFAULT 'PENDING';
