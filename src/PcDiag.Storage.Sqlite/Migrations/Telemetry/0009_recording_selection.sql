-- Telemetry schema v9 (Phase 16): what a custom recording stored (JSON array of item IDs); NULL records everything.
ALTER TABLE RecorderSessions ADD COLUMN selection_json TEXT;
