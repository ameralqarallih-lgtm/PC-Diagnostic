-- Incident index schema v4 (Phase 10): which analyzer version produced an incident's findings, so incidents
-- analyzed by an older version (or never) are analyzed again.
ALTER TABLE Incidents ADD COLUMN analysis_version INTEGER;
