-- Telemetry schema v10: the service's process after a change (start, restart, install), so a moment maps services to processes.
ALTER TABLE ServiceEvents ADD COLUMN pid INTEGER;
