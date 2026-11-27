-- Telemetry schema v13: the frames that reached the screen (PresentMon), each frame's GPU and CPU share, how Windows shows
-- them, and how a game was recognized (it may be windowed).
ALTER TABLE FrameMetrics ADD COLUMN displayed_fps REAL;       -- frames shown on the screen in the second
ALTER TABLE FrameMetrics ADD COLUMN dropped INTEGER;          -- frames presented and never shown
ALTER TABLE FrameMetrics ADD COLUMN worst_displayed_ms REAL;  -- the longest time one image stayed on the screen
ALTER TABLE FrameMetrics ADD COLUMN display_latency_ms REAL;  -- from a frame's start until it was shown, average
ALTER TABLE FrameMetrics ADD COLUMN gpu_busy_pct REAL;        -- the GPU's work as a share of the frames' time
ALTER TABLE FrameMetrics ADD COLUMN cpu_busy_pct REAL;        -- the CPU's work before presenting, as a share
ALTER TABLE FrameMetrics ADD COLUMN present_mode TEXT;        -- e.g. "Hardware: Independent Flip", "Composed: Flip"
ALTER TABLE FrameMetrics ADD COLUMN windowed INTEGER;         -- 1 when the game did not cover its screen

ALTER TABLE QualityMinutes ADD COLUMN displayed_fps REAL;
ALTER TABLE QualityMinutes ADD COLUMN dropped INTEGER;
ALTER TABLE QualityMinutes ADD COLUMN display_latency_ms REAL;
ALTER TABLE QualityMinutes ADD COLUMN gpu_busy_pct REAL;
ALTER TABLE QualityMinutes ADD COLUMN cpu_busy_pct REAL;
ALTER TABLE QualityMinutes ADD COLUMN present_mode TEXT;

ALTER TABLE GameSessions ADD COLUMN displayed_fps REAL;
ALTER TABLE GameSessions ADD COLUMN dropped INTEGER;
ALTER TABLE GameSessions ADD COLUMN display_latency_ms REAL;
ALTER TABLE GameSessions ADD COLUMN gpu_busy_pct REAL;
ALTER TABLE GameSessions ADD COLUMN cpu_busy_pct REAL;
ALTER TABLE GameSessions ADD COLUMN present_mode TEXT;
ALTER TABLE GameSessions ADD COLUMN windowed INTEGER;         -- share of its seconds windowed (0-100)
ALTER TABLE GameSessions ADD COLUMN recognized TEXT;          -- full screen | windows | library | graphics | user
