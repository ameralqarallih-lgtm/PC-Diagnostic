-- Telemetry schema v1. Shared by telemetry.db and every per-incident incident.db.
-- Conventions: ts = Unix epoch milliseconds (UTC); NULL = metric unavailable (reason in SensorAvailability);
-- telemetry tables are WITHOUT ROWID with a primary key starting at ts, so time-range scans are sequential.

-- Spine: one row per system tick, with headline values for dashboards and fast history queries.
CREATE TABLE SystemSnapshots (
  ts                INTEGER PRIMARY KEY,
  seq               INTEGER NOT NULL,
  cpu_pct           REAL,
  mem_used_pct      REAL,
  gpu_pct           REAL,
  vram_pct          REAL,
  disk_active_pct   REAL,
  net_bps           REAL,
  tick_ms           REAL,
  collectors_missed INTEGER,
  agent_cpu_pct     REAL,
  agent_ws_bytes    INTEGER
);

CREATE TABLE CpuMetrics (
  ts               INTEGER PRIMARY KEY,
  total_pct        REAL,
  user_pct         REAL,
  kernel_pct       REAL,
  freq_mhz         REAL,
  max_freq_mhz     REAL,
  perf_pct         REAL,
  perf_limit_pct   REAL,
  perf_limit_flags INTEGER,
  queue_len        REAL,
  ctx_switches_ps  REAL,
  interrupts_ps    REAL,
  package_temp_c   REAL,
  package_power_w  REAL,
  per_core_util    BLOB,   -- packed uint8 percent per logical processor
  per_core_mhz     BLOB    -- packed little-endian uint16 MHz per logical processor
) WITHOUT ROWID;

CREATE TABLE MemoryMetrics (
  ts              INTEGER PRIMARY KEY,
  total_b         INTEGER,
  available_b     INTEGER,
  free_b          INTEGER,
  cache_b         INTEGER,
  commit_b        INTEGER,
  commit_limit_b  INTEGER,
  paged_pool_b    INTEGER,
  nonpaged_pool_b INTEGER,
  compressed_b    INTEGER,
  page_faults_ps  REAL,
  hard_faults_ps  REAL,
  pages_ps        REAL,
  pressure        REAL
) WITHOUT ROWID;

CREATE TABLE Devices (
  device_id    INTEGER PRIMARY KEY,
  kind         TEXT NOT NULL,          -- gpu | disk | network | display | other
  stable_key   TEXT NOT NULL UNIQUE,   -- LUID / PnP id / interface GUID
  name         TEXT,
  vendor       TEXT,
  details_json TEXT,
  first_seen   INTEGER NOT NULL,
  last_seen    INTEGER NOT NULL
);

CREATE TABLE GpuMetrics (
  ts             INTEGER NOT NULL,
  adapter_id     INTEGER NOT NULL,     -- Devices.device_id
  util_pct       REAL,
  util_3d        REAL,
  util_compute   REAL,
  util_decode    REAL,
  util_encode    REAL,
  util_copy      REAL,
  vram_used_b    INTEGER,
  vram_budget_b  INTEGER,
  shared_used_b  INTEGER,
  temp_c         REAL,
  hotspot_c      REAL,
  core_mhz       REAL,
  mem_mhz        REAL,
  power_w        REAL,
  power_limit_w  REAL,
  throttle_flags INTEGER,
  fan_rpm        REAL,
  PRIMARY KEY (ts, adapter_id)
) WITHOUT ROWID;

CREATE TABLE DiskMetrics (
  ts           INTEGER NOT NULL,
  disk_id      INTEGER NOT NULL,       -- Devices.device_id
  read_bps     REAL,
  write_bps    REAL,
  read_iops    REAL,
  write_iops   REAL,
  read_lat_ms  REAL,
  write_lat_ms REAL,
  queue_len    REAL,
  active_pct   REAL,
  temp_c       REAL,
  PRIMARY KEY (ts, disk_id)
) WITHOUT ROWID;

CREATE TABLE NetworkMetrics (
  ts           INTEGER NOT NULL,
  if_id        INTEGER NOT NULL,       -- Devices.device_id
  rx_bps       REAL,
  tx_bps       REAL,
  rx_pps       REAL,
  tx_pps       REAL,
  rx_errors    INTEGER,                -- delta since previous tick
  tx_errors    INTEGER,
  rx_discards  INTEGER,
  tx_discards  INTEGER,
  link_bps     INTEGER,
  util_pct     REAL,
  oper_status  INTEGER,
  wifi_quality REAL,
  wifi_rssi    REAL,
  PRIMARY KEY (ts, if_id)
) WITHOUT ROWID;

-- Hardware sensors: providers expose hundreds of sensors, so readings are stored narrow.
CREATE TABLE SensorCatalog (
  sensor_id   INTEGER PRIMARY KEY,
  provider    TEXT NOT NULL,
  device_id   INTEGER,
  kind        TEXT NOT NULL,           -- Temperature | Clock | Power | Fan | Voltage | Load | Other
  name        TEXT NOT NULL,
  unit        TEXT NOT NULL,
  logical_key TEXT NOT NULL UNIQUE
);

CREATE TABLE SensorReadings (
  ts        INTEGER NOT NULL,
  sensor_id INTEGER NOT NULL,
  value     REAL,
  PRIMARY KEY (ts, sensor_id)
) WITHOUT ROWID;

-- Why a series is unavailable (one row per series, not per sample).
CREATE TABLE SensorAvailability (
  series       TEXT PRIMARY KEY,
  availability INTEGER NOT NULL,       -- PcDiag.Core.Telemetry.Availability
  reason       TEXT,
  since_ts     INTEGER NOT NULL
);

-- Extension point for future collectors (battery, USB, audio...) without schema changes.
CREATE TABLE ExtensionSeries (
  series_id INTEGER PRIMARY KEY,
  collector TEXT NOT NULL,
  name      TEXT NOT NULL,
  unit      TEXT,
  UNIQUE (collector, name)
);

CREATE TABLE ExtensionMetrics (
  ts        INTEGER NOT NULL,
  series_id INTEGER NOT NULL,
  value     REAL,
  PRIMARY KEY (ts, series_id)
) WITHOUT ROWID;

-- Processes: identity normalized once; per-tick samples reference it.
CREATE TABLE ProcessIdentity (
  proc_key   INTEGER PRIMARY KEY,      -- ProcessKey.ToStorageId(): hash of (pid, create time)
  pid        INTEGER NOT NULL,
  ppid       INTEGER,
  name       TEXT NOT NULL,
  path       TEXT,
  cmdline    TEXT,                     -- only when Privacy:CaptureCommandLines is enabled
  user       TEXT,                     -- only when Privacy:CaptureUserNames is enabled
  start_ts   INTEGER,
  end_ts     INTEGER,
  signer     TEXT,
  sig_status INTEGER,
  is_service INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IX_ProcessIdentity_name ON ProcessIdentity(name);

CREATE TABLE ProcessSnapshots (
  ts           INTEGER NOT NULL,
  proc_key     INTEGER NOT NULL,
  cpu_pct      REAL,
  cpu_time_ms  INTEGER,
  ws_b         INTEGER,
  private_b    INTEGER,
  commit_b     INTEGER,
  io_read_bps  REAL,
  io_write_bps REAL,
  gpu_pct      REAL,
  gpu_engine   TEXT,
  gpu_mem_b    INTEGER,
  threads      INTEGER,
  handles      INTEGER,
  priority     INTEGER,
  state        INTEGER,
  PRIMARY KEY (ts, proc_key)
) WITHOUT ROWID;

CREATE TABLE ProcessEvents (
  id          INTEGER PRIMARY KEY,
  ts          INTEGER NOT NULL,
  proc_key    INTEGER,
  kind        INTEGER NOT NULL,        -- PcDiag.Core.Events.ProcessEventKind
  detail_json TEXT
);
CREATE INDEX IX_ProcessEvents_ts ON ProcessEvents(ts);

-- Services: full snapshots are stored on startup and on reconcile; changes go to ServiceEvents.
CREATE TABLE ServiceSnapshots (
  ts           INTEGER NOT NULL,
  name         TEXT NOT NULL,
  display_name TEXT,
  status       INTEGER,
  start_type   INTEGER,
  pid          INTEGER,
  svc_type     INTEGER,
  PRIMARY KEY (ts, name)
) WITHOUT ROWID;

CREATE TABLE ServiceEvents (
  id        INTEGER PRIMARY KEY,
  ts        INTEGER NOT NULL,
  name      TEXT NOT NULL,
  kind      INTEGER NOT NULL,          -- PcDiag.Core.Events.ServiceEventKind
  old_value TEXT,
  new_value TEXT,
  event_ref INTEGER                    -- WindowsEvents.id, when derived from an event
);
CREATE INDEX IX_ServiceEvents_ts ON ServiceEvents(ts);

-- Snapshots stored only when their content hash changes.
CREATE TABLE HardwareSnapshots (
  id   INTEGER PRIMARY KEY,
  ts   INTEGER NOT NULL,
  hash TEXT NOT NULL,
  json TEXT NOT NULL
);
CREATE INDEX IX_HardwareSnapshots_ts ON HardwareSnapshots(ts);

CREATE TABLE DisplaySnapshots (
  id   INTEGER PRIMARY KEY,
  ts   INTEGER NOT NULL,
  hash TEXT NOT NULL,
  json TEXT NOT NULL
);
CREATE INDEX IX_DisplaySnapshots_ts ON DisplaySnapshots(ts);

CREATE TABLE DisplayEvents (
  id        INTEGER PRIMARY KEY,
  ts        INTEGER NOT NULL,
  kind      INTEGER NOT NULL,          -- PcDiag.Core.Events.DisplayEventKind
  target    TEXT,
  old_value TEXT,
  new_value TEXT
);
CREATE INDEX IX_DisplayEvents_ts ON DisplayEvents(ts);

CREATE TABLE Drivers (
  id           INTEGER PRIMARY KEY,
  snapshot_ts  INTEGER NOT NULL,
  device_class TEXT,
  device_name  TEXT,
  hardware_id  TEXT,
  provider     TEXT,
  version      TEXT,
  driver_date  TEXT,
  inf          TEXT,
  signer       TEXT
);
CREATE INDEX IX_Drivers_snapshot ON Drivers(snapshot_ts);

CREATE TABLE SystemInformation (
  key        TEXT PRIMARY KEY,
  value      TEXT,
  updated_ts INTEGER NOT NULL
);

CREATE TABLE WindowsEvents (
  id             INTEGER PRIMARY KEY,
  ts             INTEGER NOT NULL,
  channel        TEXT NOT NULL,
  record_id      INTEGER,
  provider       TEXT NOT NULL,
  event_id       INTEGER NOT NULL,
  level          INTEGER NOT NULL,
  category       INTEGER NOT NULL,     -- PcDiag.Core.Events.EventCategory
  severity       INTEGER NOT NULL,     -- PcDiag.Core.Events.Severity
  title          TEXT NOT NULL,
  message        TEXT,
  pid            INTEGER,
  process_name   TEXT,
  related_device TEXT,
  rule_id        TEXT,
  UNIQUE (channel, record_id)
);
CREATE INDEX IX_WindowsEvents_ts ON WindowsEvents(ts);
CREATE INDEX IX_WindowsEvents_category_ts ON WindowsEvents(category, ts);

CREATE TABLE Anomalies (
  id             INTEGER PRIMARY KEY,
  start_ts       INTEGER NOT NULL,
  end_ts         INTEGER,
  rule_id        TEXT NOT NULL,
  series         TEXT NOT NULL,
  observed       REAL,
  baseline_mean  REAL,
  baseline_sd    REAL,
  duration_ms    INTEGER,
  severity       INTEGER NOT NULL,
  reasoning_json TEXT NOT NULL,
  incident_key   TEXT
);
CREATE INDEX IX_Anomalies_start ON Anomalies(start_ts);

-- 1-minute min/avg/max rollups for long-range charts and trends.
CREATE TABLE RollupSeries (
  series_id INTEGER PRIMARY KEY,
  name      TEXT NOT NULL UNIQUE       -- e.g. "cpu/total", "gpu/<device_id>/util"
);

CREATE TABLE Rollup1m (
  ts        INTEGER NOT NULL,          -- start of the minute
  series_id INTEGER NOT NULL,
  min       REAL,
  avg       REAL,
  max       REAL,
  PRIMARY KEY (ts, series_id)
) WITHOUT ROWID;

-- Crash / BSOD detection: last heartbeat and whether the previous run shut down cleanly.
CREATE TABLE AgentHeartbeat (
  id             INTEGER PRIMARY KEY CHECK (id = 1),
  last_ts        INTEGER NOT NULL,
  boot_id        TEXT,
  clean_shutdown INTEGER NOT NULL
);

-- Periods the recorder was recording, so charts show gaps instead of interpolating across them.
CREATE TABLE RecorderSessions (
  id           INTEGER PRIMARY KEY,
  start_ts     INTEGER NOT NULL,
  end_ts       INTEGER,
  start_reason INTEGER NOT NULL,
  end_reason   INTEGER,
  elevated     INTEGER NOT NULL,
  app_version  TEXT
);
CREATE INDEX IX_RecorderSessions_start ON RecorderSessions(start_ts);
