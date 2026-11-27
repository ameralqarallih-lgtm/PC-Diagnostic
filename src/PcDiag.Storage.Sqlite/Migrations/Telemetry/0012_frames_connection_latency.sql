-- Telemetry schema v12: game frame rates, the connection's quality and driver latency.

-- Drivers holding the processors: DPC and interrupt (ISR) time of all processors, and of the busiest one.
ALTER TABLE CpuMetrics ADD COLUMN dpc_pct REAL;
ALTER TABLE CpuMetrics ADD COLUMN isr_pct REAL;
ALTER TABLE CpuMetrics ADD COLUMN dpcs_ps REAL;
ALTER TABLE CpuMetrics ADD COLUMN dpc_core_pct REAL;
ALTER TABLE CpuMetrics ADD COLUMN isr_core_pct REAL;
ALTER TABLE CpuMetrics ADD COLUMN core_max_pct REAL;    -- the busiest processor's load (a game held by one thread shows here)

-- TCP segments a process sent again per second (lost or late acknowledgements).
ALTER TABLE ProcessSnapshots ADD COLUMN net_retrans_ps REAL;

-- The game in front, each second while recording.
CREATE TABLE FrameMetrics (
  ts          INTEGER NOT NULL,
  pid         INTEGER NOT NULL,
  game        TEXT,
  fps         REAL NOT NULL,
  frame_ms    REAL,
  worst_ms    REAL,                    -- the longest frame of the second
  stutters    INTEGER NOT NULL,        -- frames far longer than the game's usual frame
  low1_fps    REAL,                    -- 1 % low over 30 s
  normal_fps  REAL                     -- the game's usual frame rate (median of 2 min)
);
CREATE INDEX IX_FrameMetrics_ts ON FrameMetrics(ts);

-- Pings each second while recording: 100 in *_loss when the ping was lost, 0 when it came back, NULL when none was sent.
CREATE TABLE ConnectionMetrics (
  ts            INTEGER NOT NULL,
  router_ms     REAL,
  router_loss   REAL,
  provider_ms   REAL,
  provider_loss REAL,
  internet_ms   REAL,
  internet_loss REAL,
  dns_ms        REAL,                  -- only in seconds with a DNS check
  dns_failed    INTEGER,
  provider_addr TEXT
);
CREATE INDEX IX_ConnectionMetrics_ts ON ConnectionMetrics(ts);

-- Driver latency measurements: one row per driver ('' for a measurement without drivers; its problem says why).
CREATE TABLE DriverLatency (
  ts           INTEGER NOT NULL,       -- when the measurement ended
  from_ts      INTEGER NOT NULL,
  seconds      REAL NOT NULL,
  reason       TEXT NOT NULL,          -- measure | dpc | stutter
  driver       TEXT NOT NULL,
  dpcs         INTEGER,
  dpc_max_us   REAL,
  dpc_total_us REAL,
  isrs         INTEGER,
  isr_max_us   REAL,
  isr_total_us REAL,
  over_500us   INTEGER,
  over_1ms     INTEGER,
  problem      TEXT
);
CREATE UNIQUE INDEX UX_DriverLatency ON DriverLatency(ts, driver);

-- The quality history, kept also while the recorder only watches: one row per minute…
CREATE TABLE QualityMinutes (
  ts              INTEGER PRIMARY KEY, -- the minute's start
  seconds         INTEGER NOT NULL,    -- samples in it
  game            TEXT,                -- the game in front most of the minute
  game_seconds    INTEGER,
  fps_avg         REAL,
  fps_min         REAL,
  low1_fps        REAL,
  stutters        INTEGER,
  worst_ms        REAL,
  drop_seconds    INTEGER,             -- seconds 40 % or more below the game's usual frame rate
  dpc_core_max    REAL,                -- the busiest processor's DPC time, highest second
  isr_core_max    REAL,
  dpc_avg         REAL,                -- all processors' DPC time, average
  router_ms       REAL,
  router_max      REAL,
  router_loss     REAL,                -- % of pings lost
  provider_ms     REAL,
  provider_max    REAL,
  provider_loss   REAL,
  internet_ms     REAL,
  internet_max    REAL,
  internet_jitter REAL,                -- average change between consecutive round trips
  internet_loss   REAL,
  dns_ms          REAL,
  dns_checks      INTEGER,
  dns_failed      INTEGER,
  wifi_min        REAL,                -- the weakest Wi-Fi signal (%)
  net_bps         REAL                 -- all adapters' traffic, average
);

-- …and one row per game session (a game in front, with gaps under a minute).
CREATE TABLE GameSessions (
  id            INTEGER PRIMARY KEY,
  game          TEXT NOT NULL,
  start_ts      INTEGER NOT NULL,
  end_ts        INTEGER NOT NULL,
  seconds       INTEGER NOT NULL,
  fps_avg       REAL,
  fps_p1        REAL,                  -- the slowest 1 % of its seconds
  low1_fps      REAL,                  -- median of its 1 % lows
  stutters      INTEGER,
  drop_seconds  INTEGER,
  worst_ms      REAL
);
CREATE INDEX IX_GameSessions_start ON GameSessions(start_ts);
