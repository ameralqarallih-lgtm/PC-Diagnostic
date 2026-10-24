-- Telemetry schema v5 (Phase 7b): display changes keep the display's stable key (monitor PnP instance ID), so
-- events can be matched to DisplaySnapshots entries even when the friendly name is missing or shared.
ALTER TABLE DisplayEvents ADD COLUMN target_key TEXT;
