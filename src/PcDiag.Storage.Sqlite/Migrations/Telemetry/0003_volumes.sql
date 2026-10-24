-- Telemetry schema v3 (Phase 4): volume capacity and free space.
-- Written only when free space changes by at least 1 MB or once a minute, not every tick.
CREATE TABLE VolumeMetrics (
  ts          INTEGER NOT NULL,
  mount       TEXT NOT NULL,           -- e.g. "C:\"
  disk_id     INTEGER,                 -- Devices.device_id of the physical disk, when known
  total_b     INTEGER,
  free_b      INTEGER,
  label       TEXT,
  file_system TEXT,
  drive_type  TEXT,
  PRIMARY KEY (ts, mount)
) WITHOUT ROWID;
