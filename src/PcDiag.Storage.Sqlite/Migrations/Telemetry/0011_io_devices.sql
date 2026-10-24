-- Telemetry schema v11: the device register. Every I/O device the inventory has seen, connected now or before (Windows
-- keeps the nodes of earlier connections), with its parts, driver and dates; devices of the PC itself only when they
-- have a problem. One row per physical device, updated when the register changes.
CREATE TABLE IoDevices (
  device_key    TEXT PRIMARY KEY,      -- "id:VID:PID", "bt:address", "display:model", "container:id", else the instance ID
  name          TEXT NOT NULL,
  kind          TEXT NOT NULL,         -- keyboard | mouse | game-controller | audio | camera | display | storage | ... | system
  bus           TEXT,
  manufacturer  TEXT,
  present       INTEGER NOT NULL,      -- connected at the newest inventory
  external      INTEGER NOT NULL,      -- attached rather than part of the PC
  technical     INTEGER NOT NULL,      -- plumbing (hubs, virtual devices), hidden by default
  problem_code  INTEGER NOT NULL DEFAULT 0,
  details_json  TEXT NOT NULL,         -- the whole device (IoDevice) as JSON
  first_seen    INTEGER NOT NULL,      -- the first inventory that listed it (ms)
  last_seen     INTEGER NOT NULL,      -- the newest inventory that found it connected, or noticed it gone (ms)
  updated       INTEGER NOT NULL       -- the newest inventory that listed it (ms)
);
CREATE INDEX IX_IoDevices_present ON IoDevices(present, kind);
