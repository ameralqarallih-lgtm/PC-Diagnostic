-- Incident index schema v1 (incidents.db). This database is precious: it is never deleted or rebuilt automatically.

-- AUTOINCREMENT guarantees incident numbers are never reused, even after deletions.
CREATE TABLE Incidents (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  incident_key  TEXT NOT NULL UNIQUE,  -- INC-yyyyMMdd-nnnnn
  created_ts    INTEGER NOT NULL,
  center_ts     INTEGER NOT NULL,
  pre_ms        INTEGER NOT NULL,
  post_ms       INTEGER NOT NULL,
  state         INTEGER NOT NULL,      -- PcDiag.Core.Incidents.IncidentState
  source        INTEGER NOT NULL,      -- PcDiag.Core.Incidents.IncidentSource
  user_type     INTEGER,               -- PcDiag.Core.Incidents.UserIncidentType
  title         TEXT NOT NULL,
  user_notes    TEXT,
  severity      INTEGER NOT NULL,
  headline_json TEXT,                  -- summary numbers at center_ts
  folder        TEXT NOT NULL,         -- folder name under incidents/ (generated from the key)
  app_version   TEXT,
  pinned        INTEGER NOT NULL DEFAULT 0,
  snapshot_only INTEGER NOT NULL DEFAULT 0 -- captured while the recorder was not running (no pre-window)
);
CREATE INDEX IX_Incidents_center ON Incidents(center_ts);

CREATE TABLE IncidentTriggers (
  id          INTEGER PRIMARY KEY,
  incident_id INTEGER NOT NULL REFERENCES Incidents(id) ON DELETE CASCADE,
  ts          INTEGER NOT NULL,
  source      INTEGER NOT NULL,
  user_type   INTEGER,
  rule_id     TEXT,
  detail      TEXT,
  note        TEXT
);
CREATE INDEX IX_IncidentTriggers_incident ON IncidentTriggers(incident_id);
