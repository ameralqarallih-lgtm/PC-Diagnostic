-- Telemetry schema v7 (Phase 10): anomalies name the device a per-device rule fired for (a GPU, disk or network
-- adapter), and open anomalies (still running, or left open by a crash) can be found quickly.
ALTER TABLE Anomalies ADD COLUMN device TEXT;
CREATE INDEX IX_Anomalies_open ON Anomalies(id) WHERE end_ts IS NULL;
