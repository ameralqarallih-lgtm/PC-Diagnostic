-- Telemetry schema v2 (Phase 3).
-- DXGI's per-adapter "budget" is per process (see docs/spikes/2026-09-28-gpu-apis.md), so the column holds the
-- adapter's total dedicated memory instead. D3DKMT reports power as a percentage of the board limit, not watts.
ALTER TABLE GpuMetrics RENAME COLUMN vram_budget_b TO vram_total_b;
ALTER TABLE GpuMetrics ADD COLUMN power_pct REAL;
