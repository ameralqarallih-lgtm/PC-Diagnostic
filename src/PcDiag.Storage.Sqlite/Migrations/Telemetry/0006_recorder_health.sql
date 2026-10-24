-- Telemetry schema v6 (Phase 8): heartbeat records whether the recorder was recording (boot recovery only
-- concerns recorded periods), and indexes for time-sliced retention deletes.
ALTER TABLE AgentHeartbeat ADD COLUMN recording INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IX_ProcessIdentity_end ON ProcessIdentity(end_ts) WHERE end_ts IS NOT NULL;
CREATE INDEX IX_ProcessIdentity_start ON ProcessIdentity(start_ts);
CREATE INDEX IX_RecorderSessions_end ON RecorderSessions(end_ts);
