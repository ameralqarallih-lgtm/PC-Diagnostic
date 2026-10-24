-- Incident index schema v2 (Phase 8): what the Agent found at startup about a previous run that did not stop
-- cleanly. The incident engine (Phase 9) creates an incident for each row and sets incident_id.
CREATE TABLE BootRecoveries (
  id                INTEGER PRIMARY KEY,
  detected_ts       INTEGER NOT NULL,
  last_heartbeat_ts INTEGER NOT NULL,
  boot_ts           INTEGER NOT NULL,
  kind              INTEGER NOT NULL,      -- PcDiag.Core.Recording.UncleanStopKind
  was_recording     INTEGER NOT NULL,
  summary           TEXT NOT NULL,
  details_json      TEXT,
  incident_id       INTEGER REFERENCES Incidents(id) ON DELETE SET NULL
);
CREATE INDEX IX_BootRecoveries_pending ON BootRecoveries(incident_id) WHERE incident_id IS NULL;
