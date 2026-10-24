-- Telemetry schema v8 (Phase 14): true per-process disk and network rates from kernel events (Enhanced mode).
ALTER TABLE ProcessSnapshots ADD COLUMN disk_read_bps REAL;
ALTER TABLE ProcessSnapshots ADD COLUMN disk_write_bps REAL;
ALTER TABLE ProcessSnapshots ADD COLUMN net_rx_bps REAL;
ALTER TABLE ProcessSnapshots ADD COLUMN net_tx_bps REAL;
