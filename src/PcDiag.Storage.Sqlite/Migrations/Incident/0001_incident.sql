-- Per-incident additions (applied after the telemetry schema in incidents/<key>/incident.db).

-- Copy of the incident's metadata so the folder is self-describing when exported or moved.
CREATE TABLE IncidentInfo (
  key   TEXT PRIMARY KEY,
  value TEXT
);

-- The unified, ordered incident timeline.
CREATE TABLE IncidentEvents (
  id           INTEGER PRIMARY KEY,
  ts           INTEGER NOT NULL,
  kind         TEXT NOT NULL,          -- metric-change | anomaly | windows-event | process | service | display | user | trigger
  source_table TEXT,
  source_id    INTEGER,
  title        TEXT NOT NULL,
  detail_json  TEXT,
  importance   INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IX_IncidentEvents_ts ON IncidentEvents(ts);

-- Diagnostic summary statements.
CREATE TABLE Findings (
  id                 INTEGER PRIMARY KEY,
  ordinal            INTEGER NOT NULL,
  level              INTEGER NOT NULL, -- PcDiag.Core.Diagnostics.EvidenceLevel
  text               TEXT NOT NULL,
  evidence_refs_json TEXT
);
