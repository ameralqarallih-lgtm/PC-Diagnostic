-- Incident index schema v3 (Phase 9): a boot recovery is "processed" once the incident engine has decided on it
-- (with or without an incident; e.g. a normal restart creates none).
ALTER TABLE BootRecoveries ADD COLUMN processed_ts INTEGER;
DROP INDEX IF EXISTS IX_BootRecoveries_pending;
CREATE INDEX IX_BootRecoveries_unprocessed ON BootRecoveries(id) WHERE processed_ts IS NULL;
